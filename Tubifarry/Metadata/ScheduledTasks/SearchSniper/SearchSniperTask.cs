using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.ThingiProvider;
using Tubifarry.Core.Utilities;


namespace Tubifarry.Metadata.ScheduledTasks.SearchSniper
{
    /// <summary>
    /// Search Sniper, Automated search trigger that randomly selects albums for periodic scanning.
    /// </summary>
    public class SearchSniperTask : ScheduledTaskBase<SearchSniperTaskSettings>, IExecute<SearchSniperCommand>
    {
        private static readonly CacheService _cacheService = new();
        private readonly IAlbumService _albumService;
        private readonly IQueueService _queueService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IAlbumRepository _albumRepository;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly AlbumRepositoryHelper _albumRepositoryHelper;
        private readonly Logger _logger;

        public SearchSniperTask(
            IAlbumService albumService,
            IQueueService queueService,
            IManageCommandQueue commandQueueManager,
            IAlbumRepository albumRepository,
            IQualityProfileService qualityProfileService,
            IMainDatabase database,
            IEventAggregator eventAggregator,
            Logger logger)
        {
            _albumService = albumService;
            _queueService = queueService;
            _commandQueueManager = commandQueueManager;
            _albumRepository = albumRepository;
            _qualityProfileService = qualityProfileService;
            _albumRepositoryHelper = new AlbumRepositoryHelper(database, eventAggregator);
            _logger = logger;
        }

        public override string Name => "Search Sniper";

        public override Type CommandType => typeof(SearchSniperCommand);

        public override ProviderMessage Message => new(
            "Automated search trigger that randomly selects albums for periodic scanning based on your search criteria. " +
            "Enable this metadata provider to start automatic searches.",
            ProviderMessageType.Info);

        private SearchSniperTaskSettings ActiveSettings => Settings ?? SearchSniperTaskSettings.Instance!;

        public override int IntervalMinutes => SearchSniperTaskSettings.Instance!.RefreshInterval;

        public override CommandPriority Priority => CommandPriority.Low;

        public override ValidationResult Test()
        {
            ValidationResult test = new();
            InitializeCache();

            if (ActiveSettings?.RequestCacheType == (int)CacheType.Permanent && !string.IsNullOrWhiteSpace(ActiveSettings.CacheDirectory) && !Directory.Exists(ActiveSettings.CacheDirectory))
            {
                try
                {
                    Directory.CreateDirectory(ActiveSettings.CacheDirectory);
                }
                catch (Exception ex)
                {
                    test.Errors.Add(new ValidationFailure("CacheDirectory", $"Failed to create cache directory: {ex.Message}"));
                }
            }

            return test;
        }

        /// <summary>
        /// Command handler - called when SearchSniperCommand is executed by the task manager.
        /// </summary>
        public void Execute(SearchSniperCommand message)
        {
            try
            {
                RunSearch(message);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during scheduled execution");
            }
        }

        private void InitializeCache()
        {
            if (ActiveSettings == null) return;

            _cacheService.CacheDuration = TimeSpan.FromDays(ActiveSettings.CacheRetentionDays);
            _cacheService.CacheType = (CacheType)ActiveSettings.RequestCacheType;
            _cacheService.CacheDirectory = ActiveSettings.CacheDirectory;
        }

        /// <summary>
        /// Main search logic - retrieves eligible albums and triggers searches.
        /// </summary>
        private void RunSearch(SearchSniperCommand message)
        {
            if (!ActiveSettings.SearchMissing && !ActiveSettings.SearchMissingTracks && !ActiveSettings.SearchQualityCutoffNotMet)
            {
                _logger.Warn("No search options enabled. Please enable at least one search criteria.");
                return;
            }

            if (ActiveSettings.StopWhenQueued > 0)
            {
                int queueCount = GetQueueCountByWaitOnType((WaitOnType)ActiveSettings.WaitOn);
                if (queueCount >= ActiveSettings.StopWhenQueued)
                {
                    message.SetCompletionMessage($"Skipping Search Sniper, queue threshold reached ({queueCount} {(WaitOnType)ActiveSettings.WaitOn} items)");
                    _logger.Info($"Skipping. Queue count ({queueCount}) of {(WaitOnType)ActiveSettings.WaitOn} items reached threshold ({ActiveSettings.StopWhenQueued})");
                    return;
                }
            }

            List<Album> targetAlbums = GetTargetAlbums();
            if (targetAlbums.Count == 0)
                return;

            List<Album> eligibleAlbums = ExcludeQueuedAlbums(targetAlbums);
            if (eligibleAlbums.Count == 0)
                return;

            List<Album> nonCachedAlbums = ExcludeCachedAlbumsAsync(eligibleAlbums).GetAwaiter().GetResult();
            _logger.Trace("{0} non-cached album(s) available", nonCachedAlbums.Count);

            if (nonCachedAlbums.Count == 0)
                return;

            List<Album> selectedAlbums = SelectRandomAlbums(nonCachedAlbums);

            foreach (Album album in selectedAlbums)
                _logger.Debug("Selected: '{0}' by {1}", album.Title, album.Artist?.Value.Name ?? "Unknown Artist");

            CacheSelectedAlbumsAsync(selectedAlbums).GetAwaiter().GetResult();

            if (selectedAlbums.Count > 0)
            {
                _commandQueueManager.Push(new AlbumSearchCommand(selectedAlbums.ConvertAll(a => a.Id)));
                message.SetCompletionMessage($"Search Sniper completed. Queued {selectedAlbums.Count} albums for search");
                _logger.Info($"Queued {selectedAlbums.Count} albums for search");
            }
        }

        /// <summary>
        /// Retrieves albums based on enabled search criteria from settings.
        /// </summary>
        private List<Album> GetTargetAlbums()
        {
            HashSet<int> targetAlbums = [];

            if (ActiveSettings.SearchMissing)
            {
                List<Album> noFiles = GetAlbumsWithNoFiles();
                foreach (Album album in noFiles)
                    targetAlbums.Add(album.Id);
                _logger.Debug("{0} album(s) with no files", noFiles.Count);
            }

            if (ActiveSettings.SearchMissingTracks)
            {
                List<Album> missingTracks = GetAlbumsWithMissingTracks();
                foreach (Album album in missingTracks)
                    targetAlbums.Add(album.Id);
                _logger.Debug("{0} album(s) with missing tracks", missingTracks.Count);
            }

            if (ActiveSettings.SearchQualityCutoffNotMet)
            {
                List<Album> cutoffUnmet = GetAlbumsWhereCutoffUnmet();
                foreach (Album album in cutoffUnmet)
                    targetAlbums.Add(album.Id);
                _logger.Debug("{0} album(s) below quality cutoff", cutoffUnmet.Count);
            }

            return targetAlbums
                .Join(_albumService.GetAllAlbums(), id => id, a => a.Id, (_, album) => album)
                .ToList();
        }

        /// <summary>
        /// Gets albums that have no track files (completely missing from library).
        /// </summary>
        private List<Album> GetAlbumsWithNoFiles()
        {
            try
            {
                PagingSpec<Album> pagingSpec = new()
                {
                    Page = 1,
                    PageSize = 100000,
                    SortDirection = SortDirection.Ascending,
                    SortKey = "Id"
                };

                // Explicit == true comparison is required for proper SQL translation
                pagingSpec.FilterExpressions.Add(v => v.Monitored == true && v.Artist.Value.Monitored == true);
                return _albumService.AlbumsWithoutFiles(pagingSpec).Records;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error querying albums with no files");
                return [];
            }
        }

        /// <summary>
        /// Gets albums that have some track files but are missing other tracks (partial completion).
        /// </summary>
        private List<Album> GetAlbumsWithMissingTracks()
        {
            try
            {
                // Explicit == true comparison is required for proper SQL translation
                SqlBuilder builder = _albumRepositoryHelper.Builder()
                    .Join<Album, Artist>((a, ar) => a.ArtistMetadataId == ar.ArtistMetadataId)
                    .Join<Album, AlbumRelease>((a, r) => a.Id == r.AlbumId)
                    .Join<AlbumRelease, Track>((r, t) => r.Id == t.AlbumReleaseId)
                    .LeftJoin<Track, TrackFile>((t, f) => t.TrackFileId == f.Id)
                    .Where<Album>(a => a.Monitored == true)
                    .Where<Artist>(ar => ar.Monitored == true)
                    .Where<AlbumRelease>(r => r.Monitored == true)
                    .GroupBy<Album>(a => a.Id)
                    .GroupBy<Artist>(ar => ar.SortName)
                    .Having("COUNT(DISTINCT \"Tracks\".\"Id\") > 0")
                    .Having("SUM(CASE WHEN \"Tracks\".\"TrackFileId\" > 0 THEN 1 ELSE 0 END) > 0")
                    .Having("SUM(CASE WHEN \"Tracks\".\"TrackFileId\" > 0 THEN 1 ELSE 0 END) < COUNT(DISTINCT \"Tracks\".\"Id\")");

                _logger.Trace("Executing missing tracks query");

                List<Album>? result = _albumRepositoryHelper.Query(builder);
                return result ?? [];
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error querying albums with missing tracks");
                return [];
            }
        }

        /// <summary>
        /// Gets albums where the quality cutoff has not been met.
        /// Files exist but are below the artist's quality profile cutoff.
        /// </summary>
        private List<Album> GetAlbumsWhereCutoffUnmet()
        {
            try
            {
                List<QualityProfile> qualityProfiles = _qualityProfileService.All();
                List<QualitiesBelowCutoff> qualitiesBelowCutoff = [];

                foreach (QualityProfile? profile in qualityProfiles)
                {
                    if (!profile.UpgradeAllowed)
                        continue;

                    int cutoffIndex = profile.Items.FindIndex(x => (x.Quality?.Id == profile.Cutoff) || (x.Id == profile.Cutoff));
                    if (cutoffIndex <= 0)
                        continue;
                    List<int> qualityIds = [];

                    foreach (QualityProfileQualityItem? item in profile.Items.Take(cutoffIndex))
                    {
                        if (!item.Allowed)
                            continue;
                        if (item.Quality != null)
                            qualityIds.Add(item.Quality.Id);
                        else if (item.Items?.Any() == true)
                            qualityIds.AddRange(item.Items.Where(i => i.Quality != null && i.Allowed).Select(i => i.Quality!.Id));
                    }

                    if (qualityIds.Count != 0)
                        qualitiesBelowCutoff.Add(new QualitiesBelowCutoff(profile.Id, qualityIds));
                }

                if (qualitiesBelowCutoff.Count == 0)
                    return [];

                PagingSpec<Album> pagingSpec = new()
                {
                    Page = 1,
                    PageSize = 100000,
                    SortDirection = SortDirection.Ascending,
                    SortKey = "Id"
                };

                // Explicit == true comparison is required for proper SQL translation
                pagingSpec.FilterExpressions.Add(v => v.Monitored == true && v.Artist.Value.Monitored == true);

                PagingSpec<Album> result = _albumRepository.AlbumsWhereCutoffUnmet(pagingSpec, qualitiesBelowCutoff);
                return [.. result.Records];
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error querying albums where cutoff is not met");
                return [];
            }
        }

        /// <summary>
        /// Gets the count of queue items based on the selected WaitOnType.
        /// </summary>
        private int GetQueueCountByWaitOnType(WaitOnType waitOnType)
        {
            List<Queue> queue = _queueService.GetQueue();

            return waitOnType switch
            {
                WaitOnType.Queued => queue.Count(x => x.Status == "Queued"),
                WaitOnType.Downloading => queue.Count(x => x.Status == "Downloading"),
                WaitOnType.Warning => queue.Count(x => x.Status == "Warning"),
                WaitOnType.QueuedAndDownloading => queue.Count(x => x.Status == "Queued" || x.Status == "Downloading"),
                WaitOnType.All => queue.Count(x => x.Status != "Completed" && x.Status != "Failed"),
                _ => 0
            };
        }

        /// <summary>
        /// Filters out albums that are already in the download queue.
        /// </summary>
        private List<Album> ExcludeQueuedAlbums(List<Album> albums)
        {
            HashSet<int> queuedAlbumIds = _queueService.GetQueue()
                .Where(q => q.Album is not null)
                .Select(q => q.Album!.Id)
                .ToHashSet();

            return albums.Where(a => !queuedAlbumIds.Contains(a.Id)).ToList();
        }

        /// <summary>
        /// Filters out albums that have been cached (already searched recently).
        /// </summary>
        private static async Task<List<Album>> ExcludeCachedAlbumsAsync(List<Album> albums)
        {
            HashSet<int> cachedAlbumIds = [];

            foreach (Album album in albums)
            {
                string cacheKey = GenerateCacheKey(album);
                bool cachedEntry = await _cacheService.GetAsync<bool>(cacheKey);

                if (cachedEntry)
                    cachedAlbumIds.Add(album.Id);
            }

            return albums.Where(a => !cachedAlbumIds.Contains(a.Id)).ToList();
        }

        /// <summary>
        /// Caches the selected albums to prevent re-searching too soon.
        /// </summary>
        private static async Task CacheSelectedAlbumsAsync(List<Album> albums)
        {
            foreach (Album album in albums)
            {
                string cacheKey = GenerateCacheKey(album);
                await _cacheService.SetAsync(cacheKey, true);
            }
        }

        /// <summary>
        /// Randomly selects albums from the eligible list.
        /// </summary>
        private List<Album> SelectRandomAlbums(List<Album> albums)
        {
            if (ActiveSettings == null) return [];

            int pickCount = Math.Min(ActiveSettings.RandomPicksPerInterval, albums.Count);
            Random random = new();

            return albums.OrderBy(_ => random.Next()).Take(pickCount).ToList();
        }

        /// <summary>
        /// Generates a cache key for an album.
        /// </summary>
        private static string GenerateCacheKey(Album album) =>
            $"SearchSniper:{album.Artist?.Value.Name ?? "Unknown"}:{album.Id}";

        /// <summary>
        /// Helper class to access protected AlbumRepository methods.
        /// </summary>
        private sealed class AlbumRepositoryHelper(IMainDatabase database, IEventAggregator eventAggregator)
            : AlbumRepository(database, eventAggregator)
        {
            public new SqlBuilder Builder() => base.Builder();
            public new List<Album> Query(SqlBuilder builder) => base.Query(builder);
        }
    }
}