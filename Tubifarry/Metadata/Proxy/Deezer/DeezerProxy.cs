using FuzzySharp;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Music;
using System.Collections.Concurrent;
using Tubifarry.Core.Model;
using Tubifarry.ImportLists.WantedList;

namespace Tubifarry.Metadata.Proxy.Deezer
{
    public class DeezerProxy : IDeezerProxy
    {
        private const string _identifier = "@deezer";
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, CacheEntry<object>> _cache = new();
        private readonly TimeSpan CacheDuration = TimeSpan.FromHours(10);
        private readonly IArtistService _artistService;
        private readonly IAlbumService _albumService;
        private readonly IHttpClient _httpClient;
        private readonly DeezerApiService _apiService;
        private FileCache? _permanentCache;

        public DeezerProxy(Logger logger, IHttpClient httpClient, IArtistService artistService, IAlbumService albumService)
        {
            _logger = logger;
            _httpClient = httpClient;
            _artistService = artistService;
            _albumService = albumService;
            _apiService = new(_httpClient);
        }

        private async Task<T?> GetCachedAsync<T>(DeezerMetadataProxySettings settings, string key)
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

        private async Task SetCachedAsync(DeezerMetadataProxySettings settings, string key, object value)
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

        private async Task<List<TReturn>> CachedSearchAsync<TReturn, TSearch>(DeezerMetadataProxySettings settings, string query, Func<TSearch, TReturn?> mapper, string? artist = null)
        {
            string key = $"{typeof(TSearch).Name}:{query}:{artist ?? ""}";
            List<TSearch>? results = await GetCachedAsync<List<TSearch>>(settings, key);
            if (results == null)
            {
                DeezerApiService apiService = new(_httpClient)
                {
                    PageSize = settings.PageSize,
                    MaxPageLimit = settings.PageNumber
                };
                results = await apiService.SearchAsync<TSearch>(new DeezerSearchParameter { Query = query, Artist = artist }) ?? new List<TSearch>();
            }
            List<TReturn> mapped = results.Select(r => mapper(r)).Where(x => x != null).ToList()!;
            await SetCachedAsync(settings, key, results);
            return mapped;
        }

        public async Task<List<Album>> GetArtistAlbumsAsync(DeezerMetadataProxySettings settings, Artist artist)
        {
            List<Album> albums = new();
            string artistAlbumsCacheKey = $"artist-albums:{artist.ForeignArtistId}";
            List<DeezerAlbum> albumResults = await GetCachedAsync<List<DeezerAlbum>>(settings, artistAlbumsCacheKey) ?? await _apiService.GetArtistDataAsync<DeezerAlbum>(int.Parse(RemoveIdentifier(artist.ForeignArtistId))) ?? new();
            _logger.Info(albumResults.Count > 0);
            foreach (DeezerAlbum albumD in albumResults)
            {
                Album album = DeezerMappingHelper.MapAlbumFromDeezerAlbum(albumD);
                album.Artist = artist;
                album.ArtistMetadata = artist.Metadata;
                album = DeezerMappingHelper.MergeAlbums(_albumService.FindById(artist.ForeignArtistId), album);
                albums.Add(album);
            }

            await SetCachedAsync(settings, artistAlbumsCacheKey, albumResults);
            return albums;
        }

        public List<Album> SearchNewAlbum(DeezerMetadataProxySettings settings, string title, string artist)
        {
            _logger.Debug($"SearchNewAlbum: title '{title}', artist '{artist}'");
            try
            {
                return CachedSearchAsync<Album, DeezerAlbum>(settings, title, item =>
                {
                    DeezerApiService apiService = new(_httpClient) { PageSize = settings.PageSize, MaxPageLimit = settings.PageNumber };
                    DeezerAlbum? albumDetails = apiService.GetAlbumAsync(item.Id).GetAwaiter().GetResult();
                    return albumDetails != null ? DeezerMappingHelper.MapAlbumFromDeezerAlbum(albumDetails) : null;
                }, artist).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Error($"SearchNewAlbum error: {ex}");
                throw;
            }
        }

        // For searching new artists, always fetch the full artist details.
        public List<Artist> SearchNewArtist(DeezerMetadataProxySettings settings, string title) =>
            CachedSearchAsync<Artist, DeezerArtist>(settings, "", artistD =>
        {
            Artist artist = DeezerMappingHelper.MapArtistFromDeezerArtist(artistD);
            artist.Albums = GetArtistAlbumsAsync(settings, artist).GetAwaiter().GetResult();
            return artist;
        }, title).GetAwaiter().GetResult();


        // For search, Deezer always returns track objects.
        // This method extracts distinct album IDs and distinct artist IDs
        // and then fetches full album details and full artist details separately.
        public List<object> SearchNewEntity(DeezerMetadataProxySettings settings, string query)
        {
            query = SanitizeToUnicode(query);
            _logger.Trace($"SearchNewEntity invoked: query '{query}'");
            DeezerApiService apiService = new(_httpClient)
            {
                PageSize = settings.PageSize,
                MaxPageLimit = settings.PageNumber
            };

            if (IsDeezerIdQuery(query))
            {
                query = query.Replace("deezer:", "").Replace("deezerid:", "");
                if (int.TryParse(query, out int deezerId))
                {
                    try
                    {
                        Artist artist = GetArtistInfoAsync(settings, deezerId.ToString(), 0).GetAwaiter().GetResult();
                        if (artist != null)
                            return new List<object> { artist };
                    }
                    catch { }

                    DeezerAlbum? albumResult = apiService.GetAlbumAsync(deezerId).GetAwaiter().GetResult();
                    if (albumResult != null)
                        return new List<object> { DeezerMappingHelper.MapAlbumFromDeezerAlbum(albumResult) };
                }
            }

            List<DeezerAlbum> searchItems = CachedSearchAsync<DeezerAlbum, DeezerAlbum>(settings, query, x => x).GetAwaiter().GetResult();
            List<ScoredAlbum> scoredAlbums = new();
            List<ScoredArtist> scoredArtists = new();

            foreach (DeezerAlbum? item in searchItems.DistinctBy(x => x.Id))
            {
                Artist? mappedArtist = scoredArtists.Find(x => x.Id == item.Artist.Id)?.Artist;
                if (mappedArtist == null)
                {
                    mappedArtist = _artistService.FindById(item.Artist.Id.ToString());
                    mappedArtist ??= DeezerMappingHelper.MapArtistFromDeezerArtist(item.Artist);
                    mappedArtist.Albums = GetArtistAlbumsAsync(settings, mappedArtist).GetAwaiter().GetResult();
                    _logger.Info(mappedArtist.Albums.Value.Count.ToString());
                    int scoreA = Fuzz.Ratio(query, mappedArtist!.Name);
                    scoredArtists.Add(new ScoredArtist
                    {
                        Artist = mappedArtist,
                        Score = scoreA,
                        Id = item.Artist.Id
                    });
                }

                DeezerAlbum? albumDetails = item;
                Album mappedAlbum = DeezerMappingHelper.MapAlbumFromDeezerAlbum(albumDetails ?? item);
                mappedAlbum.Artist = mappedArtist;
                mappedAlbum.ArtistMetadata = mappedArtist.Metadata;
                int score = Fuzz.Ratio(query, mappedAlbum.Title);
                scoredAlbums.Add(new ScoredAlbum
                {
                    Album = mappedAlbum,
                    Score = score,
                    ArtistId = item.Artist.Id
                });

                if (albumDetails != null)
                    SetCachedAsync(settings, $"album:{item.Id}{_identifier}", albumDetails!).GetAwaiter().GetResult();
            }

            return OrderSearchResults(scoredAlbums, scoredArtists);
        }

        private static List<object> OrderSearchResults(List<ScoredAlbum> scoredAlbums, List<ScoredArtist> scoredArtists)
        {
            List<object> orderedResults = new();
            Dictionary<object, int> allItemsMap = new();

            scoredAlbums.ForEach(sa => allItemsMap.Add(sa.Album, sa.Score));
            scoredArtists.ForEach(sa => allItemsMap.Add(sa.Artist, sa.Score));

            ScoredAlbum? topAlbum = scoredAlbums.OrderByDescending(a => a.Score).FirstOrDefault();
            ScoredArtist? topArtist = scoredArtists.OrderByDescending(a => a.Score).FirstOrDefault();

            if (topAlbum != null && (topArtist == null || topAlbum.Score > topArtist.Score))
            {
                orderedResults.Add(topAlbum.Album);

                ScoredArtist? albumArtist = scoredArtists.FirstOrDefault(a => a.Id == topAlbum.ArtistId);
                if (albumArtist != null) orderedResults.Add(albumArtist.Artist);

                IOrderedEnumerable<object> remaining = scoredAlbums.Where(a => a != topAlbum)
                    .Concat<object>(scoredArtists.Where(a => a != albumArtist)).OrderByDescending(x => x is ScoredAlbum ?
                        ((ScoredAlbum)x).Score :
                        ((ScoredArtist)x).Score);

                orderedResults.AddRange(Enumerable.Select<object, object>(remaining, x => x is ScoredAlbum ? ((ScoredAlbum)x).Album : ((ScoredArtist)x).Artist));
            }
            else if (topArtist != null)
            {
                orderedResults.Add(topArtist.Artist);

                IEnumerable<object> artistAlbums = scoredAlbums
                    .Where(a => a.ArtistId == topArtist.Id)
                    .OrderByDescending(a => a.Score)
                    .Take(3)
                    .Select(a => a.Album);

                orderedResults.AddRange(artistAlbums);

                IOrderedEnumerable<object> remaining = scoredAlbums.Where(a => !artistAlbums.Contains(a.Album))
                    .Concat<object>(scoredArtists.Where(a => a != topArtist))
                    .OrderByDescending(x => x is ScoredAlbum ?
                        ((ScoredAlbum)x).Score :
                        ((ScoredArtist)x).Score);

                orderedResults.AddRange(Enumerable.Select<object, object>(remaining, x => x is ScoredAlbum ? ((ScoredAlbum)x).Album : ((ScoredArtist)x).Artist));
            }

            return orderedResults;
        }

        // Helper classes
        private class ScoredAlbum
        {
            public Album Album { get; set; } = null!;
            public int Score { get; set; }
            public long ArtistId { get; set; }
        }

        private class ScoredArtist
        {
            public Artist Artist { get; set; } = null!;
            public int Score { get; set; }
            public long Id { get; set; }
        }

        public async Task<Tuple<string, Album, List<ArtistMetadata>>> GetAlbumInfoAsync(DeezerMetadataProxySettings settings, string foreignAlbumId)
        {
            _logger.Trace("Fetching album details for AlbumId: {0}", foreignAlbumId);
            string albumCacheKey = $"album:{foreignAlbumId}";
            Album? existingAlbum = _albumService.FindById(foreignAlbumId);
            DeezerAlbum albumDetails = await GetCachedAsync<DeezerAlbum>(settings, albumCacheKey) ?? await _apiService.GetAlbumAsync(int.Parse(RemoveIdentifier(foreignAlbumId))) ?? throw new Exception("Album not found from Deezer API.");
            Album mappedAlbum = DeezerMappingHelper.MapAlbumFromDeezerAlbum(albumDetails);
            Artist existingArtist = _artistService.FindById(mappedAlbum.Artist.Value.Metadata.Value.ForeignArtistId) ?? DeezerMappingHelper.MapArtistFromDeezerArtist(albumDetails.Artist);
            mappedAlbum.Artist = existingArtist;
            mappedAlbum.ArtistMetadata = existingArtist.Metadata;
            Album finalAlbum = existingAlbum != null ? DeezerMappingHelper.MergeAlbums(existingAlbum, mappedAlbum) : mappedAlbum;
            await SetCachedAsync(settings, albumCacheKey, albumDetails!);
            _logger.Trace("Completed processing for AlbumId: {0}", foreignAlbumId);
            return new Tuple<string, Album, List<ArtistMetadata>>(existingArtist.ForeignArtistId, finalAlbum, new List<ArtistMetadata> { existingArtist.Metadata.Value });
        }

        public async Task<Artist> GetArtistInfoAsync(DeezerMetadataProxySettings settings, string foreignArtistId, int metadataProfileId)
        {
            _logger.Trace($"Fetching artist info for ID: {foreignArtistId}.");
            string artistCacheKey = $"artist:{foreignArtistId}";

            Artist? existingArtist = _artistService.FindById(foreignArtistId);
            if (existingArtist == null)
            {
                DeezerArtist? artistDetails = await GetCachedAsync<DeezerArtist>(settings, artistCacheKey);
                if (artistDetails == null)
                {
                    _logger.Debug($"Artist not found in cache. Fetching from Deezer API for ID: {foreignArtistId}.");
                    artistDetails = await _apiService.GetArtistAsync(int.Parse(RemoveIdentifier(foreignArtistId)));
                    await SetCachedAsync(settings, artistCacheKey, artistDetails!);
                }
                if (artistDetails == null)
                    throw new KeyNotFoundException();
                existingArtist = DeezerMappingHelper.MapArtistFromDeezerArtist(artistDetails);
            }

            existingArtist.Albums = GetArtistAlbumsAsync(settings, existingArtist).GetAwaiter().GetResult();
            existingArtist.MetadataProfileId = metadataProfileId;
            _logger.Trace($"Processed artist: {existingArtist.Name} (ID: {existingArtist.ForeignArtistId}).");
            return existingArtist;
        }

        public static bool IsDeezerIdQuery(string? query) => query?.StartsWith("deezer:") == true || query?.StartsWith("deezerid:") == true;
        private static string SanitizeToUnicode(string input) => string.IsNullOrEmpty(input) ? input : new string(input.Where(c => c <= 0xFFFF).ToArray());
        private static string RemoveIdentifier(string input) => input.EndsWith(_identifier, StringComparison.OrdinalIgnoreCase) ? input.Remove(input.Length - _identifier.Length) : input;
    }
}
