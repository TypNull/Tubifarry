using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Queue;
using NzbDrone.Core.ThingiProvider;
using Tubifarry.Core.Model;

namespace Tubifarry.ImportLists.WantedList
{
    public class SearchSniper : ImportListBase<SearchSniperSettings>
    {
        private readonly IAlbumService _albumService;
        private readonly IQueueService _queueService;
        private readonly IManageCommandQueue _commandQueueManager;

        private static readonly Dictionary<int, DateTime> _memoryCache = new();
        private static readonly object _cacheLock = new();
        private FileCache _fileCache = null!;

        public SearchSniper(IImportListStatusService importListStatusService, IConfigService configService, IParsingService parsingService, IAlbumService albumService, IQueueService queueService, IManageCommandQueue commandQueueManager, Logger logger) : base(importListStatusService, configService, parsingService, logger)
        {
            _albumService = albumService;
            _queueService = queueService;
            _commandQueueManager = commandQueueManager;
        }

        public override string Name => "Search Sniper";

        public override ProviderMessage Message => new("This is not a Import List. The 'Added Artist Settings' and 'General Import List Settings' features are non-functional here. This is an automated search trigger that randomly selects albums from the wanted queue for periodic scanning.", ProviderMessageType.Warning);
        public override ImportListType ListType => ImportListType.Advanced;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromMinutes((Definition?.Settings as SearchSniperSettings)?.RefreshInterval ?? 1);

        public override IList<ImportListItemInfo> Fetch()
        {
            PagingSpec<Album> pagingSpec = new()
            {
                Page = 1,
                PageSize = 100000,
                SortDirection = SortDirection.Ascending,
                SortKey = "Id"
            };

            pagingSpec.FilterExpressions.Add(v => v.Monitored == true && v.Artist.Value.Monitored == true);

            List<Album> allMissingAlbums = _albumService.AlbumsWithoutFiles(pagingSpec).Records;
            List<int> queueIds = _queueService.GetQueue().Where(q => q.Album != null).Select(q => q.Album.Id).ToList();
            List<Album> eligibleAlbums = allMissingAlbums.Where(a => !queueIds.Contains(a.Id)).ToList();

            if (!eligibleAlbums.Any())
            {
                _logger.Debug("No albums available for Search Sniper to pick.");
                return new List<ImportListItemInfo>();
            }

            TimeSpan cacheDuration = TimeSpan.FromDays(Settings.CacheRetentionDays);
            List<int> cachedAlbumIds = new();
            DateTime now = DateTime.UtcNow;

            if (Settings.CacheType == (int)CacheType.Memory)
            {
                lock (_cacheLock)
                {
                    _memoryCache.Keys.Where(key => now - _memoryCache[key] > cacheDuration).ToList().ForEach(key => _memoryCache.Remove(key));
                    cachedAlbumIds = _memoryCache.Keys.ToList();
                }
                _logger.Trace("Memory cache contains {0} album(s).", cachedAlbumIds.Count);
            }
            else if (Settings.CacheType == (int)CacheType.Permanent)
            {
                _fileCache ??= new FileCache(Settings.CacheDirectory);

                IEnumerable<IGrouping<string, Album>> albumsByArtist = eligibleAlbums.GroupBy(a => a.Artist?.Value.Name ?? "UnknownArtist");
                foreach (IGrouping<string, Album> artistGroup in albumsByArtist)
                {
                    string cacheKey = GenerateCacheKey(artistGroup.First());
                    Dictionary<int, DateTime> cacheData = _fileCache.GetAsync<Dictionary<int, DateTime>>(cacheKey).GetAwaiter().GetResult() ?? new();

                    cacheData.Keys.Where(key => now - cacheData[key] > cacheDuration).ToList().ForEach(key => cacheData.Remove(key));

                    cachedAlbumIds.AddRange(cacheData.Keys);
                    _logger.Trace("Permanent cache for artist '{0}' contains {1} album(s).", artistGroup.Key, cacheData.Count);
                }
            }

            List<Album> nonCachedAlbums = eligibleAlbums.Where(a => !cachedAlbumIds.Contains(a.Id)).ToList();
            if (!nonCachedAlbums.Any())
            {
                _logger.Debug("All eligible albums are cached. No new album will be searched.");
                return new List<ImportListItemInfo>();
            }

            Random random = new();
            int pickCount = Math.Min(Settings.RandomPicksPerInterval, nonCachedAlbums.Count);
            List<Album> selectedAlbums = nonCachedAlbums.OrderBy(a => random.Next()).Take(pickCount).ToList();

            foreach (Album album in selectedAlbums)
                _logger.Debug("Search Sniper picked album: {0} by {1}", album.Title, album.Artist?.Value.Name ?? "Unknown Artist");

            if (Settings.CacheType == (int)CacheType.Memory)
            {
                lock (_cacheLock)
                    selectedAlbums.ForEach(album => _memoryCache[album.Id] = now);
            }
            else if (Settings.CacheType == (int)CacheType.Permanent)
            {
                IEnumerable<IGrouping<string, Album>> selectedAlbumsByArtist = selectedAlbums.GroupBy(a => a.Artist?.Value.Name ?? "UnknownArtist");
                foreach (IGrouping<string, Album> artistGroup in selectedAlbumsByArtist)
                {
                    string cacheKey = GenerateCacheKey(artistGroup.First());
                    Dictionary<int, DateTime> cacheData = _fileCache.GetAsync<Dictionary<int, DateTime>>(cacheKey).GetAwaiter().GetResult() ?? new();

                    artistGroup.ToList().ForEach(album => cacheData[album.Id] = now);
                    _fileCache.SetAsync(cacheKey, cacheData, cacheDuration).GetAwaiter().GetResult();
                }
            }

            if (selectedAlbums.Any())
                _commandQueueManager.Push(new AlbumSearchCommand(selectedAlbums.ConvertAll(a => a.Id)));

            return new List<ImportListItemInfo>();
        }

        private static string GenerateCacheKey(Album album) => $"SearchSniper{album.Artist?.Value.Name ?? "Unknown"}";

        protected override void Test(List<ValidationFailure> failures)
        {
            if (Settings.CacheType == (int)CacheType.Permanent)
            {
                if (string.IsNullOrEmpty(Settings.CacheDirectory))
                {
                    failures.Add(new ValidationFailure(nameof(Settings.CacheDirectory), "A valid cache directory is required for Permanent cache. Please specify a directory where the cache files will be stored."));
                    return;
                }

                _fileCache ??= new FileCache(Settings.CacheDirectory);
                try
                {
                    _fileCache.CheckDirectory();
                }
                catch (Exception ex)
                {
                    failures.Add(new ValidationFailure(nameof(Settings.CacheDirectory), $"The specified cache directory is invalid or inaccessible. Error: {ex.Message}. Please ensure the directory exists and has the correct permissions."));
                }
            }
        }
    }
}
