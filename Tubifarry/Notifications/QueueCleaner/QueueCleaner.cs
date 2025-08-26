using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using System.IO.Abstractions;
using Tubifarry.Core.Utilities;
using Tubifarry.Notifications.Queue;

namespace Tubifarry.Notifications.QueueCleaner
{
    public class QueueCleaner : NotificationBase<QueueCleanerSettings>
    {
        private readonly Logger _logger;
        private readonly IDiskProvider _diskProvider;
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly INamingConfigService _namingConfig;
        private readonly IIndexerFactory _indexerFactory;

        public override string Name => "Queue Cleaner";

        public override string Link => "";

        public override ProviderMessage Message => new("Queue Cleaner automatically processes items that failed to import. It can rename, blocklist, or remove items based on your settings.", ProviderMessageType.Info);

        public QueueCleaner(IDiskProvider diskProvider, IHistoryService historyService, INamingConfigService namingConfig, IEventAggregator eventAggregator, IIndexerFactory indexerFactory, ICompletedDownloadService completedDownloadService, Logger logger)
        {
            _logger = logger;
            _indexerFactory = indexerFactory;
            _diskProvider = diskProvider;
            _eventAggregator = eventAggregator;
            _completedDownloadService = completedDownloadService;
            _historyService = historyService;
            _namingConfig = namingConfig;
        }

        public IFileInfo[] GetAudioFiles(string path, bool allDirectories = true)
        {
            List<IFileInfo> filesOnDisk = _diskProvider.GetFileInfos(path, allDirectories);
            return filesOnDisk.Where(file => MediaFileExtensions.Extensions.Contains(file.Extension)).ToArray();
        }

        internal void CleanImport(AlbumImportIncompleteEvent message)
        {
            TrackedDownload trackedDownload = message.TrackedDownload;

            if (!trackedDownload.IsTrackable || trackedDownload.State != TrackedDownloadState.ImportFailed || !trackedDownload.DownloadItem.CanMoveFiles)
                return;

            if (Settings.Indexers?.Any() == true && !Settings.Indexers.Contains(trackedDownload.Indexer))
            {
                _logger.Debug($"Skipping queue cleaning for download '{trackedDownload.DownloadItem.Title}'. Indexer '{trackedDownload.Indexer}' is not in list");
                return;
            }

            switch (CheckImport(trackedDownload))
            {
                case ImportFailureReason.FailedBecauseOfMissingTracks:
                    HandleFailure(trackedDownload, Settings.ImportCleaningOption, ImportCleaningOptions.WhenMissingTracks);
                    break;

                case ImportFailureReason.FailedBecauseOfInsufficientInformation:
                    HandleFailure(trackedDownload, Settings.ImportCleaningOption, ImportCleaningOptions.WhenAlbumInfoIncomplete);
                    break;

                case ImportFailureReason.Both:
                    if (Settings.ImportCleaningOption == (int)ImportCleaningOptions.Disabled)
                        break;
                    HandleFailure(trackedDownload, Settings.ImportCleaningOption, ImportCleaningOptions.Always);
                    break;

                case ImportFailureReason.DidNotFail:
                    break;
            }
        }

        private void HandleFailure(TrackedDownload trackedDownload, int importCleaningOption, ImportCleaningOptions requiredOption)
        {
            if (importCleaningOption != (int)requiredOption && importCleaningOption != (int)ImportCleaningOptions.Always)
                return;


            if (Settings.RenameOption != (int)RenameOptions.DoNotRename && Rename(trackedDownload))
            {
                Retry(trackedDownload);
                return;
            }

            if (Settings.BlocklistOption == (int)BlocklistOptions.RemoveAndBlocklist || Settings.BlocklistOption == (int)BlocklistOptions.BlocklistOnly)
                Blocklist(trackedDownload);

            if (Settings.BlocklistOption == (int)BlocklistOptions.RemoveAndBlocklist || Settings.BlocklistOption == (int)BlocklistOptions.RemoveOnly)
                Remove(trackedDownload);
        }

        private void Remove(TrackedDownload item)
        {
            if (!item.DownloadItem.CanBeRemoved)
                return;
            foreach (IFileInfo file in (List<IFileInfo>)_diskProvider.GetFileInfos(item.DownloadItem.OutputPath.FullPath, true))
                _diskProvider.DeleteFile(file.FullName);
            _eventAggregator.PublishEvent(new DownloadCanBeRemovedEvent(item));
        }

        private bool Rename(TrackedDownload item)
        {
            if (!item.DownloadItem.CanMoveFiles)
            {
                _logger.Debug("Skipping rename: Download item cannot be moved.");
                return false;
            }

            List<IFileInfo> filesOnDisk = _diskProvider.GetFileInfos(item.DownloadItem.OutputPath.FullPath, true).ToList();
            HashSet<string> audioExtensions = new(MediaFileExtensions.Extensions, StringComparer.OrdinalIgnoreCase);
            ReleaseFormatter releaseFormatter = new(item.RemoteAlbum.Release, item.RemoteAlbum.Artist, _namingConfig.GetConfig());

            return filesOnDisk.Any(file => TryRenameFile(file, audioExtensions, releaseFormatter, filesOnDisk));
        }

        private bool TryRenameFile(IFileInfo file, HashSet<string> audioExtensions, ReleaseFormatter releaseFormatter, List<IFileInfo> filesOnDisk)
        {
            string filePath = file.FullName;
            string? directory = Path.GetDirectoryName(filePath);
            if (directory == null || !audioExtensions.Contains(Path.GetExtension(filePath)))
            {
                _logger.Warn($"Unable to determine directory for file: {filePath}");
                return false;
            }

            try
            {
                TagLib.File fileTags = TagLib.File.Create(filePath);
                string artistName = fileTags.Tag.FirstAlbumArtist ?? fileTags.Tag.FirstPerformer ?? "UnknownArtist";
                string title = fileTags.Tag.Title ?? Path.GetFileNameWithoutExtension(filePath);
                string trackNumber = fileTags.Tag.Track.ToString("D2");

                string newFileNameWithoutExtension = releaseFormatter.BuildTrackFilename(null, new Track
                {
                    Title = title,
                    TrackNumber = trackNumber,
                    Artist = new Artist { Name = artistName }
                }, new Album { Title = title });

                string newFileName = $"{newFileNameWithoutExtension}{Path.GetExtension(filePath)}";
                string newFilePath = Path.Combine(directory, newFileName);

                if (!newFilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) && MoveFile(filePath, newFilePath))
                {
                    string oldBaseName = Path.GetFileNameWithoutExtension(filePath);
                    filesOnDisk.Where(f => !Path.GetExtension(f.FullName).Equals(Path.GetExtension(filePath), StringComparison.OrdinalIgnoreCase) &&
                                          Path.GetFileNameWithoutExtension(f.FullName).Equals(oldBaseName, StringComparison.OrdinalIgnoreCase)).ToList()
                              .ForEach(associatedFile => MoveFile(associatedFile.FullName, Path.Combine(directory, $"{newFileNameWithoutExtension}{Path.GetExtension(associatedFile.FullName)}")));
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to parse or rename file: {filePath}");
            }
            return false;
        }

        private bool MoveFile(string sourcePath, string destinationPath)
        {
            if (!_diskProvider.FileExists(sourcePath)) return false;
            try
            {
                _diskProvider.MoveFile(sourcePath, destinationPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to rename file: {sourcePath}");
                return false;
            }
        }

        private void Blocklist(TrackedDownload item)
        {
            item.State = TrackedDownloadState.DownloadFailed;

            List<EntityHistory> grabbedItems = _historyService.Find(item.DownloadItem.DownloadId, EntityHistoryEventType.Grabbed).ToList();
            EntityHistory historyItem = grabbedItems[^1];

            _ = Enum.TryParse(historyItem.Data.GetValueOrDefault(EntityHistory.RELEASE_SOURCE, ReleaseSourceType.Unknown.ToString()), out ReleaseSourceType releaseSource);

            DownloadFailedEvent downloadFailedEvent = new()
            {
                ArtistId = historyItem.ArtistId,
                AlbumIds = grabbedItems.Select(h => h.AlbumId).Distinct().ToList(),
                Quality = historyItem.Quality,
                SourceTitle = historyItem.SourceTitle,
                DownloadClient = historyItem.Data.GetValueOrDefault(EntityHistory.DOWNLOAD_CLIENT),
                DownloadId = historyItem.DownloadId,
                Message = "Import failed: Item removed by Queue Cleaner.",
                Data = historyItem.Data,
                TrackedDownload = item,
                SkipRedownload = !Settings.RetryFindingRelease,
                ReleaseSource = releaseSource
            };
            _eventAggregator.PublishEvent(downloadFailedEvent);
        }

        private void Retry(TrackedDownload item)
        {
            item.State = TrackedDownloadState.ImportPending;
            _completedDownloadService.Import(item);
        }

        private static ImportFailureReason CheckImport(TrackedDownload trackedDownload)
        {
            if (trackedDownload.State != TrackedDownloadState.ImportFailed)
                return ImportFailureReason.DidNotFail;

            bool hasMissingTracks = trackedDownload.StatusMessages
                .Any(sm => sm.Messages.Any(m => m.Contains("Has missing tracks", StringComparison.OrdinalIgnoreCase)));

            bool hasInsufficientInformation = trackedDownload.StatusMessages
                .Any(sm => sm.Messages.Any(m => m.Contains("Album match is not close enough", StringComparison.OrdinalIgnoreCase)));

            return (hasMissingTracks, hasInsufficientInformation) switch
            {
                (true, true) => ImportFailureReason.Both,
                (true, _) => ImportFailureReason.FailedBecauseOfMissingTracks,
                (_, true) => ImportFailureReason.FailedBecauseOfInsufficientInformation,
                _ => ImportFailureReason.DidNotFail
            };
        }

        public override void OnImportFailure(AlbumDownloadMessage message) => base.OnImportFailure(message);

        public override ValidationResult Test()
        {
            ValidationResult result = new();
            IEnumerable<string> validProviders = _indexerFactory.GetAvailableProviders().Select(x => x.Name);

            if (Settings.Indexers?.Any() == true)
            {
                List<string> invalidProviders = Settings.Indexers
                    .Where(indexer => !validProviders.Contains(indexer, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (invalidProviders.Any())
                {
                    string errorMessage = $"The following indexers are not valid or available: {string.Join(", ", invalidProviders)}";
                    result.Errors.Add(new ValidationFailure(nameof(Settings.Indexers), errorMessage, Settings.Indexers));
                }
            }

            return result;
        }

        public enum ImportFailureReason
        {
            DidNotFail,
            Both,
            FailedBecauseOfMissingTracks,
            FailedBecauseOfInsufficientInformation
        }
    }
}