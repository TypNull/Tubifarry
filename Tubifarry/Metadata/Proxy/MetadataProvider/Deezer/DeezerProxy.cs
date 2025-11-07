using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Metadata;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Deezer
{
    public class DeezerProxy : IDeezerProxy
    {
        private const string _identifier = "@deezer";
        private readonly Logger _logger;
        private readonly CacheService _cache;
        private readonly IArtistService _artistService;
        private readonly IAlbumService _albumService;
        private readonly IHttpClient _httpClient;
        private readonly IMetadataProfileService _metadataProfileService;

        public DeezerProxy(Logger logger, IHttpClient httpClient, IArtistService artistService, IAlbumService albumService, IMetadataProfileService metadataProfileService)
        {
            _logger = logger;
            _httpClient = httpClient;
            _artistService = artistService;
            _albumService = albumService;
            _metadataProfileService = metadataProfileService;
            _cache = new CacheService();
        }

        private void UpdateCache(DeezerMetadataProxySettings settings)
        {
            _cache.CacheDirectory = settings.CacheDirectory;
            _cache.CacheType = (CacheType)settings.RequestCacheType;
        }

        private async Task<List<TReturn>> CachedSearchAsync<TReturn, TSearch>(DeezerMetadataProxySettings settings, string query, Func<TSearch, TReturn?> mapper, string? artist = null)
        {
            UpdateCache(settings);
            string key = $"{typeof(TSearch).Name}:{query}:{artist ?? ""}" + _identifier;
            List<TSearch> results = await _cache.FetchAndCacheAsync<List<TSearch>>(key, () =>
            {
                DeezerApiService apiService = new(_httpClient, settings.UserAgent)
                {
                    PageSize = settings.PageSize,
                    MaxPageLimit = settings.PageNumber
                };
                return apiService.SearchAsync<TSearch>(new DeezerSearchParameter { Query = query, Artist = artist })!;
            });
            return results.Select(r => mapper(r)).Where(x => x != null).ToList()!;
        }

        public async Task<List<Album>> GetArtistAlbumsAsync(DeezerMetadataProxySettings settings, Artist artist)
        {
            UpdateCache(settings);
            DeezerApiService apiService = new(_httpClient, settings.UserAgent);

            string artistAlbumsCacheKey = $"artist-albums:{artist.ForeignArtistId}" + _identifier;
            List<DeezerAlbum> albumResults = await _cache.FetchAndCacheAsync<List<DeezerAlbum>>(
                artistAlbumsCacheKey,
                () => apiService.GetArtistDataAsync<DeezerAlbum>(int.Parse(RemoveIdentifier(artist.ForeignArtistId)))!);

            List<Album> albums = [];
            foreach (DeezerAlbum albumD in albumResults)
            {
                if (albumD.Artist?.Id != null && RemoveIdentifier(artist.ForeignArtistId) != albumD.Artist.Id.ToString())
                    continue;
                Album album = DeezerMappingHelper.MapAlbumFromDeezerAlbum(albumD, artist);
                album = DeezerMappingHelper.MergeAlbums(_albumService.FindById(artist.ForeignArtistId), album);
                albums.Add(album);
            }
            return albums;
        }

        public List<Album> SearchNewAlbum(DeezerMetadataProxySettings settings, string title, string artist)
        {
            _logger.Debug($"SearchNewAlbum: title '{title}', artist '{artist}'");
            UpdateCache(settings);
            try
            {
                return CachedSearchAsync<Album, DeezerAlbum>(settings, title, item =>
                {
                    DeezerApiService apiService = new(_httpClient, settings.UserAgent)
                    {
                        PageSize = settings.PageSize,
                        MaxPageLimit = settings.PageNumber
                    };
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

        public List<Artist> SearchNewArtist(DeezerMetadataProxySettings settings, string title)
        {
            UpdateCache(settings);
            return CachedSearchAsync<Artist, DeezerArtist>(settings, "", artistD =>
            {
                Artist artist = DeezerMappingHelper.MapArtistFromDeezerArtist(artistD);
                artist.Albums = GetArtistAlbumsAsync(settings, artist).GetAwaiter().GetResult();
                return artist;
            }, title).GetAwaiter().GetResult();
        }

        public List<object> SearchNewEntity(DeezerMetadataProxySettings settings, string query)
        {
            _logger.Debug($"SearchNewEntity invoked: query '{query}'");
            UpdateCache(settings);
            query = SanitizeToUnicode(query);

            DeezerApiService apiService = new(_httpClient, settings.UserAgent)
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
                            return [artist];
                    }
                    catch { }

                    DeezerAlbum? albumResult = apiService.GetAlbumAsync(deezerId).GetAwaiter().GetResult();
                    if (albumResult != null)
                        return [DeezerMappingHelper.MapAlbumFromDeezerAlbum(albumResult)];
                }
            }

            List<DeezerAlbum> searchItems = CachedSearchAsync<DeezerAlbum, DeezerAlbum>(settings, query, x => x).GetAwaiter().GetResult();
            List<Artist> artists = [];
            List<object> results = [];

            foreach (DeezerAlbum? item in searchItems.DistinctBy(x => x.Id))
            {
                Artist? mappedArtist = artists.Find(x => x.ForeignArtistId.StartsWith(item.Artist.Id.ToString()));
                if (mappedArtist == null)
                {
                    mappedArtist = DeezerMappingHelper.MapArtistFromDeezerArtist(item.Artist);
                    artists.Add(mappedArtist);
                    results.Add(mappedArtist);
                }

                DeezerAlbum? albumDetails = item;
                Album mappedAlbum = DeezerMappingHelper.MapAlbumFromDeezerAlbum(albumDetails ?? item);
                mappedAlbum.Artist = mappedArtist;
                mappedAlbum.ArtistMetadata = mappedArtist.Metadata;
                results.Add(mappedAlbum);
            }

            return results;
        }

        public async Task<Tuple<string, Album, List<ArtistMetadata>>> GetAlbumInfoAsync(DeezerMetadataProxySettings settings, string foreignAlbumId)
        {
            _logger.Debug("Fetching album details for AlbumId: {0}", foreignAlbumId);
            UpdateCache(settings);
            DeezerApiService apiService = new(_httpClient, settings.UserAgent);

            string albumCacheKey = $"album:{foreignAlbumId}" + _identifier;
            Album? existingAlbum = _albumService.FindById(foreignAlbumId);
            DeezerAlbum albumDetails = await _cache.FetchAndCacheAsync<DeezerAlbum>(albumCacheKey,
                () => apiService.GetAlbumAsync(int.Parse(RemoveIdentifier(foreignAlbumId)))!)
                ?? throw new Exception("Album not found from Deezer API.");

            albumDetails = await EnsureAllTracksAsync(albumDetails, apiService);

            Artist? existingArtist = existingAlbum?.Artist?.Value ?? _artistService.FindById(albumDetails.Artist.Id + _identifier);
            if (existingArtist == null)
            {
                _logger.Debug("Artist {0} not found in database, mapping from Deezer data", albumDetails.Artist.Name);
                existingArtist = DeezerMappingHelper.MapArtistFromDeezerArtist(albumDetails.Artist);
            }

            Album mappedAlbum = DeezerMappingHelper.MapAlbumFromDeezerAlbum(albumDetails, existingArtist);
            Album finalAlbum = existingAlbum != null ? DeezerMappingHelper.MergeAlbums(existingAlbum, mappedAlbum) : mappedAlbum;

            _logger.Trace("Completed processing for AlbumId: {0}", foreignAlbumId);
            return new Tuple<string, Album, List<ArtistMetadata>>(existingArtist.ForeignArtistId, finalAlbum, [existingArtist.Metadata.Value]);
        }

        private async Task<DeezerAlbum> EnsureAllTracksAsync(DeezerAlbum album, DeezerApiService apiService)
        {
            if (album.NumberOfTracks > 25 && (album.Tracks?.Data?.Count ?? 0) < album.NumberOfTracks)
            {
                string tracksCacheKey = $"album-tracks:{album.Id}" + _identifier;
                List<DeezerTrack>? allTracks = await _cache.FetchAndCacheAsync<List<DeezerTrack>>(tracksCacheKey,
                    () => apiService.GetAlbumDataAsync<DeezerTrack>(album.Id)!);
                if (allTracks != null && allTracks.Count > (album.Tracks?.Data?.Count ?? 0))
                    return album with { Tracks = new DeezerTrackWrapper(allTracks) };
            }
            return album;
        }

        public async Task<Artist> GetArtistInfoAsync(DeezerMetadataProxySettings settings, string foreignArtistId, int metadataProfileId)
        {
            _logger.Debug($"Fetching artist info for ID: {foreignArtistId}.");
            UpdateCache(settings);

            string artistCacheKey = $"artist:{foreignArtistId}" + _identifier;
            Artist? existingArtist = _artistService.FindById(foreignArtistId);

            if (existingArtist == null)
            {
                DeezerApiService apiService = new(_httpClient, settings.UserAgent);
                DeezerArtist? artistDetails = await _cache.FetchAndCacheAsync<DeezerArtist>(artistCacheKey,
                    () => apiService.GetArtistAsync(int.Parse(RemoveIdentifier(foreignArtistId)))!) ?? throw new KeyNotFoundException();
                existingArtist = DeezerMappingHelper.MapArtistFromDeezerArtist(artistDetails);
            }

            existingArtist.Albums = AlbumMapper.FilterAlbums(await GetArtistAlbumsAsync(settings, existingArtist), metadataProfileId, _metadataProfileService);
            existingArtist.MetadataProfileId = metadataProfileId;

            _logger.Trace($"Processed artist: {existingArtist.Name} (ID: {existingArtist.ForeignArtistId}).");
            return existingArtist;
        }

        public static bool IsDeezerIdQuery(string? query) => query?.StartsWith("deezer:", StringComparison.OrdinalIgnoreCase) == true || query?.StartsWith("deezerid:", StringComparison.OrdinalIgnoreCase) == true;
        private static string SanitizeToUnicode(string input) => string.IsNullOrEmpty(input) ? input : new string(input.Where(c => c <= 0xFFFF).ToArray());
        private static string RemoveIdentifier(string input) => input.EndsWith(_identifier, StringComparison.OrdinalIgnoreCase) ? input.Remove(input.Length - _identifier.Length) : input;
    }
}
