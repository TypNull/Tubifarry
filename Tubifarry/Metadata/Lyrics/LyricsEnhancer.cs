using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using Tubifarry.Core.Records;
using Tubifarry.Metadata.ScheduledTasks;

namespace Tubifarry.Metadata.Lyrics
{
    public class LyricsEnhancer : ScheduledTaskBase<LyricsEnhancerSettings>, IExecute<LyricsUpdateCommand>
    {
        private readonly Logger _logger;
        private readonly HttpClient _httpClient;
        private readonly IRootFolderWatchingService _rootFolderWatchingService;
        private readonly IArtistService _artistService;
        private readonly IDiskProvider _diskProvider;
        private readonly TrackFileRepositoryHelper _trackFileRepositoryHelper;

        private LyricsProviders _lyricsProviders;

        // Batch size for SQL LIMIT/OFFSET pagination
        private const int SQL_BATCH_SIZE = 500;

        public LyricsEnhancer(
            HttpClient httpClient,
            Logger logger,
            IRootFolderWatchingService rootFolderWatchingService,
            ILyricFileService lyricFileService,
            IArtistService artistService,
            IDiskProvider diskProvider,
            IMainDatabase database,
            IEventAggregator eventAggregator,
            ITrackRepository trackRepository)
        {
            _logger = logger;
            _httpClient = httpClient;
            _rootFolderWatchingService = rootFolderWatchingService;
            _lyricsProviders = new LyricsProviders(httpClient, logger, ActiveSettings);
            _artistService = artistService;
            _diskProvider = diskProvider;
            _trackFileRepositoryHelper = new TrackFileRepositoryHelper(database, eventAggregator, trackRepository, lyricFileService, logger);
        }

        public override string Name => "Lyrics Enhancer";
        public override Type CommandType => typeof(LyricsUpdateCommand);

        public override int IntervalMinutes => ActiveSettings.EnableScheduledUpdates
            ? (int)TimeSpan.FromDays(ActiveSettings.UpdateInterval).TotalMinutes
            : 0;

        private LyricsEnhancerSettings ActiveSettings => Settings ?? LyricsEnhancerSettings.Instance!;
        public override CommandPriority Priority => CommandPriority.Low;

        public void Execute(LyricsUpdateCommand message)
        {
            if (!ActiveSettings.EnableScheduledUpdates)
            {
                _logger.Debug("Scheduled lyrics updates are disabled in settings");
                message.SetCompletionMessage("Lyrics updates are disabled");
                return;
            }

            try
            {
                _lyricsProviders = new LyricsProviders(_httpClient, _logger, ActiveSettings);
                _logger.ProgressInfo("Starting scheduled lyrics update");

                int totalTracks = _trackFileRepositoryHelper.GetTracksWithoutLrcFilesCount();

                if (totalTracks == 0)
                {
                    _logger.Info("All tracks in database have lyric file entries");
                    message.SetCompletionMessage("All tracks have lyrics entries");
                    return;
                }

                _logger.Debug($"Found {totalTracks} tracks without lyric entries in database");

                ProcessingResult totalResult = new();
                int processedCount = 0;

                for (int offset = 0; offset < totalTracks; offset += SQL_BATCH_SIZE)
                {
                    List<TrackFile> batch = _trackFileRepositoryHelper.GetTracksWithoutLrcFilesBatch(offset, SQL_BATCH_SIZE);

                    if (batch.Count == 0)
                        break;

                    _logger.Debug($"Processing SQL batch {(offset / SQL_BATCH_SIZE) + 1} (offset {offset}, {batch.Count} tracks)");

                    ProcessingResult batchResult = ProcessTrackBatch(batch);

                    totalResult.SuccessCount += batchResult.SuccessCount;
                    totalResult.SyncedCount += batchResult.SyncedCount;
                    totalResult.FailedCount += batchResult.FailedCount;

                    processedCount += batch.Count;
                    _logger.Debug($"Progress: {processedCount}/{totalTracks} tracks without lyric processed");
                }

                string completionMsg = $"Lyrics update completed: {totalResult.SuccessCount} created, " +
                                      $"{totalResult.SyncedCount} synced, " +
                                      $"{totalResult.FailedCount} not found.";
                _logger.Info(completionMsg);
                message.SetCompletionMessage(completionMsg);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during scheduled lyrics update execution");
                message.SetCompletionMessage($"Lyrics update failed: {ex.Message}");
            }
        }

        private ProcessingResult ProcessTrackBatch(List<TrackFile> batch)
        {
            ProcessingResult result = new();

            foreach (TrackFile trackFile in batch)
            {
                try
                {
                    Artist? artist = trackFile.Artist?.Value ?? _artistService.GetArtist(trackFile.Tracks?.Value?.FirstOrDefault()?.Artist?.Value?.Id ?? 0);
                    if (artist == null)
                    {
                        _logger.Debug($"Could not find artist for track file: {trackFile.Path}");
                        result.FailedCount++;
                        continue;
                    }

                    if (LyricsHelper.LrcFileExistsOnDisk(trackFile.Path, _diskProvider))
                    {
                        SyncExistingLrcFile(artist, trackFile);
                        result.SyncedCount++;
                        continue;
                    }

                    _logger.ProgressTrace($"Searching lyrics for: {trackFile.Tracks?.Value?.FirstOrDefault()?.Title ?? Path.GetFileName(trackFile.Path)}");

                    MetadataFileResult? metadataResult = TrackMetadata(artist, trackFile);

                    if (metadataResult != null && !string.IsNullOrEmpty(metadataResult.Contents))
                    {
                        WriteLrcFile(artist, trackFile, metadataResult);
                        result.SuccessCount++;
                    }
                    else
                    {
                        result.FailedCount++;
                        _logger.Trace($"No lyrics found for: {trackFile.Path}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Error processing track: {trackFile.Path}");
                    result.FailedCount++;
                }
            }

            return result;
        }

        private void SyncExistingLrcFile(Artist artist, TrackFile trackFile)
        {
            string lrcPath = Path.ChangeExtension(trackFile.Path, ".lrc");
            string relativePath = artist.Path.GetRelativePath(lrcPath);
            _trackFileRepositoryHelper.CreateAndUpsertLyricFile(artist, trackFile, relativePath);
            _logger.Trace($"Synced existing LRC file to database: {lrcPath}");
        }

        private void WriteLrcFile(Artist artist, TrackFile trackFile, MetadataFileResult metadataResult)
        {
            string lrcPath = Path.Combine(artist.Path, metadataResult.RelativePath);
            _diskProvider.WriteAllText(lrcPath, metadataResult.Contents);
            _trackFileRepositoryHelper.CreateAndUpsertLyricFile(artist, trackFile, metadataResult.RelativePath);
            _logger.Trace($"Created LRC file: {lrcPath}");
        }

        private void EmbedLyrics(Lyric lyric, string trackFilePath)
        {
            LyricOptions embeddingOption = (LyricOptions)ActiveSettings.LyricEmbeddingOption;
            if (embeddingOption == LyricOptions.Disabled)
                return;

            string? lyricsToEmbed = LyricsHelper.GetLyricsForEmbedding(lyric, embeddingOption);
            if (!string.IsNullOrWhiteSpace(lyricsToEmbed))
            {
                LyricsHelper.EmbedLyricsInAudioFile(trackFilePath, lyricsToEmbed, _logger, _rootFolderWatchingService);
            }
        }

        public override string GetFilenameAfterMove(Artist artist, TrackFile trackFile, MetadataFile metadataFile)
        {
            if (metadataFile.Type == MetadataType.TrackMetadata)
                return Path.ChangeExtension(trackFile.Path, ".lrc");

            _logger.Trace("Unknown track file metadata: {0}", metadataFile.RelativePath);
            return Path.Combine(artist.Path, metadataFile.RelativePath);
        }

        public override MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile)
        {
            if (!ActiveSettings.OverwriteExistingLrcFiles && LyricsHelper.LrcFileExistsOnDisk(trackFile.Path, _diskProvider))
            {
                _logger.Trace($"LRC file already exists and overwrite is disabled: {trackFile.Path}");
                return default!;
            }

            if (!_diskProvider.FileExists(trackFile.Path))
            {
                _logger.Warn($"Track file does not exist: {trackFile.Path}");
                return default!;
            }

            try
            {
                return ProcessTrackLyricsAsync(artist, trackFile).Result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error processing lyrics for track: {trackFile.Path}");
                return default!;
            }
        }

        private async Task<MetadataFileResult> ProcessTrackLyricsAsync(Artist artist, TrackFile trackFile)
        {
            _logger.Trace($"Processing lyrics for track: {trackFile.Path}");

            (string Artist, string Title, string Album, int Duration)? trackInfo = LyricsHelper.ExtractTrackInfo(trackFile, artist, _logger);
            if (trackInfo == null)
                return default!;

            Lyric? lyric = await FetchLyricsAsync(trackInfo.Value);
            if (lyric == null)
            {
                _logger.Trace($"No lyrics found for track: {trackInfo.Value.Title} by {trackInfo.Value.Artist}");
                return default!;
            }

            // Embed lyrics into audio file based on settings
            EmbedLyrics(lyric, trackFile.Path);

            // Create LRC file content based on settings
            string? lrcContent = CreateLrcFileContent(lyric, trackInfo.Value);
            if (string.IsNullOrEmpty(lrcContent))
                return default!;

            string relativePath = artist.Path.GetRelativePath(trackFile.Path);
            relativePath = Path.ChangeExtension(relativePath, ".lrc");
            return new MetadataFileResult(relativePath, lrcContent);
        }

        private async Task<Lyric?> FetchLyricsAsync((string Artist, string Title, string Album, int Duration) trackInfo)
        {
            Lyric? lyric = null;

            if (ActiveSettings.LrcLibEnabled)
            {
                lyric = await _lyricsProviders.FetchFromLrcLibAsync(
                    trackInfo.Artist,
                    trackInfo.Title,
                    trackInfo.Album,
                    trackInfo.Duration);
            }

            if (lyric == null && ActiveSettings.GeniusEnabled && !string.IsNullOrWhiteSpace(ActiveSettings.GeniusApiKey))
            {
                lyric = await _lyricsProviders.FetchFromGeniusAsync(trackInfo.Artist, trackInfo.Title);
            }

            return lyric;
        }

        private string? CreateLrcFileContent(Lyric lyric, (string Artist, string Title, string Album, int Duration) trackInfo)
        {
            LyricOptions lrcOption = (LyricOptions)ActiveSettings.LrcFileOptions;
            return LyricsHelper.GetLyricsForLrcFile(
                lyric,
                lrcOption,
                trackInfo.Artist,
                trackInfo.Title,
                trackInfo.Album,
                trackInfo.Duration);
        }

        /// <summary>
        /// Result container for track processing operations.
        /// </summary>
        private class ProcessingResult
        {
            public int SuccessCount { get; set; }
            public int SyncedCount { get; set; }
            public int FailedCount { get; set; }
        }
    }
}