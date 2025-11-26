using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource.SkyHook;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Metadata;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Lastfm
{
    public class LastfmProxy : ILastfmProxy
    {
        private const string _identifier = "@lastfm";
        private readonly Logger _logger;
        private readonly CacheService _cache;
        private readonly IArtistService _artistService;
        private readonly IAlbumService _albumService;
        private readonly IHttpClient _httpClient;
        private readonly IMetadataProfileService _metadataProfileService;

        public LastfmProxy(Logger logger, IHttpClient httpClient, IArtistService artistService, IAlbumService albumService, IMetadataProfileService metadataProfileService)
        {
            _logger = logger;
            _httpClient = httpClient;
            _artistService = artistService;
            _albumService = albumService;
            _metadataProfileService = metadataProfileService;
            _cache = new CacheService();
        }

        private void UpdateCache(LastfmMetadataProxySettings settings)
        {
            _cache.CacheDirectory = settings.CacheDirectory;
            _cache.CacheType = (CacheType)settings.RequestCacheType;
        }

        public List<Album> SearchNewAlbum(LastfmMetadataProxySettings settings, string title, string artist)
        {
            _logger.Debug($"SearchNewAlbum: title '{title}', artist '{artist}'");
            UpdateCache(settings);

            try
            {
                LastfmApiService apiService = GetApiService(settings);

                List<Album> albums = [];
                List<LastfmAlbum>? lastfmAlbums = _cache.FetchAndCacheAsync($"search:album:{title}:{settings.PageSize}:{settings.PageNumber}",
                    () => apiService.SearchAlbumsAsync(title, settings.PageSize, settings.PageNumber)).GetAwaiter().GetResult();

                if (!string.IsNullOrEmpty(artist))
                {
                    lastfmAlbums = lastfmAlbums?.Where(a =>
                        string.Equals(a.ArtistName, artist, StringComparison.OrdinalIgnoreCase) ||
                        a.ArtistName.Contains(artist, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                foreach (LastfmAlbum lastfmAlbum in lastfmAlbums ?? [])
                {
                    LastfmAlbum? detailedAlbum = _cache.FetchAndCacheAsync($"album:{lastfmAlbum.ArtistName}:{lastfmAlbum.Name}",
                        () => apiService.GetAlbumInfoAsync(lastfmAlbum.ArtistName, lastfmAlbum.Name)).GetAwaiter().GetResult();

                    if (detailedAlbum != null)
                        albums.Add(LastfmMappingHelper.MapAlbumFromLastfmAlbum(detailedAlbum));
                }

                return albums.GroupBy(a => a.CleanTitle).Select(g => g.First()).ToList();
            }
            catch (Exception ex)
            {
                _logger.Error($"SearchNewAlbum error: {ex}");
                throw;
            }
        }

        public List<Artist> SearchNewArtist(LastfmMetadataProxySettings settings, string title)
        {
            _logger.Debug($"SearchNewArtist: title '{title}'");
            UpdateCache(settings);

            try
            {
                LastfmApiService apiService = GetApiService(settings);
                List<Artist> artists = [];
                List<LastfmArtist>? lastfmArtists = _cache.FetchAndCacheAsync($"search:artist:{title}:{settings.PageSize}:{settings.PageNumber}",
                    () => apiService.SearchArtistsAsync(title, settings.PageSize, settings.PageNumber)).GetAwaiter().GetResult();

                foreach (LastfmArtist lastfmArtist in lastfmArtists ?? [])
                {
                    LastfmArtist? detailedArtist = _cache.FetchAndCacheAsync($"artist:{lastfmArtist.Name}", () => apiService.GetArtistInfoAsync(lastfmArtist.Name)).GetAwaiter().GetResult();
                    if (detailedArtist != null)
                        artists.Add(LastfmMappingHelper.MapArtistFromLastfmArtist(detailedArtist));
                }

                return artists;
            }
            catch (Exception ex)
            {
                _logger.Error($"SearchNewArtist error: {ex}");
                throw;
            }
        }

        public List<object> SearchNewEntity(LastfmMetadataProxySettings settings, string query)
        {
            _logger.Info($"SearchNewEntity invoked: query '{query}'");
            UpdateCache(settings);
            query = SanitizeToUnicode(query);
            List<object> results = [];

            try
            {
                LastfmApiService apiService = GetApiService(settings);

                if (IsLastfmIdQuery(query))
                {
                    string id = query.Replace("lastfm:", "").Replace("lastfmid:", "");
                    _logger.Debug($"Processing Last.fm ID query: {id}");

                    if (Guid.TryParse(id, out _))
                    {
                        try
                        {
                            results.Add(GetArtistInfoAsync(settings, id, default).GetAwaiter().GetResult());
                            return results;
                        }
                        catch
                        {
                            try
                            {
                                Tuple<string, Album, List<ArtistMetadata>> albumResult = GetAlbumInfoAsync(settings, id).GetAwaiter().GetResult();
                                results.Add(albumResult.Item2);
                                return results;
                            }
                            catch { }
                        }
                    }
                    else if (id.Contains("::"))
                    {
                        try
                        {
                            Tuple<string, Album, List<ArtistMetadata>> albumResult = GetAlbumInfoAsync(settings, id + _identifier).GetAwaiter().GetResult();
                            results.Add(albumResult.Item2);
                            return results;
                        }
                        catch { }
                    }
                    else
                    {
                        try
                        {
                            results.Add(GetArtistInfoAsync(settings, id + _identifier, default).GetAwaiter().GetResult());
                            return results;
                        }
                        catch { }
                    }
                }

                _logger.Trace($"Performing general search for: {query}");

                List<LastfmArtist>? lastfmArtists = _cache.FetchAndCacheAsync($"search:artist:{query}:{settings.PageSize}:{settings.PageNumber}",
                    () => apiService.SearchArtistsAsync(query, settings.PageSize, settings.PageNumber)).GetAwaiter().GetResult();

                foreach (LastfmArtist lastfmArtist in lastfmArtists ?? [])
                {
                    try
                    {
                        Artist artist = LastfmMappingHelper.MapArtistFromLastfmArtist(lastfmArtist);
                        artist.Albums = new LazyLoaded<List<Album>>([]);
                        results.Add(artist);
                    }
                    catch { }
                }

                List<LastfmAlbum>? lastfmAlbums = _cache.FetchAndCacheAsync($"search:album:{query}:{settings.PageSize}:{settings.PageNumber}",
                    () => apiService.SearchAlbumsAsync(query, settings.PageSize, settings.PageNumber)).GetAwaiter().GetResult();

                foreach (LastfmAlbum lastfmAlbum in lastfmAlbums ?? [])
                {
                    try
                    {
                        Tuple<string, Album, List<ArtistMetadata>> albumResult = GetAlbumInfoAsync(settings, $"{lastfmAlbum.ArtistName}::{lastfmAlbum.Name}{_identifier}").GetAwaiter().GetResult();
                        results.Add(albumResult.Item2);
                    }
                    catch { }
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.Error($"SearchNewEntity error: {ex}");
                throw;
            }
        }

        private async Task EnhanceArtistImagesAsync(LastfmMetadataProxySettings settings, Artist artist)
        {
            if (artist?.Metadata?.Value == null || string.IsNullOrEmpty(artist.Name))
                return;
            try
            {
                LastfmImageScraper scraper = new(_httpClient, settings.UserAgent, _cache);
                List<string> imageUrls = await scraper.GetArtistImagesAsync(artist.Name);
                if (imageUrls == null || imageUrls.Count == 0)
                    return;

                List<MediaCover> newImages = [];
                for (int i = 0; i < Math.Min(imageUrls.Count, 3); i++)
                {
                    MediaCoverTypes type = i == 0 ? MediaCoverTypes.Poster : MediaCoverTypes.Fanart;
                    newImages.Add(new MediaCover
                    {
                        Url = imageUrls[i],
                        CoverType = type
                    });
                }
                artist.Metadata.Value.Images = newImages;

                _logger.Debug($"Enhanced {artist.Name} with {newImages.Count} scraped images");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Failed to enhance artist images for {artist.Name}");
            }
        }

        public async Task<Tuple<string, Album, List<ArtistMetadata>>> GetAlbumInfoAsync(LastfmMetadataProxySettings settings, string foreignAlbumId)
        {
            _logger.Info($"Starting GetAlbumInfoAsync for AlbumId: {foreignAlbumId}");
            UpdateCache(settings);
            _logger.Info(foreignAlbumId);

            try
            {
                string albumIdentifier = RemoveIdentifier(foreignAlbumId);
                LastfmApiService apiService = GetApiService(settings);
                LastfmAlbum? lastfmAlbum = null;
                string? artistName = null;
                string? albumName = null;

                if (Guid.TryParse(albumIdentifier, out _))
                {
                    lastfmAlbum = await _cache.FetchAndCacheAsync(
                        $"album:mbid:{albumIdentifier}",
                        () => apiService.GetAlbumInfoByMbidAsync(albumIdentifier));
                }
                else if (albumIdentifier.Contains("::"))
                {
                    string[] parts = albumIdentifier.Split(["::"], StringSplitOptions.None);
                    artistName = parts[0].Trim();
                    albumName = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    if (!string.IsNullOrEmpty(artistName) && !string.IsNullOrEmpty(albumName))
                    {
                        lastfmAlbum = await _cache.FetchAndCacheAsync(
                            $"album:{artistName}:{albumName}",
                            () => apiService.GetAlbumInfoAsync(artistName, albumName));
                    }
                }
                else
                {
                    throw new SkyHookException("Album format not correct");
                }

                if (lastfmAlbum == null)
                {
                    _logger.Warn($"Album not found on Last.fm: {foreignAlbumId}");
                    throw new SkyHookException("Album not found on Last.fm");
                }

                LastfmArtist? lastfmArtist = await _cache.FetchAndCacheAsync(
                    $"artist:{lastfmAlbum.ArtistName}",
                    () => apiService.GetArtistInfoAsync(lastfmAlbum.ArtistName));
                if (lastfmArtist == null)
                {
                    _logger.Warn($"Artist not found for album: {foreignAlbumId}");
                    throw new SkyHookException("Artist not found");
                }

                Artist artist = LastfmMappingHelper.MapArtistFromLastfmArtist(lastfmArtist);
                Album mappedAlbum = LastfmMappingHelper.MapAlbumFromLastfmAlbum(lastfmAlbum, artist);

                Album? existingAlbum = _albumService.FindById(foreignAlbumId);
                if (existingAlbum != null)
                    mappedAlbum = LastfmMappingHelper.MergeAlbums(existingAlbum, mappedAlbum);

                _logger.Trace($"Completed processing for AlbumId: {foreignAlbumId}");
                return new Tuple<string, Album, List<ArtistMetadata>>(artist.ForeignArtistId,
                    mappedAlbum,
                    [artist.Metadata.Value]);
            }
            catch (Exception ex)
            {
                _logger.Error($"GetAlbumInfoAsync error: {ex}");
                throw;
            }
        }

        public async Task<Artist> GetArtistInfoAsync(LastfmMetadataProxySettings settings, string foreignArtistId, int metadataProfileId)
        {
            _logger.Trace($"Fetching artist info for ID: {foreignArtistId}");
            UpdateCache(settings);

            try
            {
                string artistIdentifier = RemoveIdentifier(foreignArtistId);
                LastfmApiService apiService = GetApiService(settings);
                LastfmArtist? lastfmArtist = null;

                if (Guid.TryParse(artistIdentifier, out _))
                {
                    lastfmArtist = await _cache.FetchAndCacheAsync(
                        $"artist:mbid:{artistIdentifier}",
                        () => apiService.GetArtistInfoByMbidAsync(artistIdentifier));
                }
                else
                {
                    lastfmArtist = await _cache.FetchAndCacheAsync(
                        $"artist:{artistIdentifier}",
                        () => apiService.GetArtistInfoAsync(artistIdentifier));
                }

                if (lastfmArtist == null)
                {
                    _logger.Warn($"Artist not found on Last.fm: {foreignArtistId}");
                    throw new SkyHookException("Artist not found on Last.fm");
                }

                Artist artist = LastfmMappingHelper.MapArtistFromLastfmArtist(lastfmArtist);
                await FetchAlbumsForArtistAsync(settings, artist);
                await EnhanceArtistImagesAsync(settings, artist);
                artist.Albums = AlbumMapper.FilterAlbums(
                    artist.Albums.Value,
                    metadataProfileId,
                    _metadataProfileService);

                artist.MetadataProfileId = metadataProfileId;
                Artist? existingArtist = _artistService.FindById(foreignArtistId);
                if (existingArtist != null)
                {
                    // TODO: Merge any existing data
                    artist.Id = existingArtist.Id;
                    artist.Path = existingArtist.Path;
                    artist.Monitored = existingArtist.Monitored;
                }

                _logger.Trace($"Processed artist: {artist.Name} (ID: {artist.ForeignArtistId})");
                return artist;
            }
            catch (Exception ex)
            {
                _logger.Error($"GetArtistInfoAsync error: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Fetches and maps albums for an artist using the top albums endpoint
        /// </summary>
        private async Task FetchAlbumsForArtistAsync(LastfmMetadataProxySettings settings, Artist artist)
        {
            _logger.Debug($"Fetching top albums for artist: {artist.Name}");

            try
            {
                LastfmApiService apiService = GetApiService(settings);
                string artistName = artist.Name;
                string? mbid = null;

                LastfmArtist? lastfmArtist = await _cache.FetchAndCacheAsync(
                    $"artist:{RemoveIdentifier(artist.ForeignArtistId)}",
                    () => apiService.GetArtistInfoAsync(artistName));

                if (lastfmArtist != null && !string.IsNullOrEmpty(lastfmArtist.MBID))
                    mbid = lastfmArtist.MBID;
                string cacheKey = !string.IsNullOrEmpty(mbid)
                    ? $"artist:topalbums:mbid:{mbid}"
                    : $"artist:topalbums:{artistName}";

                List<LastfmTopAlbum>? topAlbums = await _cache.FetchAndCacheAsync(cacheKey,
                    () => apiService.GetTopAlbumsAsync(artistName, mbid, 100, null, true));

                _logger.Info("Found: " + topAlbums?.Count);
                List<Album> albums = [];

                foreach (LastfmTopAlbum topAlbum in topAlbums ?? [])
                {
                    LastfmAlbum? detailedAlbum = await _cache.GetAsync<LastfmAlbum>($"album:{topAlbum.ArtistName}::{topAlbum.Name}");
                    if (detailedAlbum != null)
                        albums.Add(LastfmMappingHelper.MapAlbumFromLastfmAlbum(detailedAlbum, artist));
                    else albums.Add(LastfmMappingHelper.MapAlbumFromLastfmTopAlbum(topAlbum, artist));
                }

                artist.Albums = new LazyLoaded<List<Album>>(albums.Where(x => x.Title != "(null)").ToList());
                _logger.Info($"Fetched {albums.Count} top albums for artist: {artist.Name}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error fetching top albums for artist {artist.Name}: {ex.Message}");
                artist.Albums = new LazyLoaded<List<Album>>([]);
            }
        }

        /// <summary>
        /// Gets a configured Last.fm API service
        /// </summary>
        private LastfmApiService GetApiService(LastfmMetadataProxySettings settings) => new(_httpClient, settings.UserAgent)
        {
            ApiKey = settings.ApiKey,
            PageSize = settings.PageSize,
            MaxPageLimit = settings.PageNumber
        };

        public bool IsLastfmIdQuery(string? query) => query?.StartsWith("lastfm:", StringComparison.OrdinalIgnoreCase) == true ||
                   query?.StartsWith("lastfmid:", StringComparison.OrdinalIgnoreCase) == true;

        private static string SanitizeToUnicode(string input) => string.IsNullOrEmpty(input) ? input : new string(input.Where(c => c <= 0xFFFF).ToArray());

        private static string RemoveIdentifier(string input) => input.EndsWith(_identifier, StringComparison.OrdinalIgnoreCase)
                ? input.Remove(input.Length - _identifier.Length) : input;
    }
}