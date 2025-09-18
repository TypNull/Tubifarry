using FuzzySharp;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Metadata;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Discogs
{
    public class DiscogsProxy : IDiscogsProxy
    {
        private const string _identifier = "@discogs";
        private readonly Logger _logger;
        private readonly CacheService _cache;
        private readonly IArtistService _artistService;
        private readonly IAlbumService _albumService;
        private readonly IHttpClient _httpClient;
        private readonly IMetadataProfileService _metadataProfileService;
        private readonly IPluginSettings _pluginSettings;

        public DiscogsProxy(Logger logger, IPluginSettings pluginSettings, IHttpClient httpClient, IArtistService artistService, IAlbumService albumService, IMetadataProfileService metadataProfileService)
        {
            _logger = logger;
            _httpClient = httpClient;
            _artistService = artistService;
            _albumService = albumService;
            _metadataProfileService = metadataProfileService;
            _pluginSettings = pluginSettings;
            _cache = new CacheService();
        }

        private async Task<List<T>> CachedSearchAsync<T>(DiscogsMetadataProxySettings settings, string query, Func<DiscogsSearchItem, T?> mapper, string kind = "all", string? artist = null)
        {
            string key = $"{kind}:{query}:{artist ?? ""}" + _identifier;
            DiscogsApiService apiService = new(_httpClient, settings.UserAgent)
            {
                AuthToken = settings.AuthToken,
                PageSize = settings.PageSize,
                MaxPageLimit = settings.PageNumber
            };

            List<DiscogsSearchItem> results = await _cache.FetchAndCacheAsync(key,
                () => apiService.SearchAsync(new() { Query = query, Artist = artist, Type = kind == "all" ? string.Empty : kind }));

            return results.Select(r => mapper(r)).Where(x => x != null).ToList()!;
        }

        private void UpdateCache(DiscogsMetadataProxySettings settings)
        {
            _cache.CacheDirectory = settings.CacheDirectory;
            _cache.CacheType = (CacheType)settings.RequestCacheType;
        }

        public List<Album> SearchNewAlbum(DiscogsMetadataProxySettings settings, string title, string artist)
        {
            _logger.Debug($"SearchNewAlbum: title '{title}', artist '{artist}'");
            UpdateCache(settings);

            try
            {
                List<Album> albums = CachedSearchAsync(settings, title, r =>
                {
                    DiscogsApiService apiService = new(_httpClient, settings.UserAgent) { AuthToken = settings.AuthToken, PageSize = settings.PageSize };
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

        public List<Artist> SearchNewArtist(DiscogsMetadataProxySettings settings, string title)
        {
            UpdateCache(settings);
            return CachedSearchAsync(settings, title, DiscogsMappingHelper.MapArtistFromSearchItem, "artist", null).GetAwaiter().GetResult();
        }


        public List<object> SearchNewEntity(DiscogsMetadataProxySettings settings, string query)
        {
            _logger.Debug($"SearchNewEntity invoked: query '{query}'");
            UpdateCache(settings);
            query = SanitizeToUnicode(query);

            DiscogsApiService apiService = new(_httpClient, settings.UserAgent)
            {
                AuthToken = settings.AuthToken,
                PageSize = settings.PageSize,
                MaxPageLimit = settings.PageNumber
            };

            if (IsDiscogsidQuery(query))
            {
                query = query.Replace("discogs:", "").Replace("discogsid:", "");
                string? typeSpecifier = null;
                if (query.Length > 2 && query[1] == ':')
                {
                    typeSpecifier = query[0].ToString().ToLowerInvariant();
                    query = query.Substring(2);
                }

                if (int.TryParse(query, out int discogsId))
                {
                    List<object?> results = new();
                    if (typeSpecifier == "a")
                    {
                        results.Add(_cache.FetchAndCacheAsync<DiscogsArtist>($"artist:{discogsId}", () => apiService.GetArtistAsync(discogsId)!)
                            .ContinueWith(t => t.GetAwaiter().GetResult() == null ? null : (object)DiscogsMappingHelper.MapArtistFromDiscogsArtist(t.GetAwaiter().GetResult())).GetAwaiter().GetResult());
                    }
                    else if (typeSpecifier == "r")
                    {
                        results.Add(_cache.FetchAndCacheAsync<DiscogsRelease>($"release:{discogsId}", () => apiService.GetReleaseAsync(discogsId)!)
                            .ContinueWith(t => t.GetAwaiter().GetResult() == null ? null : (object)DiscogsMappingHelper.MapAlbumFromRelease(t.GetAwaiter().GetResult()))
                            .GetAwaiter().GetResult());
                    }
                    else if (typeSpecifier == "m")
                    {
                        results.Add(_cache.FetchAndCacheAsync<DiscogsMasterRelease>($"master:{discogsId}", () => apiService.GetMasterReleaseAsync(discogsId)!)
                            .ContinueWith(t => t.GetAwaiter().GetResult() == null ? null : (object)DiscogsMappingHelper.MapAlbumFromMasterRelease(t.GetAwaiter().GetResult()))
                            .GetAwaiter().GetResult());
                    }
                    return results.Where(x => x != null).ToList()!;
                }
            }


            return CachedSearchAsync(settings, query, item =>
            {
                return item.Type?.ToLowerInvariant() switch
                {
                    "artist" => _cache.FetchAndCacheAsync<DiscogsArtist>($"artist:{item.Id}", () => apiService.GetArtistAsync(item.Id)!)
                        .ContinueWith(t => (object)DiscogsMappingHelper.MapArtistFromDiscogsArtist(t.GetAwaiter().GetResult())).GetAwaiter().GetResult(),
                    "release" => _cache.FetchAndCacheAsync<DiscogsRelease>($"release:{item.Id}", () => apiService.GetReleaseAsync(item.Id)!)
                        .ContinueWith(t =>
                        {
                            Album album = DiscogsMappingHelper.MapAlbumFromRelease(t.GetAwaiter().GetResult());
                            album.Artist = DiscogsMappingHelper.MapArtistFromSearchItem(item);
                            return album;
                        }).GetAwaiter().GetResult(),
                    "master" => _cache.FetchAndCacheAsync<DiscogsMasterRelease>($"master:{item.Id}", () => apiService.GetMasterReleaseAsync(item.Id)!)
                        .ContinueWith(t =>
                        {
                            Album album = DiscogsMappingHelper.MapAlbumFromMasterRelease(t.GetAwaiter().GetResult());
                            album.Artist = DiscogsMappingHelper.MapArtistFromSearchItem(item);
                            return album;
                        }).GetAwaiter().GetResult(),
                    _ => null
                };
            }).GetAwaiter().GetResult();
        }

        public async Task<Tuple<string, Album, List<ArtistMetadata>>> GetAlbumInfoAsync(DiscogsMetadataProxySettings settings, string foreignAlbumId)
        {
            _logger.Debug($"Starting GetAlbumInfoAsync for AlbumId: {foreignAlbumId}");
            UpdateCache(settings);

            Album? existingAlbum = _albumService.FindById(foreignAlbumId);

            bool useMaster = foreignAlbumId.StartsWith("m");
            _logger.Trace($"Using {(useMaster ? "master" : "release")} details for AlbumId: {foreignAlbumId}");

            DiscogsApiService apiService = new(_httpClient, settings.UserAgent) { AuthToken = settings.AuthToken };

            (Album mappedAlbum, object releaseForTracks) = useMaster
                ? await GetMasterReleaseDetailsAsync(foreignAlbumId, apiService)
                : await GetReleaseDetailsAsync(foreignAlbumId, apiService);

            DiscogsArtist? discogsArtist = await GetPrimaryArtistAsync(foreignAlbumId, useMaster, existingAlbum);

            Artist existingArtist = (existingAlbum?.Artist?.Value ?? (discogsArtist != null ? DiscogsMappingHelper.MapArtistFromDiscogsArtist(discogsArtist) : null))
                ?? throw new ModelNotFoundException(typeof(Artist), 0);
            _logger.Trace($"Processed artist information for ArtistId: {existingArtist.ForeignArtistId}");
            existingArtist.Albums ??= new LazyLoaded<List<Album>>(new List<Album>());

            mappedAlbum.Artist = existingArtist;
            mappedAlbum.ArtistMetadata = existingArtist.Metadata;
            mappedAlbum.ArtistMetadataId = existingArtist.ArtistMetadataId;

            Album finalAlbum = DiscogsMappingHelper.MergeAlbums(existingAlbum!, mappedAlbum);
            AlbumRelease albumRelease = finalAlbum.AlbumReleases.Value[0];
            List<Track> tracks = DiscogsMappingHelper.MapTracks(releaseForTracks, finalAlbum, albumRelease);
            _logger.Trace($"Mapped {tracks.Count} tracks for AlbumId: {foreignAlbumId}");

            albumRelease.TrackCount = tracks.Count;
            albumRelease.Duration = tracks.Sum(x => x.Duration);
            albumRelease.Monitored = tracks.Count > 0;
            albumRelease.Tracks = tracks;

            _logger.Trace($"Completed processing for AlbumId: {foreignAlbumId}. Total Tracks: {tracks.Count}");
            return new Tuple<string, Album, List<ArtistMetadata>>(existingArtist.ForeignArtistId, finalAlbum, new List<ArtistMetadata> { existingArtist.Metadata.Value });
        }

        private async Task<(Album, object)> GetMasterReleaseDetailsAsync(string id, DiscogsApiService apiService)
        {
            string masterKey = $"master:{id}" + _identifier;
            DiscogsMasterRelease? master = await _cache.FetchAndCacheAsync<DiscogsMasterRelease>(masterKey,
                () => apiService.GetMasterReleaseAsync(int.Parse(RemoveIdentifier(id[1..])))!);
            return (DiscogsMappingHelper.MapAlbumFromMasterRelease(master!), master)!;
        }

        private async Task<(Album, object)> GetReleaseDetailsAsync(string id, DiscogsApiService apiService)
        {
            string releaseKey = $"release:{id}" + _identifier;
            DiscogsRelease? release = await _cache.FetchAndCacheAsync<DiscogsRelease>(releaseKey,
                () => apiService.GetReleaseAsync(int.Parse(RemoveIdentifier(id[1..])))!);
            return (DiscogsMappingHelper.MapAlbumFromRelease(release!), release)!;
        }

        private async Task<DiscogsArtist?> GetPrimaryArtistAsync(string id, bool useMaster, Album? existingAlbum)
        {
            string key = (useMaster ? $"master:{id}" : $"release:{id}") + _identifier;
            object? release = useMaster
                ? await _cache.FetchAndCacheAsync<DiscogsMasterRelease>(key, () => Task.FromResult<DiscogsMasterRelease>(null!))
                : await _cache.FetchAndCacheAsync<DiscogsRelease>(key, () => Task.FromResult<DiscogsRelease>(null!));

            IEnumerable<DiscogsArtist> artists = (IEnumerable<DiscogsArtist>)(release as dynamic)?.Artists! ?? Enumerable.Empty<DiscogsArtist>();
            return artists.FirstOrDefault(x => existingAlbum == null || Fuzz.Ratio(x.Name, existingAlbum.Artist?.Value.Name) > 80);
        }

        public async Task<Artist> GetArtistInfoAsync(DiscogsMetadataProxySettings settings, string foreignArtistId, int metadataProfileId)
        {
            _logger.Debug($"Fetching artist info for ID: {foreignArtistId}.");
            UpdateCache(settings);

            string artistCacheKey = $"artist:{foreignArtistId}" + _identifier;
            DiscogsArtist? artist = await _cache.FetchAndCacheAsync<DiscogsArtist>(artistCacheKey, () =>
            {
                DiscogsApiService apiService = new(_httpClient, settings.UserAgent) { AuthToken = settings.AuthToken };
                return apiService.GetArtistAsync(int.Parse(RemoveIdentifier(foreignArtistId)[1..]))!;
            });

            Artist? existingArtist = _artistService.FindById(foreignArtistId);
            existingArtist ??= DiscogsMappingHelper.MapArtistFromDiscogsArtist(artist!);
            existingArtist.Albums = AlbumMapper.FilterAlbums(await FetchAlbumsForArtistAsync(settings, existingArtist, artist!.Id), metadataProfileId, _metadataProfileService);
            existingArtist.MetadataProfileId = metadataProfileId;

            _logger.Trace($"Processed artist: {artist.Name} (ID: {existingArtist.ForeignArtistId}).");
            return existingArtist;
        }

        private async Task<List<Album>> FetchAlbumsForArtistAsync(DiscogsMetadataProxySettings settings, Artist artist, int foreignArtistId)
        {
            _logger.Debug($"Fetching albums for artist ID: {foreignArtistId}.");

            string key = $"artist-albums:{foreignArtistId}" + _identifier;
            List<DiscogsArtistRelease> artistReleases = await _cache.FetchAndCacheAsync(key, () =>
            {
                DiscogsApiService apiService = new(_httpClient, settings.UserAgent) { AuthToken = settings.AuthToken };
                return apiService.GetArtistReleasesAsync(foreignArtistId, null, 70);
            });

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
            return albums;
        }

        public bool IsDiscogsidQuery(string? query) => query?.StartsWith("discogs:", StringComparison.OrdinalIgnoreCase) == true || query?.StartsWith("discogsid:", StringComparison.OrdinalIgnoreCase) == true;

        private static string SanitizeToUnicode(string input) => string.IsNullOrEmpty(input) ? input : new string(input.Where(c => c <= 0xFFFF).ToArray());

        private static string RemoveIdentifier(string input) => input.EndsWith(_identifier, StringComparison.OrdinalIgnoreCase) ? input.Remove(input.Length - _identifier.Length) : input;
    }
}
