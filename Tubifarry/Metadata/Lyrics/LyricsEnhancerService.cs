using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using Tubifarry.Core.Records;
using Tubifarry.Metadata.Lyrics.Converters;

namespace Tubifarry.Metadata.Lyrics
{
    public interface ILyricsEnhancerService
    {
        void Execute(LyricsUpdateCommand message, LyricsEnhancerSettings settings);
        MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile, LyricsEnhancerSettings settings);
        string GetFilenameAfterMove(Artist artist, TrackFile trackFile, MetadataFile metadataFile);
    }

    public class LyricsEnhancerService : ILyricsEnhancerService
    {
        private const int SqlBatchSize = 500;

        private readonly Logger _logger;
        private readonly HttpClient _httpClient;
        private readonly IRootFolderWatchingService _rootFolderWatchingService;
        private readonly IArtistService _artistService;
        private readonly IDiskProvider _diskProvider;
        private readonly TrackFileRepositoryHelper _trackFileRepositoryHelper;
        private readonly IMediaFileService _mediaFileService;

        public LyricsEnhancerService(
            HttpClient httpClient,
            Logger logger,
            IRootFolderWatchingService rootFolderWatchingService,
            ILyricFileService lyricFileService,
            IArtistService artistService,
            IDiskProvider diskProvider,
            IMainDatabase database,
            IEventAggregator eventAggregator,
            ITrackRepository trackRepository,
            IExtraFileService<OtherExtraFile> otherExtraFileService,
            IMetadataFileService metadataFileService,
            IMediaFileService mediaFileService)
        {
            _logger = logger;
            _httpClient = httpClient;
            _rootFolderWatchingService = rootFolderWatchingService;
            _artistService = artistService;
            _diskProvider = diskProvider;
            _mediaFileService = mediaFileService;
            _trackFileRepositoryHelper = new TrackFileRepositoryHelper(database, eventAggregator, trackRepository, lyricFileService, otherExtraFileService, metadataFileService, logger);
        }

        public void Execute(LyricsUpdateCommand message, LyricsEnhancerSettings settings)
        {
            if (!settings.EnableScheduledUpdates)
            {
                _logger.Debug("Scheduled lyrics updates are disabled in settings");
                message.SetCompletionMessage("Lyrics updates are disabled");
                return;
            }

            try
            {
                LyricsProviderManager providers = new(_httpClient, _logger, settings);
                _logger.ProgressInfo("Starting scheduled lyrics update");

                int totalTracks = _trackFileRepositoryHelper.GetTracksWithoutLrcFilesCount();
                if (totalTracks == 0)
                {
                    _logger.Info("All tracks in database have lyric file entries");
                    message.SetCompletionMessage("All tracks have lyrics entries");
                    return;
                }

                _logger.Debug($"Found {totalTracks} tracks without lyric entries in database");

                ProcessingResult total = new();

                for (int offset = 0; offset < totalTracks; offset += SqlBatchSize)
                {
                    List<TrackFile> batch = _trackFileRepositoryHelper.GetTracksWithoutLrcFilesBatch(offset, SqlBatchSize);
                    if (batch.Count == 0)
                        break;

                    total.Add(ProcessTrackBatch(batch, settings, providers));
                    _logger.Debug($"Progress: {Math.Min(offset + batch.Count, totalTracks)}/{totalTracks} tracks without lyrics processed");
                }

                string completionMsg = $"Lyrics update completed: {total.Created} created, {total.Synced} synced, {total.Failed} not found.";
                _logger.Info(completionMsg);
                message.SetCompletionMessage(completionMsg);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during scheduled lyrics update execution");
                message.SetCompletionMessage($"Lyrics update failed: {ex.Message}");
            }
        }

        public MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile, LyricsEnhancerSettings settings) =>
            TrackMetadata(artist, trackFile, settings, new LyricsProviderManager(_httpClient, _logger, settings));

        public string GetFilenameAfterMove(Artist artist, TrackFile trackFile, MetadataFile metadataFile)
        {
            if (metadataFile.Type == MetadataType.TrackMetadata)
            {
                string extension = Path.GetExtension(metadataFile.RelativePath);
                if (string.IsNullOrEmpty(extension))
                    extension = LyricsHelper.LrcExtension;
                return Path.ChangeExtension(trackFile.Path, extension);
            }

            _logger.Trace("Unknown track file metadata: {0}", metadataFile.RelativePath);
            return Path.Combine(artist.Path, metadataFile.RelativePath);
        }

        private ProcessingResult ProcessTrackBatch(List<TrackFile> batch, LyricsEnhancerSettings settings, LyricsProviderManager providers)
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
                        result.Failed++;
                        continue;
                    }

                    if (LyricsHelper.TryFindLyricFileOnDisk(trackFile.Path, _diskProvider, out string existingLyricPath))
                    {
                        RegisterLyricFile(artist, trackFile, artist.Path.GetRelativePath(existingLyricPath));
                        result.Synced++;
                        continue;
                    }

                    _logger.ProgressTrace($"Searching lyrics for: {trackFile.Tracks?.Value?.FirstOrDefault()?.Title ?? Path.GetFileName(trackFile.Path)}");

                    MetadataFileResult? metadataResult = TrackMetadata(artist, trackFile, settings, providers);
                    if (metadataResult != null && !string.IsNullOrEmpty(metadataResult.Contents))
                    {
                        _diskProvider.WriteAllText(Path.Combine(artist.Path, metadataResult.RelativePath), metadataResult.Contents);
                        RegisterLyricFile(artist, trackFile, metadataResult.RelativePath);
                        result.Created++;
                    }
                    else
                    {
                        _logger.Trace($"No lyrics found for: {trackFile.Path}");
                        result.Failed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Error processing track: {trackFile.Path}");
                    result.Failed++;
                }
            }

            return result;
        }

        private void RegisterLyricFile(Artist artist, TrackFile trackFile, string relativePath)
        {
            _trackFileRepositoryHelper.CreateAndUpsertLyricFile(artist, trackFile, relativePath);
            _logger.Trace($"Registered lyric file in database: {relativePath}");
        }

        private MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile, LyricsEnhancerSettings settings, LyricsProviderManager providers)
        {
            if (!settings.OverwriteExistingLrcFiles && LyricsHelper.LyricFileExistsOnDisk(trackFile.Path, _diskProvider))
            {
                _logger.Trace($"Lyric file already exists and overwrite is disabled: {trackFile.Path}");
                return default!;
            }

            if (!_diskProvider.FileExists(trackFile.Path))
            {
                _logger.Warn($"Track file does not exist: {trackFile.Path}");
                return default!;
            }

            try
            {
                return ProcessTrackLyricsAsync(artist, trackFile, settings, providers).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error processing lyrics for track: {trackFile.Path}");
                return default!;
            }
        }

        private async Task<MetadataFileResult> ProcessTrackLyricsAsync(Artist artist, TrackFile trackFile, LyricsEnhancerSettings settings, LyricsProviderManager providers)
        {
            TrackInfo? trackInfo = LyricsHelper.ExtractTrackInfo(trackFile, artist, _logger);
            if (trackInfo == null)
                return default!;

            Lyric? lyric = await FetchLyricsAsync(trackInfo, settings, providers);
            if (lyric == null)
            {
                _logger.Trace($"No lyrics found for track: {trackInfo.Title} by {trackInfo.Artist}");
                return default!;
            }

            EmbedLyrics(lyric, trackFile, settings);

            (string Content, string Extension)? lyricsFile = CreateLyricsFile(lyric, trackInfo, settings);
            if (lyricsFile == null)
                return default!;

            string relativePath = Path.ChangeExtension(artist.Path.GetRelativePath(trackFile.Path), lyricsFile.Value.Extension);
            return new MetadataFileResult(relativePath, lyricsFile.Value.Content);
        }

        private async Task<Lyric?> FetchLyricsAsync(TrackInfo trackInfo, LyricsEnhancerSettings settings, LyricsProviderManager providers)
        {
            SyncLevel desiredLevel = GetDesiredSyncLevel(settings);
            Lyric? bestSoFar = null;

            foreach ((bool Enabled, Func<Task<Lyric?>> Fetch, string Name) provider in EnumerateProviders(trackInfo, settings, providers))
            {
                if (!provider.Enabled)
                    continue;

                Lyric? lyric = await provider.Fetch();
                if (lyric == null)
                    continue;

                SyncLevel level = GetSyncLevel(lyric);
                if (level >= desiredLevel)
                {
                    _logger.Trace($"Using {level} lyrics from {provider.Name} (desired: {desiredLevel})");
                    return lyric;
                }

                if (bestSoFar == null || level > GetSyncLevel(bestSoFar))
                {
                    _logger.Trace($"{provider.Name} returned {level} lyrics, keeping as fallback while searching for {desiredLevel}");
                    bestSoFar = lyric;
                }
            }

            if (bestSoFar != null)
                _logger.Trace($"No provider returned {desiredLevel} lyrics, falling back to {GetSyncLevel(bestSoFar)}");

            return bestSoFar;
        }

        private static IEnumerable<(bool Enabled, Func<Task<Lyric?>> Fetch, string Name)> EnumerateProviders(TrackInfo track, LyricsEnhancerSettings settings, LyricsProviderManager providers)
        {
            yield return (settings.LrcLibEnabled,
                () => providers.FetchFromLrcLibAsync(track.Artist, track.Title, track.Album, track.DurationSeconds), "LRCLIB");
            yield return (settings.BinimumEnabled,
                () => providers.FetchFromBinimumAsync(track.Artist, track.Title, track.Album, track.DurationSeconds), "Binimum");
            yield return (settings.LyricsPlusEnabled,
                () => providers.FetchFromLyricsPlusAsync(track.Artist, track.Title, track.Album, track.DurationSeconds), "LyricsPlus");
            yield return (settings.UnisonEnabled,
                () => providers.FetchFromUnisonAsync(track.Artist, track.Title, track.Album, track.DurationSeconds), "Unison");
            yield return (settings.GeniusEnabled && !string.IsNullOrWhiteSpace(settings.GeniusApiKey),
                () => providers.FetchFromGeniusAsync(track.Artist, track.Title), "Genius");
        }

        private void EmbedLyrics(Lyric lyric, TrackFile trackFile, LyricsEnhancerSettings settings)
        {
            LyricOptions option = (LyricOptions)settings.LyricEmbeddingOption;
            if (option == LyricOptions.Disabled)
                return;

            string? lyricsToEmbed = GetLyricsContent(lyric, option);
            if (string.IsNullOrWhiteSpace(lyricsToEmbed))
                return;

            bool wasModified = LyricsHelper.EmbedLyricsInAudioFile(trackFile.Path, lyricsToEmbed, _logger, _rootFolderWatchingService);
            if (wasModified && trackFile.Id > 0)
                UpdateTrackFileAfterEmbed(trackFile);
        }

        private void UpdateTrackFileAfterEmbed(TrackFile trackFile)
        {
            try
            {
                FileInfo fileInfo = new(trackFile.Path);
                trackFile.Size = fileInfo.Length;
                trackFile.Modified = fileInfo.LastWriteTimeUtc;
                _mediaFileService.Update(trackFile);
                _logger.Debug($"Updated TrackFile metadata after embedding lyrics: Size={trackFile.Size}, Modified={trackFile.Modified:O}");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Failed to update TrackFile metadata after embedding lyrics: {trackFile.Path}");
            }
        }

        private (string Content, string Extension)? CreateLyricsFile(Lyric lyric, TrackInfo trackInfo, LyricsEnhancerSettings settings)
        {
            Lyric lyricWithMeta = lyric with { Artist = trackInfo.Artist, Title = trackInfo.Title, Album = trackInfo.Album, Duration = trackInfo.DurationSeconds };

            (LyricConverterBase Converter, string Extension)? target = SelectSyncLevel(lyricWithMeta, (LyricOptions)settings.LrcFileOptions) switch
            {
                SyncLevel.WordSynced => LyricsHelper.ResolveWordSynced(settings),
                SyncLevel.LineSynced => LyricsHelper.ResolveLineSynced(settings),
                SyncLevel.Plain => (new PlainTextConverter(), LyricsHelper.ResolveLineSynced(settings).Extension),
                _ => null
            };

            if (target == null)
                return null;

            string? content = target.Value.Converter.Write(lyricWithMeta);
            return string.IsNullOrEmpty(content) ? null : (content, target.Value.Extension);
        }

        private static string? GetLyricsContent(Lyric lyric, LyricOptions option) =>
            SelectSyncLevel(lyric, option) switch
            {
                SyncLevel.WordSynced => new ElrcConverter().Write(lyric),
                SyncLevel.LineSynced => new LrcConverter().Write(lyric),
                SyncLevel.Plain => new PlainTextConverter().Write(lyric),
                _ => null
            };

        private static SyncLevel? SelectSyncLevel(Lyric lyric, LyricOptions option) => option switch
        {
            LyricOptions.OnlyPlain => SyncLevel.Plain,
            LyricOptions.OnlyLineSynced when lyric.HasLineSync => SyncLevel.LineSynced,
            LyricOptions.PreferLineSynced => lyric.HasLineSync ? SyncLevel.LineSynced : SyncLevel.Plain,
            LyricOptions.OnlyWordSynced when lyric.HasWordSync => SyncLevel.WordSynced,
            LyricOptions.PreferWordSynced => lyric.HasWordSync ? SyncLevel.WordSynced
                : lyric.HasLineSync ? SyncLevel.LineSynced
                : SyncLevel.Plain,
            _ => null
        };

        private static SyncLevel GetDesiredSyncLevel(LyricsEnhancerSettings settings)
        {
            static SyncLevel ForOption(LyricOptions option) => option switch
            {
                LyricOptions.OnlyWordSynced or LyricOptions.PreferWordSynced => SyncLevel.WordSynced,
                LyricOptions.OnlyLineSynced or LyricOptions.PreferLineSynced => SyncLevel.LineSynced,
                LyricOptions.OnlyPlain => SyncLevel.Plain,
                _ => SyncLevel.None
            };

            SyncLevel fileLevel = ForOption((LyricOptions)settings.LrcFileOptions);
            SyncLevel embedLevel = ForOption((LyricOptions)settings.LyricEmbeddingOption);
            return fileLevel > embedLevel ? fileLevel : embedLevel;
        }

        private static SyncLevel GetSyncLevel(Lyric lyric) =>
            lyric.HasWordSync ? SyncLevel.WordSynced :
            lyric.HasLineSync ? SyncLevel.LineSynced :
            SyncLevel.Plain;

        private enum SyncLevel
        {
            None = 0,
            Plain = 1,
            LineSynced = 2,
            WordSynced = 3
        }

        private sealed class ProcessingResult
        {
            public int Created;
            public int Synced;
            public int Failed;

            public void Add(ProcessingResult other)
            {
                Created += other.Created;
                Synced += other.Synced;
                Failed += other.Failed;
            }
        }
    }
}
