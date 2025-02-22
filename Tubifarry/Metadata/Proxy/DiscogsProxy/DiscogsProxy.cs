using FuzzySharp;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Music;
using System.Collections.Concurrent;
using Tubifarry.Core.Model;
using Tubifarry.ImportLists.WantedList;

namespace Tubifarry.Metadata.Proxy.DiscogsProxy
{
    public class DiscogsProxy : IDiscogsProxy
    {
        private const string _identifier = "@discogs";
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, CacheEntry<object>> _cache = new();
        private readonly TimeSpan CacheDuration = TimeSpan.FromHours(10);
        private readonly IArtistService _artistService;
        private readonly IAlbumService _albumService;
        private readonly IHttpClient _httpClient;
        private FileCache? _permanentCache;


        public DiscogsProxy(Logger logger, IHttpClient httpClient, IArtistService artistService, IAlbumService albumService)
        {
            _logger = logger;
            _httpClient = httpClient;
            _artistService = artistService;
            _albumService = albumService;
        }

        private async Task<T?> GetCachedAsync<T>(DiscogsMetadataProxySettings settings, string key)
        {
            if (settings.CacheType == CacheType.Permanent)
            {
                _permanentCache ??= new FileCache(settings.CacheDirectory);
                T? result = await _permanentCache.GetAsync<T>(key);
                if (result != null)
                    _logger.Trace($"Permanent cache hit: {key}");
                return result;
            }
            else
            {
                if (_cache.TryGetValue(key, out CacheEntry<object>? entry) &&
                    (DateTime.Now - entry.Timestamp) < CacheDuration &&
                    entry.Value is T cached)
                {
                    _logger.Trace($"Memory cache hit: {key}");
                    return cached;
                }
                return default;
            }
        }

        private async Task SetCachedAsync(DiscogsMetadataProxySettings settings, string key, object value)
        {
            if (settings.CacheType == CacheType.Permanent)
            {
                _permanentCache ??= new FileCache(settings.CacheDirectory);
                await _permanentCache.SetAsync(key, value, CacheDuration);
            }
            else
            {
                _cache[key] = new CacheEntry<object> { Timestamp = DateTime.Now, Value = value };
            }
        }

        private class CacheEntry<T>
        {
            public DateTime Timestamp { get; set; }
            public T Value { get; set; } = default!;
        }

        private async Task<List<T>> CachedSearchAsync<T>(DiscogsMetadataProxySettings settings, string querry, Func<DiscogsSearchItem, T?> mapper, string kind = "all", string? artist = null)
        {
            string key = $"{kind}:{querry}:{artist ?? ""}";
            List<DiscogsSearchItem>? results = await GetCachedAsync<List<DiscogsSearchItem>>(settings, key);
            if (results == null)
            {
                DiscogsApiService apiService = new(_httpClient) { AuthToken = settings.AuthToken, PageSize = settings.PageSize, MaxPageLimit = settings.PageNumber };
                results = await apiService.SearchAsync(new() { Query = querry, Artist = artist, Type = kind == "all" ? string.Empty : kind });
            }
            List<T> mapped = results.Select(r => mapper(r)).Where(x => x != null).ToList()!;
            await SetCachedAsync(settings, key, results);
            return mapped;
        }

        public List<Album> SearchNewAlbum(DiscogsMetadataProxySettings settings, string title, string artist)
        {
            _logger.Debug($"SearchNewAlbum: title '{title}', artist '{artist}'");
            try
            {
                //Check for artist id in artist string! and master and release
                List<Album> albums = CachedSearchAsync(settings, title, r =>
                {
                    DiscogsApiService apiService = new(_httpClient) { AuthToken = settings.AuthToken, PageSize = settings.PageSize };
                    DiscogsRelease? release = apiService.GetReleaseAsync(r.Id).GetAwaiter().GetResult();
                    return DiscogsMappingHelper.MapAlbumFromRelease(release!);
                }, "release", artist).GetAwaiter().GetResult();
                return albums.GroupBy(a => new { a.CleanTitle, a.AlbumType }).Select(g => g.First()).ToList();
            }
            catch (Exception ex)
            {
                _logger.Error($"SearchNewAlbum error: {ex}");
                throw;
            }
        }

        public List<Artist> SearchNewArtist(DiscogsMetadataProxySettings settings, string title) => CachedSearchAsync(settings, title, DiscogsMappingHelper.MapArtistFromSearchItem, "artist", null).GetAwaiter().GetResult();

        public List<object> SearchNewEntity(DiscogsMetadataProxySettings settings, string query)
        {
            query = SanitizeToUnicode(query);
            _logger.Trace($"SearchNewEntity invoked: query '{query}'");
            DiscogsApiService apiService = new(_httpClient)
            {
                AuthToken = settings.AuthToken,
                PageSize = settings.PageSize,
                MaxPageLimit = settings.PageNumber
            };

            if (IsDiscogsidQuery(query))
            {
                query = query.Replace("discogs:", "").Replace("discogsid:", "");
                if (int.TryParse(query, out int discogsId))
                {
                    DiscogsArtist? artistResult = apiService.GetArtistAsync(discogsId).GetAwaiter().GetResult();
                    if (artistResult != null)
                        return new List<object> { DiscogsMappingHelper.MapArtistFromDiscogsArtist(artistResult) };

                    DiscogsRelease? releaseResult = apiService.GetReleaseAsync(discogsId).GetAwaiter().GetResult();
                    if (releaseResult != null)
                        return new List<object> { DiscogsMappingHelper.MapAlbumFromRelease(releaseResult) };

                    DiscogsMasterRelease? masterResult = apiService.GetMasterReleaseAsync(discogsId).GetAwaiter().GetResult();
                    if (masterResult != null)
                        return new List<object> { DiscogsMappingHelper.MapAlbumFromMasterRelease(masterResult) };
                }
            }

            List<object> mappedResults = new();
            foreach (DiscogsSearchItem? item in CachedSearchAsync(settings, query, x => x).GetAwaiter().GetResult())
            {
                switch (item.Type?.ToLowerInvariant())
                {
                    case "artist":
                        DiscogsArtist? artistResult = GetCachedAsync<DiscogsArtist>(settings, $"artist:{item.Id + _identifier}").GetAwaiter().GetResult();
                        if (artistResult != null)
                            mappedResults.Add(DiscogsMappingHelper.MapArtistFromDiscogsArtist(artistResult));
                        else
                            mappedResults.Add(DiscogsMappingHelper.MapArtistFromSearchItem(item));
                        break;
                    case "release":
                        DiscogsRelease? release = GetCachedAsync<DiscogsRelease>(settings, $"release:{item.Id + _identifier}").GetAwaiter().GetResult() ?? apiService.GetReleaseAsync(item.Id).GetAwaiter().GetResult();
                        if (release != null)
                        {
                            mappedResults.Add(DiscogsMappingHelper.MapAlbumFromRelease(release));
                            SetCachedAsync(settings, $"release:{item.Id + _identifier}", release).GetAwaiter();
                        }
                        break;
                    case "master":
                        DiscogsMasterRelease? master = GetCachedAsync<DiscogsMasterRelease>(settings, $"master:{item.Id + _identifier}").GetAwaiter().GetResult() ?? apiService.GetMasterReleaseAsync(item.Id).GetAwaiter().GetResult();
                        if (master != null)
                        {
                            mappedResults.Add(DiscogsMappingHelper.MapAlbumFromMasterRelease(master));
                            SetCachedAsync(settings, $"master:{item.Id + _identifier}", master).GetAwaiter();
                        }
                        break;
                }
            }
            return mappedResults;
        }

        public async Task<Tuple<string, Album, List<ArtistMetadata>>> GetAlbumInfoAsync(DiscogsMetadataProxySettings settings, string foreignAlbumId)
        {
            _logger.Trace("Starting GetAlbumInfoAsync for AlbumId: {0}", foreignAlbumId);
            Album? existingAlbum = _albumService.FindById(foreignAlbumId);

            // Determine if we should use the master release details
            bool useMaster = existingAlbum?.SecondaryTypes.Any(st => st.Id == 36) ?? false;
            _logger.Trace("Using {0} release details for AlbumId: {1}", useMaster ? "Master" : "release", foreignAlbumId);

            DiscogsApiService apiService = new(_httpClient) { AuthToken = settings.AuthToken };

            // Fetch release details based on the release type and retrieve primary artist information
            (Album mappedAlbum, object releaseForTracks) = useMaster ? await GetMasterReleaseDetailsAsync(settings, foreignAlbumId, apiService) : await GetReleaseDetailsAsync(settings, foreignAlbumId, apiService);
            DiscogsArtist? discogsArtist = await GetPrimaryArtistAsync(settings, foreignAlbumId, useMaster, existingAlbum!);

            // Process artist info and mapping
            Artist existingArtist = (existingAlbum?.Artist?.Value ?? (discogsArtist != null ? DiscogsMappingHelper.MapArtistFromDiscogsArtist(discogsArtist) : null)) ?? throw new ModelNotFoundException(typeof(Artist), 0);
            _logger.Trace("Processed artist information for ArtistId: {0}", existingArtist.ForeignArtistId);
            existingArtist.Albums ??= new LazyLoaded<List<Album>>(new List<Album>());

            mappedAlbum.Artist = existingArtist;
            mappedAlbum.ArtistMetadata = existingArtist.Metadata;
            mappedAlbum.ArtistMetadataId = existingArtist.ArtistMetadataId;

            // Merge album details
            Album finalAlbum = DiscogsMappingHelper.MergeAlbums(existingAlbum!, mappedAlbum);
            AlbumRelease albumRelease = finalAlbum.AlbumReleases.Value[0];
            List<Track> tracks = DiscogsMappingHelper.MapTracks(releaseForTracks, finalAlbum, albumRelease);
            _logger.Trace("Mapped {0} tracks for AlbumId: {1}", tracks.Count, foreignAlbumId);

            albumRelease.TrackCount = tracks.Count;
            albumRelease.Duration = tracks.Sum(x => x.Duration);
            albumRelease.Monitored = tracks.Count > 0;
            albumRelease.Tracks = tracks;

            _logger.Trace("Completed processing for AlbumId: {0}. Total Tracks: {1}", foreignAlbumId, tracks.Count);
            return new Tuple<string, Album, List<ArtistMetadata>>(existingArtist.ForeignArtistId, finalAlbum, new List<ArtistMetadata> { existingArtist.Metadata.Value });
        }


        private async Task<(Album, object)> GetMasterReleaseDetailsAsync(DiscogsMetadataProxySettings settings, string id, DiscogsApiService apiService)
        {
            string masterKey = $"master:{id}";
            DiscogsMasterRelease? masterRelease = await GetCachedAsync<DiscogsMasterRelease>(settings, masterKey) ?? await apiService.GetMasterReleaseAsync(int.Parse(RemoveIdentifier(id)));
            await SetCachedAsync(settings, masterKey, masterRelease!);
            return (DiscogsMappingHelper.MapAlbumFromMasterRelease(masterRelease!), masterRelease)!;
        }

        private async Task<(Album, object)> GetReleaseDetailsAsync(DiscogsMetadataProxySettings settings, string id, DiscogsApiService apiService)
        {
            string releaseKey = $"release:{id}";
            DiscogsRelease? release = await GetCachedAsync<DiscogsRelease>(settings, releaseKey) ?? await apiService.GetReleaseAsync(int.Parse(RemoveIdentifier(id)));
            await SetCachedAsync(settings, releaseKey, release!);
            return (DiscogsMappingHelper.MapAlbumFromRelease(release!), release)!;
        }

        private async Task<DiscogsArtist?> GetPrimaryArtistAsync(DiscogsMetadataProxySettings settings, string id, bool useMaster, Album existingAlbum)
        {
            string key = useMaster ? $"master:{id}" : $"release:{id}";
            object? release = useMaster ? await GetCachedAsync<DiscogsMasterRelease>(settings, key) : await GetCachedAsync<DiscogsRelease>(settings, key);
            IEnumerable<DiscogsArtist> artists = ((IEnumerable<DiscogsArtist>)(release as dynamic)?.Artists!) ?? Enumerable.Empty<DiscogsArtist>();
            return artists.FirstOrDefault(x => Fuzz.Ratio(x.Name, existingAlbum.Artist.Value.Name) > 80);
        }

        public async Task<Artist> GetArtistInfoAsync(DiscogsMetadataProxySettings settings, string foreignArtistId, int metadataProfileId)
        {
            _logger.Trace($"Fetching artist info for ID: {foreignArtistId}.");

            string artistCacheKey = $"artist:{foreignArtistId}";
            DiscogsArtist? artist = await GetCachedAsync<DiscogsArtist>(settings, artistCacheKey);

            if (artist == null)
            {
                _logger.Debug($"Artist not found in cache. Fetching from Discogs API for ID: {foreignArtistId}.");
                DiscogsApiService apiService = new(_httpClient) { AuthToken = settings.AuthToken };
                artist = await apiService.GetArtistAsync(int.Parse(RemoveIdentifier(foreignArtistId)));
            }

            Artist? existingArtist = _artistService.FindById(foreignArtistId);
            existingArtist ??= DiscogsMappingHelper.MapArtistFromDiscogsArtist(artist!);
            existingArtist.Albums = await FetchAlbumsForArtistAsync(settings, existingArtist, artist!.Id);
            existingArtist.MetadataProfileId = metadataProfileId;

            _logger.Trace($"Processed artist: {artist.Name} (ID: {existingArtist.ForeignArtistId}).");
            await SetCachedAsync(settings, artistCacheKey, artist);
            return existingArtist;
        }

        private async Task<List<Album>> FetchAlbumsForArtistAsync(DiscogsMetadataProxySettings settings, Artist artist, int foreignArtistId)
        {
            _logger.Trace($"Fetching albums for artist ID: {foreignArtistId}.");

            string key = $"ArtistRelease:{foreignArtistId}";
            List<DiscogsArtistRelease>? artistReleases = await GetCachedAsync<List<DiscogsArtistRelease>>(settings, key);

            if (artistReleases == null)
            {
                _logger.Debug($"Albums not found in cache. Fetching from Discogs API for artist ID: {foreignArtistId}.");
                DiscogsApiService apiService = new(_httpClient) { AuthToken = settings.AuthToken };
                artistReleases = await apiService.GetArtistReleasesAsync(foreignArtistId, null, 70);
            }

            List<Album> albums = new();
            foreach (DiscogsArtistRelease release in artistReleases)
            {
                if (release == null || release.Role != "Main")
                    continue;

                Album album = DiscogsMappingHelper.MapAlbumFromArtistRelease(release);
                album.Artist = artist;
                album.ArtistMetadata = artist.Metadata;
                albums.Add(album);
            }

            _logger.Trace($"Fetched {albums.Count} albums for artist ID: {foreignArtistId}.");
            await SetCachedAsync(settings, key, artistReleases);
            return albums;
        }

        public static bool IsDiscogsidQuery(string? query) => query?.StartsWith("discogs:") == true || query?.StartsWith("discogsid:") == true;
        private static string SanitizeToUnicode(string input) => string.IsNullOrEmpty(input) ? input : new string(input.Where(c => c <= 0xFFFF).ToArray());
        private static string RemoveIdentifier(string input) => input.EndsWith(_identifier, StringComparison.OrdinalIgnoreCase) ? input.Remove(input.Length - _identifier.Length) : input;

    }
}