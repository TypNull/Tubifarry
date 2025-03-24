using FluentValidation.Results;
using FuzzySharp;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Music;
using Tubifarry.Metadata.Proxy.Core;

namespace Tubifarry.Metadata.Proxy.Mixed
{
    public class MixedMetadataProxy : ConsumerProxyPlaceholder<MixedMetadataProxySettings>, IMetadata, IMixedProxy
    {
        public override string Name => "MetaMix";
        private readonly Logger _logger;

        private readonly IArtistService _artistService;
        internal readonly IProvideAdaptiveThreshold _adaptiveThreshold;

        public MixedMetadataProxy(Lazy<IProxyService> proxyService, IProvideAdaptiveThreshold adaptiveThreshold, IArtistService artistService, Logger logger) : base(proxyService)
        {
            _logger = logger;
            _adaptiveThreshold = adaptiveThreshold;
            _artistService = artistService;

            if (MixedMetadataProxySettings.Instance?.DynamicThresholdMode == true)
                _adaptiveThreshold.LoadConfig(MixedMetadataProxySettings.Instance?.WeightsPath);
        }

        #region Abstract Methods

        public override Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string id)
        {
            List<ProxyCandidate> candidates = GetCandidateProxies((x) => x.CanHandleId(id));

            if (!candidates.Any())
                throw new NotImplementedException($"No proxy available to handle album id: {id}");

            ProxyCandidate selected = candidates[0];
            _logger.Trace($"GetAlbumInfo: Using proxy {selected.Proxy.Definition.Name} with priority {selected.Priority} for album id {id}");
            return selected.Proxy.GetAlbumInfo(id);
        }

        public override Artist GetArtistInfo(string lidarrId, int metadataProfileId)
        {
            _logger.Trace($"Fetching artist info for Lidarr ID: {lidarrId} with Metadata Profile ID: {metadataProfileId}");

            Artist? baseArtist = _artistService.FindById(lidarrId);

            List<IProxy> usedProxies = new();
            List<Artist> newArtists = new ProxyDecisionHandler<Artist>(
                mixedProxy: this,
                searchExecutor: proxy =>
                {
                    Artist? proxyArtist = null;
                    ISupportMetadataMixing mixingProxy = (ISupportMetadataMixing)proxy;
                    _logger.Debug($"Checking proxy: {proxy.Name}");

                    if (mixingProxy.CanHandleId(lidarrId) == MetadataSupportLevel.Supported)
                    {
                        _logger.Debug($"Proxy {proxy.Name} can handle ID {lidarrId} directly.");
                        proxyArtist = proxy.GetArtistInfo(lidarrId, metadataProfileId);
                    }
                    else if (baseArtist?.Metadata?.Value?.Links != null)
                    {
                        _logger.Trace($"Checking if proxy {proxy.Name} supports links for base artist {baseArtist.Name}");
                        string? proxyID = mixingProxy.SupportsLink(baseArtist.Metadata.Value.Links);
                        if (proxyID != null)
                        {
                            _logger.Debug($"Proxy {proxy.Name} found matching link-based ID: {proxyID}");
                            proxyArtist = proxy.GetArtistInfo(proxyID, metadataProfileId);
                        }
                    }

                    if (proxyArtist?.Albums?.Value != null && proxyArtist.Albums.Value.Count > 0)
                    {
                        _logger.Trace($"Proxy {proxy.Name} retrieved {proxyArtist.Albums.Value.Count} albums.");
                        usedProxies.Add(proxy);
                    }

                    return new List<Artist> { proxyArtist! };
                },
                containsItem: ContainsArtistInfo,
                isValidQuery: null,
                supportSelector: s =>
                {
                    if (s.CanHandleId(lidarrId) == MetadataSupportLevel.Supported)
                    {
                        return MetadataSupportLevel.Supported;
                    }
                    else if (MixedMetadataProxySettings.Instance?.PopulateWithMultipleProxies == true)
                    {
                        if (s.SupportsLink(baseArtist?.Metadata?.Value?.Links ?? new()) != null)
                            return MetadataSupportLevel.ImplicitSupported;
                        else if (MixedMetadataProxySettings.Instance?.TryFindArtist == true)
                            return MetadataSupportLevel.ImplicitSupported;
                    }
                    _logger.Trace($"Support selector: {s.Name} does not support {lidarrId}");
                    return MetadataSupportLevel.Unsupported;
                }
            ).ExecuteSearch();

            _logger.Debug($"Total new artists found: {newArtists.Count}");

            Dictionary<IProxy, List<Album>> proxyAlbumMap = new();
            if (baseArtist?.Albums?.Value.Any() == true)
            {
                _logger.Trace($"Base artist has {baseArtist.Albums.Value.Count} albums, checking proxy support...");
                foreach (Album album in baseArtist.Albums.Value)
                {
                    IProxy? candidate = _proxyService.Value.ActiveProxys
                        .Where(p => p is ISupportMetadataMixing)
                        .FirstOrDefault(p => ((ISupportMetadataMixing)p).CanHandleId(album.ForeignAlbumId) == MetadataSupportLevel.Supported);

                    if (candidate != null)
                    {
                        _logger.Trace($"Album '{album.Title}' is supported by proxy: {candidate.Name}");
                        if (!proxyAlbumMap.ContainsKey(candidate))
                            proxyAlbumMap[candidate] = new List<Album>();

                        proxyAlbumMap[candidate].Add(album);
                    }
                }
            }

            newArtists = newArtists.Where(x => x != null).ToList();
            Artist? mergedArtist = newArtists.Find(x => x.ForeignArtistId == lidarrId) ?? baseArtist ?? newArtists.FirstOrDefault();

            if (mergedArtist == null)
                return null!;
            foreach (KeyValuePair<IProxy, List<Album>> kvp in proxyAlbumMap)
            {
                IProxy proxy = kvp.Key;
                if (!usedProxies.Contains(proxy))
                {
                    _logger.Debug($"Adding old albums from proxy {proxy.Name} to merged artist {mergedArtist.Name}");
                    mergedArtist = AddOldAlbums(mergedArtist, kvp.Value);
                }
            }

            foreach (Artist artist in newArtists)
            {
                if (artist == mergedArtist)
                    continue;
                mergedArtist = MergeArtists(mergedArtist, artist);
            }

            _logger.Info($"Final merged artist: {mergedArtist.Name} with {mergedArtist.Albums?.Value.Count} albums.");
            return mergedArtist;
        }


        public override HashSet<string> GetChangedAlbums(DateTime startTime) => GetChanged(x => x.GetChangedAlbums(startTime));
        public override HashSet<string> GetChangedArtists(DateTime startTime) => GetChanged(x => x.GetChangedArtists(startTime));

        private HashSet<string> GetChanged(Func<IProxy, HashSet<string>> func)
        {
            HashSet<string> result = new();
            foreach (IProxy proxy in GetCandidateProxies(x => x.CanHandleChanged()).Cast<IProxy>())
            {
                try
                {
                    HashSet<string> changed = func(proxy);
                    if (changed != null)
                        result.UnionWith(changed);
                }
                catch (Exception ex) { _logger.Error(ex, $"GetChanged: Exception from proxy {proxy.Definition.Name}"); }
            }
            return result;
        }

        public override ValidationResult Test()
        {
            _logger.Debug("Test");
            return new ValidationResult();
        }

        #region Search Methods Using the Generic Handler

        public override List<Album> SearchForNewAlbum(string albumTitle, string artistName) => new ProxyDecisionHandler<Album>(
                mixedProxy: this,
                searchExecutor: proxy => proxy.SearchForNewAlbum(albumTitle, artistName),
                containsItem: ContainsAlbum,
                isValidQuery: () => IsValidQuery(albumTitle, artistName),
                supportSelector: s => s.CanHandleSearch(albumTitle, artistName)
            ).ExecuteSearch();

        public override List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds) => new ProxyDecisionHandler<Album>(
                mixedProxy: this,
                searchExecutor: proxy => proxy.SearchForNewAlbumByRecordingIds(recordingIds),
                containsItem: ContainsAlbum,
                isValidQuery: () => true,
                supportSelector: s => s.CanHandleIRecordingIds(recordingIds.ToArray())
            ).ExecuteSearch();

        public override List<Artist> SearchForNewArtist(string artistName) => new ProxyDecisionHandler<Artist>(
                mixedProxy: this,
                searchExecutor: proxy => proxy.SearchForNewArtist(artistName),
                containsItem: ContainsArtist,
                isValidQuery: () => IsValidQuery(artistName),
                supportSelector: s => s.CanHandleSearch(artistName: artistName)
            ).ExecuteSearch();

        public override List<object> SearchForNewEntity(string albumTitle) => new ProxyDecisionHandler<object>(
                mixedProxy: this,
                searchExecutor: proxy => proxy.SearchForNewEntity(albumTitle),
                containsItem: ContainsEntity,
                isValidQuery: () => IsValidQuery(albumTitle),
                supportSelector: s => s.CanHandleSearch(albumTitle: albumTitle)
            ).ExecuteSearch();

        #endregion

        #endregion

        #region Helper Methods and Classes

        private static int GetPriority(string proxyName)
        {
            if (MixedMetadataProxySettings.Instance?.Priotities != null)
            {
                KeyValuePair<string, string> kvp = MixedMetadataProxySettings.Instance.Priotities.FirstOrDefault(x => string.Equals(x.Key, proxyName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(kvp.Value) && int.TryParse(kvp.Value, out int priority))
                    return priority;
            }
            return 50;
        }

        private static int GetThreshold(int aggregatedCount)
        {
            if (aggregatedCount < 10)
                return 5;
            else if (aggregatedCount < 50)
                return 3;
            else
                return 1;
        }

        internal int CalculateThreshold(string proxyName, int aggregatedCount) => MixedMetadataProxySettings.Instance?.DynamicThresholdMode == true ?
            _adaptiveThreshold.GetDynamicThreshold(proxyName, aggregatedCount) :
            GetThreshold(aggregatedCount);

        internal List<ProxyCandidate> GetCandidateProxies(Func<ISupportMetadataMixing, MetadataSupportLevel> supportSelector)
        {
            List<ProxyCandidate> candidates = _proxyService.Value.ActiveProxys
                .Where(p => p != this && p is ISupportMetadataMixing)
                .Select(p => new ProxyCandidate
                {
                    Proxy = p,
                    Priority = GetPriority(p.Definition.Name),
                    Support = supportSelector((ISupportMetadataMixing)p)
                })
                .Where(c => c.Support != MetadataSupportLevel.Unsupported)
                .ToList();

            int maxPriority = candidates.Max(c => c.Priority);
            foreach (ProxyCandidate? candidate in candidates.Where(c => c.Proxy.ProxyMode == ProxyMode.Internal))
                candidate.Priority = maxPriority;
            return candidates.OrderBy(c => c.Priority).ThenByDescending(c => c.Support).ToList();
        }

        private static bool ContainsAlbum(List<Album> albums, Album newAlbum) => albums.Any(album => Fuzz.Ratio(album.Title, newAlbum.Title) > 60);
        private static bool ContainsArtist(List<Artist> artists, Artist newArtist) => artists.Any(artist => Fuzz.Ratio(artist.Name, newArtist.Name) > 70);

        private static bool ContainsArtistInfo(List<Artist> artists, Artist newArtist)
        {
            if (artists.Count == 0)
                return false;

            foreach (Album? album in newArtist?.Albums?.Value ?? new List<Album>())
                foreach (Artist existing in artists)
                    if (!ContainsAlbum(existing.Albums.Value, album))
                        return false;
            return true;
        }

        private static bool ContainsEntity(List<object> entities, object newEntity)
        {
            if (newEntity is Album newAlbum)
                return ContainsAlbum(entities.OfType<Album>().ToList(), newAlbum);
            if (newEntity is Artist newArtist)
                return ContainsArtist(entities.OfType<Artist>().ToList(), newArtist);
            return entities.Any(e => e.ToString() == newEntity.ToString());
        }

        private static bool IsValidQuery(params string[] queries) => !queries.Any(query => string.IsNullOrWhiteSpace(query) || query.Length < 5 || !query.Any(char.IsLetter));

        private static Artist MergeArtists(Artist baseArtist, Artist newArtist)
        {
            if (newArtist.Albums?.Value != null)
            {
                baseArtist.Albums ??= new LazyLoaded<List<Album>>(new List<Album>());
                List<Links> newLinks = newArtist.Metadata.Value.Links
              .Where(newLink => !baseArtist.Metadata.Value.Links.Any(baseLink => string.Equals(baseLink.Url, newLink.Url, StringComparison.OrdinalIgnoreCase) || string.Equals(baseLink.Name, newLink.Name, StringComparison.OrdinalIgnoreCase)))
              .ToList();

                baseArtist.Metadata.Value.Links.AddRange(newLinks);
                baseArtist.Albums = baseArtist.Albums.Value.Union(newArtist.Albums.Value.Where(a => !ContainsAlbum(baseArtist.Albums.Value, a)))
                    .ToList();
            }

            return baseArtist;
        }

        private static Artist AddOldAlbums(Artist artist, List<Album> oldAlbums)
        {
            artist.Albums ??= new LazyLoaded<List<Album>>(new List<Album>());
            artist.Albums = artist.Albums.Value.Union(oldAlbums.Where(a => !ContainsAlbum(artist.Albums.Value, a))).ToList();
            return artist;
        }

        #endregion
    }
}
