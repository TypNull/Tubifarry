using FuzzySharp;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Mixed
{
    [Proxy(ProxyMode.Public)]
    [ProxyFor(typeof(IProvideArtistInfo))]
    [ProxyFor(typeof(IProvideAlbumInfo))]
    [ProxyFor(typeof(ISearchForNewArtist))]
    [ProxyFor(typeof(ISearchForNewAlbum))]
    [ProxyFor(typeof(ISearchForNewEntity))]
    public class MixedMetadataProxy : MixedProxyBase<MixedMetadataProxySettings>
    {
        public override string Name => "MetaMix";

        private const int ALBUM_SIMILARITY_THRESHOLD = 60;
        private const int ARTIST_SIMILARITY_THRESHOLD = 70;
        private const int DEFAULT_PRIORITY = 50;
        private const int MIN_QUERY_LENGTH = 5;

        private readonly IArtistService _artistService;
        internal readonly IProvideAdaptiveThreshold _adaptiveThreshold;

        public MixedMetadataProxy(Lazy<IProxyService> proxyService, IProvideAdaptiveThreshold adaptiveThreshold, IArtistService artistService, Logger logger) : base(proxyService, logger)
        {
            _adaptiveThreshold = adaptiveThreshold;
            _artistService = artistService;

            InitializeAdaptiveThreshold();
        }

        #region Proxy Methods

        public Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string id)
        {
            List<ProxyCandidate> candidates = GetCandidateProxies(x => x.CanHandleId(id));

            if (!candidates.Any())
                throw new NotImplementedException($"No proxy available to handle album id: {id}");

            ProxyCandidate selected = candidates[0];
            _logger.Trace($"GetAlbumInfo: Using proxy {selected.Proxy.Name} with priority {selected.Priority} for album id {id}");

            return InvokeProxyMethod<Tuple<string, Album, List<ArtistMetadata>>>(selected.Proxy, nameof(GetAlbumInfo), id);
        }

        public HashSet<string> GetChangedAlbums(DateTime startTime) =>
            GetChanged(x => InvokeProxyMethod<HashSet<string>>(x, nameof(GetChangedAlbums), startTime));

        public Artist GetArtistInfo(string lidarrId, int metadataProfileId)
        {
            _logger.Trace($"Fetching artist info for Lidarr ID: {lidarrId} with Metadata Profile ID: {metadataProfileId}");

            Artist baseArtist = _artistService.FindById(lidarrId);
            HashSet<IProxy> usedProxies = new();

            List<Artist> newArtists = ExecuteArtistSearch(lidarrId, metadataProfileId, baseArtist, usedProxies);
            Dictionary<IProxy, List<Album>> proxyAlbumMap = BuildProxyAlbumMap(baseArtist);

            return MergeAllArtistData(newArtists, baseArtist, lidarrId, proxyAlbumMap, usedProxies);
        }

        public HashSet<string> GetChangedArtists(DateTime startTime) =>
            GetChanged(x => InvokeProxyMethod<HashSet<string>>(x, nameof(GetChangedArtists), startTime));

        public List<Artist> SearchForNewArtist(string artistName) =>
            new ProxyDecisionHandler<Artist>(
                mixedProxy: this,
                searchExecutor: proxy => InvokeProxyMethod<List<Artist>>(proxy, nameof(SearchForNewArtist), artistName),
                containsItem: ContainsArtist,
                isValidQuery: () => IsValidQuery(artistName),
                supportSelector: s => s.CanHandleSearch(artistName: artistName)
            ).ExecuteSearch();

        public List<Album> SearchForNewAlbum(string albumTitle, string artistName) =>
            new ProxyDecisionHandler<Album>(
                mixedProxy: this,
                searchExecutor: proxy => InvokeProxyMethod<List<Album>>(proxy, nameof(SearchForNewAlbum), albumTitle, artistName),
                containsItem: ContainsAlbum,
                isValidQuery: () => IsValidQuery(albumTitle, artistName),
                supportSelector: s => s.CanHandleSearch(albumTitle, artistName)
            ).ExecuteSearch();

        public List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds) =>
            new ProxyDecisionHandler<Album>(
                mixedProxy: this,
                searchExecutor: proxy => InvokeProxyMethod<List<Album>>(proxy, nameof(SearchForNewAlbumByRecordingIds), recordingIds),
                containsItem: ContainsAlbum,
                isValidQuery: () => true,
                supportSelector: s => s.CanHandleIRecordingIds(recordingIds.ToArray())
            ).ExecuteSearch();

        public List<object> SearchForNewEntity(string albumTitle) =>
            new ProxyDecisionHandler<object>(
                mixedProxy: this,
                searchExecutor: proxy => InvokeProxyMethod<List<object>>(proxy, nameof(SearchForNewEntity), albumTitle),
                containsItem: ContainsEntity,
                isValidQuery: () => IsValidQuery(albumTitle),
                supportSelector: s => s.CanHandleSearch(albumTitle: albumTitle)
            ).ExecuteSearch();

        #endregion

        private void InitializeAdaptiveThreshold()
        {
            if (MixedMetadataProxySettings.Instance?.DynamicThresholdMode == true)
                _adaptiveThreshold.LoadConfig(MixedMetadataProxySettings.Instance?.WeightsPath);
        }

        private List<Artist> ExecuteArtistSearch(string lidarrId, int metadataProfileId, Artist? baseArtist, HashSet<IProxy> usedProxies) =>
            new ProxyDecisionHandler<Artist>(
                mixedProxy: this,
                searchExecutor: proxy => ExecuteSingleProxyArtistSearch(proxy, lidarrId, metadataProfileId, baseArtist, usedProxies),
                containsItem: ContainsArtistInfo,
                isValidQuery: null,
                supportSelector: s => DetermineSupportLevel(s, lidarrId, baseArtist)
            ).ExecuteSearch();

        private List<Artist> ExecuteSingleProxyArtistSearch(IProxy proxy, string lidarrId, int metadataProfileId, Artist? baseArtist, HashSet<IProxy> usedProxies)
        {
            ISupportMetadataMixing mixingProxy = (ISupportMetadataMixing)proxy;
            _logger.Debug($"Checking proxy: {proxy.Name}");

            Artist? proxyArtist = TryGetArtistFromProxy(proxy, mixingProxy, lidarrId, metadataProfileId, baseArtist);

            if (HasValidAlbumData(proxyArtist))
            {
                _logger.Trace($"Proxy {proxy.Name} retrieved {proxyArtist!.Albums!.Value.Count} albums.");
                usedProxies.Add(proxy);
            }

            return new List<Artist> { proxyArtist! };
        }

        private Artist? TryGetArtistFromProxy(IProxy proxy, ISupportMetadataMixing mixingProxy,
            string lidarrId, int metadataProfileId, Artist? baseArtist)
        {
            if (mixingProxy.CanHandleId(lidarrId) == MetadataSupportLevel.Supported)
            {
                _logger.Debug($"Proxy {proxy.Name} can handle ID {lidarrId} directly.");
                return InvokeProxyMethod<Artist>(proxy, nameof(GetArtistInfo), lidarrId, metadataProfileId);
            }

            if (baseArtist?.Metadata?.Value?.Links != null)
            {
                _logger.Trace($"Checking if proxy {proxy.Name} supports links for base artist {baseArtist.Name}");
                string? proxyID = mixingProxy.SupportsLink(baseArtist.Metadata.Value.Links);
                if (proxyID != null)
                {
                    _logger.Debug($"Proxy {proxy.Name} found matching link-based ID: {proxyID}");
                    return InvokeProxyMethod<Artist>(proxy, nameof(GetArtistInfo), proxyID, metadataProfileId);
                }
            }

            return null;
        }

        private MetadataSupportLevel DetermineSupportLevel(ISupportMetadataMixing supportMixing, string lidarrId, Artist? baseArtist)
        {
            if (supportMixing.CanHandleId(lidarrId) == MetadataSupportLevel.Supported)
                return MetadataSupportLevel.Supported;

            if (MixedMetadataProxySettings.Instance?.PopulateWithMultipleProxies == true)
            {
                if (supportMixing.SupportsLink(baseArtist?.Metadata?.Value?.Links ?? new()) != null || MixedMetadataProxySettings.Instance?.TryFindArtist == true)
                    return MetadataSupportLevel.ImplicitSupported;
            }

            _logger.Trace($"Support selector: {supportMixing.Name} does not support {lidarrId}");
            return MetadataSupportLevel.Unsupported;
        }

        private Dictionary<IProxy, List<Album>> BuildProxyAlbumMap(Artist? baseArtist)
        {
            Dictionary<IProxy, List<Album>> proxyAlbumMap = new();

            if (baseArtist?.Albums?.Value?.Any() != true)
                return proxyAlbumMap;

            _logger.Trace($"Base artist has {baseArtist.Albums.Value.Count} albums, checking proxy support...");

            foreach (Album? album in baseArtist.Albums.Value)
            {
                IProxy? candidate = FindProxyForAlbum(album.ForeignAlbumId);
                if (candidate != null)
                {
                    _logger.Trace($"Album '{album.Title}' is supported by proxy: {candidate.Name}");
                    proxyAlbumMap.TryAdd(candidate, new List<Album>());
                    proxyAlbumMap[candidate].Add(album);
                }
            }

            return proxyAlbumMap;
        }

        private IProxy? FindProxyForAlbum(string foreignAlbumId) =>
            ProxyService.Value.ActiveProxies
                .OfType<ISupportMetadataMixing>()
                .Cast<IProxy>()
                .FirstOrDefault(p => ((ISupportMetadataMixing)p).CanHandleId(foreignAlbumId) == MetadataSupportLevel.Supported);

        private Artist MergeAllArtistData(List<Artist> newArtists, Artist? baseArtist, string lidarrId,
            Dictionary<IProxy, List<Album>> proxyAlbumMap, HashSet<IProxy> usedProxies)
        {
            List<Artist> validArtists = newArtists.Where(x => x != null).ToList();
            Artist? mergedArtist = validArtists.Find(x => x.ForeignArtistId == lidarrId)
                ?? baseArtist ?? validArtists.FirstOrDefault();

            if (mergedArtist == null)
                return null!;

            AddAlbumsFromUnusedProxies(mergedArtist, proxyAlbumMap, usedProxies);
            MergeAdditionalArtists(mergedArtist, validArtists);

            _logger.Info($"Final merged artist: {mergedArtist.Name} with {mergedArtist.Albums?.Value?.Count ?? 0} albums.");
            return mergedArtist;
        }

        private void AddAlbumsFromUnusedProxies(Artist mergedArtist, Dictionary<IProxy, List<Album>> proxyAlbumMap, HashSet<IProxy> usedProxies)
        {
            foreach ((IProxy proxy, List<Album> albums) in proxyAlbumMap.Where(kvp => !usedProxies.Contains(kvp.Key)))
            {
                _logger.Debug($"Adding old albums from proxy {proxy.Name} to merged artist {mergedArtist.Name}");
                AddOldAlbums(mergedArtist, albums);
            }
        }

        private static void MergeAdditionalArtists(Artist mergedArtist, List<Artist> validArtists) =>
            validArtists.Where(a => a != mergedArtist).ToList()
            .ForEach(artist => MergeArtists(mergedArtist, artist));

        internal List<ProxyCandidate> GetCandidateProxies(Func<ISupportMetadataMixing, MetadataSupportLevel> supportSelector)
        {
            List<ProxyCandidate> candidates = ProxyService.Value.ActiveProxies
                .Where(p => p != this && p is ISupportMetadataMixing)
                .Select(p => CreateProxyCandidate(p, supportSelector))
                .Where(c => c.Support != MetadataSupportLevel.Unsupported)
                .ToList();

            if (!candidates.Any())
                return candidates;

            BoostInternalProxyPriorities(candidates);
            return candidates.OrderBy(c => c.Priority).ThenByDescending(c => c.Support).ToList();
        }

        private static ProxyCandidate CreateProxyCandidate(IProxy proxy, Func<ISupportMetadataMixing, MetadataSupportLevel> supportSelector) => new()
        {
            Proxy = proxy,
            Priority = GetPriority(proxy.Name ?? string.Empty),
            Support = supportSelector((ISupportMetadataMixing)proxy)
        };

        private static void BoostInternalProxyPriorities(List<ProxyCandidate> candidates)
        {
            int maxPriority = candidates.Max(c => c.Priority);
            foreach (ProxyCandidate? candidate in candidates.Where(c => c.Proxy.GetProxyMode() == ProxyMode.Internal))
                candidate.Priority = maxPriority;
        }

        private HashSet<string> GetChanged(Func<IProxy, HashSet<string>> func)
        {
            HashSet<string> result = new();
            List<ProxyCandidate> candidates = GetCandidateProxies(x => x.CanHandleChanged());

            foreach (IProxy? proxy in candidates.Select(c => c.Proxy))
            {
                try
                {
                    HashSet<string> changed = func(proxy);
                    if (changed != null)
                        result.UnionWith(changed);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"GetChanged: Exception from proxy {proxy.Name ?? "Unknown"}");
                }
            }

            return result;
        }

        internal int CalculateThreshold(string proxyName, int aggregatedCount) =>
            MixedMetadataProxySettings.Instance?.DynamicThresholdMode == true
                ? _adaptiveThreshold.GetDynamicThreshold(proxyName, aggregatedCount)
                : GetThreshold(aggregatedCount);

        #region Utility Methods

        private static int GetPriority(string proxyName)
        {
            if (string.IsNullOrWhiteSpace(proxyName) || MixedMetadataProxySettings.Instance?.Priotities == null)
                return DEFAULT_PRIORITY;

            KeyValuePair<string, string> matchingPriority = MixedMetadataProxySettings.Instance.Priotities
                .FirstOrDefault(x => string.Equals(x.Key, proxyName, StringComparison.OrdinalIgnoreCase));

            return !string.IsNullOrWhiteSpace(matchingPriority.Value) && int.TryParse(matchingPriority.Value, out int priority)
                ? priority
                : DEFAULT_PRIORITY;
        }

        private static int GetThreshold(int aggregatedCount) => aggregatedCount switch
        {
            < 10 => 5,
            < 50 => 3,
            _ => 1
        };

        private static bool HasValidAlbumData(Artist? artist) =>
            artist?.Albums?.Value != null && artist.Albums.Value.Count > 0;

        private static bool IsValidQuery(params string[] queries) =>
            !queries.Any(query => string.IsNullOrWhiteSpace(query) ||
                                 query.Length < MIN_QUERY_LENGTH ||
                                 !query.Any(char.IsLetter));

        private static bool ContainsAlbum(List<Album> albums, Album newAlbum) =>
            albums.Any(album => Fuzz.Ratio(album.Title, newAlbum.Title) > ALBUM_SIMILARITY_THRESHOLD);

        private static bool ContainsArtist(List<Artist> artists, Artist newArtist) =>
            artists.Any(artist => Fuzz.Ratio(artist.Name, newArtist.Name) > ARTIST_SIMILARITY_THRESHOLD);

        private static bool ContainsArtistInfo(List<Artist> artists, Artist newArtist)
        {
            if (artists.Count == 0 || newArtist?.Albums?.Value == null)
                return false;

            return newArtist.Albums.Value.All(album =>
                artists.Any(existing => existing.Albums?.Value != null &&
                                      ContainsAlbum(existing.Albums.Value, album)));
        }

        private static bool ContainsEntity(List<object> entities, object newEntity) => newEntity switch
        {
            Album newAlbum => ContainsAlbum(entities.OfType<Album>().ToList(), newAlbum),
            Artist newArtist => ContainsArtist(entities.OfType<Artist>().ToList(), newArtist),
            _ => entities.Any(e => string.Equals(e.ToString(), newEntity.ToString(), StringComparison.Ordinal))
        };

        private static Artist MergeArtists(Artist baseArtist, Artist newArtist)
        {
            if (newArtist.Albums?.Value == null)
                return baseArtist;

            EnsureAlbumsCollection(baseArtist);
            MergeLinks(baseArtist, newArtist);
            MergeAlbums(baseArtist, newArtist);

            return baseArtist;
        }

        private static void EnsureAlbumsCollection(Artist artist) =>
            artist.Albums ??= new LazyLoaded<List<Album>>(new List<Album>());


        private static void MergeLinks(Artist baseArtist, Artist newArtist)
        {
            List<Links>? newLinks = newArtist.Metadata?.Value?.Links?
                .Where(newLink => baseArtist.Metadata?.Value?.Links?.Any(baseLink =>
                    string.Equals(baseLink.Url, newLink.Url, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(baseLink.Name, newLink.Name, StringComparison.OrdinalIgnoreCase)) == false)
                .ToList();

            if (newLinks?.Any() == true)
                baseArtist.Metadata?.Value?.Links?.AddRange(newLinks);
        }

        private static void MergeAlbums(Artist baseArtist, Artist newArtist)
        {
            IEnumerable<Album> uniqueAlbums = newArtist.Albums!.Value
                .Where(album => !ContainsAlbum(baseArtist.Albums!.Value, album));

            baseArtist.Albums = baseArtist.Albums.Value.Union(uniqueAlbums).ToList();
        }

        private static Artist AddOldAlbums(Artist artist, List<Album> oldAlbums)
        {
            EnsureAlbumsCollection(artist);
            IEnumerable<Album> uniqueOldAlbums = oldAlbums.Where(album => !ContainsAlbum(artist.Albums!.Value, album));
            artist.Albums = artist.Albums.Value.Union(uniqueOldAlbums).ToList();
            return artist;
        }

        #endregion
    }
}