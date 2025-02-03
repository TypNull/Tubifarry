using FluentValidation.Results;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource.SkyHook;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using ParkSquare.Discogs;
using ParkSquare.Discogs.Dto;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;

namespace Tubifarry.Metadata.Proxy.DiscogsProxy
{
    public class DiscogsProxy : IDiscogsProxy
    {
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, CacheEntry<object>> _cache = new();
        private readonly TimeSpan CacheDuration = TimeSpan.FromHours(10);
        private readonly System.Net.Http.HttpClient _artistHttpClient = new();

        #region Caching Helpers

        public DiscogsProxy(Logger logger) => _logger = logger;

        private T? GetCached<T>(string key)
        {
            if (_cache.TryGetValue(key, out CacheEntry<object>? entry) &&
                (DateTime.Now - entry.Timestamp) < CacheDuration &&
                entry.Value is T cached)
            {
                _logger.Trace($"Cache hit: {key}");
                return cached;
            }
            return default;
        }

        private void SetCached(string key, object value) => _cache[key] = new CacheEntry<object> { Timestamp = DateTime.Now, Value = value };

        private class CacheEntry<T>
        {
            public DateTime Timestamp { get; set; }
            public T Value { get; set; } = default!;
        }

        #endregion

        #region API Execution Helpers

        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> func, int maxRetries = 3)
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    _logger.Trace($"Attempt {attempt + 1}: Calling Discogs API.");
                    T result = await func();
                    _logger.Debug("API call succeeded.");
                    return result;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    attempt++;
                    _logger.Warn($"HTTP 429 received. Attempt {attempt} of {maxRetries}.");
                    if (attempt > maxRetries)
                    {
                        _logger.Error("Max retry attempts reached. Aborting.");
                        throw;
                    }
                    int delaySeconds = 60 * attempt;
                    _logger.Debug($"Delaying for {delaySeconds} seconds before retrying.");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                }
            }
        }

        private async Task<SearchResults> SearchAsync(DiscogsMetadataProxySettings settings, SearchCriteria criteria, PageOptions? options = null)
        {
            IDiscogsClient client = CreateDiscogsClient(settings);
            if (options != null)
                return await ExecuteWithRetryAsync(() => client.SearchAsync(criteria, options));
            return await ExecuteWithRetryAsync(() => client.SearchAsync(criteria));
        }

        private async Task<List<T>> CachedSearchAsync<T>(DiscogsMetadataProxySettings settings, string kind, string title, string? artist, PageOptions? options, Func<SearchResult, T?> mapper)
        {
            string key = $"{kind}:{title}:{artist ?? ""}:{(options != null ? $"P{options.PageNumber}_S{options.PageSize}" : "nopage")}";
            List<T>? cached = GetCached<List<T>>(key);
            if (cached != null)
                return cached;

            SearchCriteria criteria = new()
            {
                Query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}",
                Type = kind.StartsWith("artist") ? "artist" : "release"
            };

            SearchResults results = await SearchAsync(settings, criteria, options);
            List<T> mapped = results.Results.Select(r => mapper(r)).Where(x => x != null).ToList()!;
            SetCached(key, mapped);
            return mapped;
        }

        #endregion

        #region Client Creation

        private static IDiscogsClient CreateDiscogsClient(DiscogsMetadataProxySettings settings)
        {
            System.Net.Http.HttpClient httpClient = new(new HttpClientHandler());
            ApiQueryBuilder queryBuilder = new(settings);
            return new DiscogsClient(httpClient, queryBuilder);
        }

        #endregion

        #region IDiscogsProxy Methods

        public ValidationResult Test(DiscogsMetadataProxySettings settings)
        {
            _logger.Info("Test method invoked.");
            if (settings == null)
                return new ValidationResult(new ValidationFailure[] { new("Settings", "Can not be null") });
            return new ValidationResult();
        }

        public List<Album> SearchNewAlbum(DiscogsMetadataProxySettings settings, string title, string artist)
        {
            _logger.Debug($"SearchNewAlbum: title '{title}', artist '{artist}'");
            try
            {
                List<Album> albums = CachedSearchAsync<Album>(settings, "album", title, artist, null, r => MapExtendedAlbumAsync(settings, r).GetAwaiter().GetResult()).GetAwaiter().GetResult();

                return albums.GroupBy(a => new { a.CleanTitle, a.AlbumType }).Select(g => g.First()).ToList();
            }
            catch (Exception ex)
            {
                _logger.Error($"SearchNewAlbum error: {ex}");
                throw;
            }
        }

        public List<NzbDrone.Core.Music.Artist> SearchNewArtist(DiscogsMetadataProxySettings settings, string title)
        {
            _logger.Debug($"SearchNewArtist: title '{title}'");
            try
            {
                return CachedSearchAsync<NzbDrone.Core.Music.Artist>(settings, "artist", title, null, null, r => new NzbDrone.Core.Music.Artist
                {
                    Metadata = new LazyLoaded<ArtistMetadata>(new ArtistMetadata
                    {
                        Name = r.Title,
                        Ratings = new Ratings(),
                        ForeignArtistId = r.ReleaseId.ToString(),
                        Images = new List<MediaCover> { new() { Url = r.CoverImage ?? r.Thumb, CoverType = MapCoverType("primary", true) } }
                    }) 
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Error($"SearchNewArtist error: {ex}");
                throw;
            }
        }

        public List<object> SearchNewEntity(DiscogsMetadataProxySettings settings, string title)
        {
            _logger.Debug($"SearchNewEntity invoked: query '{title}'");
            string lowerTitle = title.ToLowerInvariant();
            if (IsDiscogsidQuery(lowerTitle))
            {
                List<NzbDrone.Core.Music.Artist> artistResults = SearchNewArtist(settings, lowerTitle);
                if (artistResults.Any())
                {
                    _logger.Trace("DiscogsID query found artist result.");
                    return new List<object> { artistResults[0] };
                }
                List<Album> albumResults = SearchNewAlbum(settings, lowerTitle, null!);
                if (albumResults.Any())
                {
                    Album? album = albumResults.FirstOrDefault(x => x.AlbumReleases.Value.Any());
                    _logger.Trace("DiscogsID query found album result.");
                    return album != null ? new List<object> { album } : new List<object>();
                }
            }

            try
            {
                PageOptions pageOptions = new() { PageNumber = settings.PageNumber, PageSize = settings.PageSize };
                List<Album> albumPaged = CachedSearchAsync(settings, "releaseEntity", title, null, pageOptions, r => MapExtendedAlbumAsync(settings, r).GetAwaiter().GetResult()).GetAwaiter().GetResult();
                List<NzbDrone.Core.Music.Artist> artistPaged = CachedSearchAsync<NzbDrone.Core.Music.Artist>(settings, "artistEntity", title, null, pageOptions, r => new NzbDrone.Core.Music.Artist
                {
                    Metadata = new LazyLoaded<ArtistMetadata>(new ArtistMetadata
                    {
                        Name = r.Title,
                        Ratings = new Ratings(),
                        ForeignArtistId = r.ReleaseId.ToString(),
                        Images = new List<MediaCover> { new() { Url = r.CoverImage ?? r.Thumb, CoverType = MapCoverType("primary", true) } }
                    })
                }).GetAwaiter().GetResult();

                _logger.Trace("Paged search completed for entity.");
                return albumPaged.Cast<object>().Concat(artistPaged).ToList();
            }
            catch (HttpException)
            {
                throw new SkyHookException("Search for '{0}' failed. Unable to communicate with Discogs API.", title);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, ex.Message);
                throw new SkyHookException("Search for '{0}' failed. Invalid response received from Discogs API.", title);
            }
        }

        public Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(DiscogsMetadataProxySettings settings, string foreignAlbumId)
        {
            _logger.Debug($"GetAlbumInfo: album id {foreignAlbumId}");
            try
            {
                return GetAlbumInfoAsync(settings, foreignAlbumId).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Error($"GetAlbumInfo error (id {foreignAlbumId}): {ex}");
                throw;
            }
        }

        private async Task<Tuple<string, Album, List<ArtistMetadata>>> GetAlbumInfoAsync(DiscogsMetadataProxySettings settings, string foreignAlbumId)
        {
            string rawId = foreignAlbumId;
            _logger.Debug($"GetAlbumInfoAsync: using album id '{rawId}'.");

            string releaseKey = $"release:{rawId}";
            Release? release = GetCached<Release>(releaseKey);
            if (release == null)
            {
                release = await ExecuteWithRetryAsync(() => CreateDiscogsClient(settings).GetReleaseAsync(int.Parse(rawId)));
                SetCached(releaseKey, release);
            }
            _logger.Trace($"Retrieved release details for album id {rawId}.");
            Album album = MapExtendedAlbumFromRelease(settings, release) ?? throw new Exception("Album mapping failed (missing ArtistMetadata).");

            ParkSquare.Discogs.Dto.Artist discogsArtist = release.Artists[0];
            NzbDrone.Core.Music.Artist? artist = GetCached<NzbDrone.Core.Music.Artist>($"artist:{discogsArtist.Id}");
            ArtistMetadata artistMetadata = artist == null ? new ArtistMetadata()
            {
                Name = discogsArtist.Name,
                ForeignArtistId = discogsArtist.Id.ToString(),
                Overview = discogsArtist.Join,
                Images = release.Images?.Select(img => MapImage(img, true)).Where(x => x != null).ToList() ?? new(),
                Links = new List<Links> { new() { Url = discogsArtist.ResourceUrl, Name = "Discogs" } },
                Ratings = new Ratings()
            } : artist.Metadata;

            _logger.Trace($"GetAlbumInfoAsync: mapped album '{album.Title}' and artist '{discogsArtist.Name}'.");
            return new Tuple<string, Album, List<ArtistMetadata>>(discogsArtist.Id.ToString(), album, new List<ArtistMetadata> { artistMetadata });
        }

        public HashSet<string> GetChangedAlbums(DiscogsMetadataProxySettings settings, DateTime startTime)
        {
            _logger.Warn("GetChangedAlbums: Discogs API does not support change tracking; returning empty set.");
            return new HashSet<string>();
        }

        public HashSet<string> GetChangedArtists(DiscogsMetadataProxySettings settings, DateTime startTime)
        {
            _logger.Warn("GetChangedArtists: Discogs API does not support change tracking; returning empty set.");
            return new HashSet<string>();
        }

        public List<Album> SearchNewAlbumByRecordingIds(DiscogsMetadataProxySettings settings, List<string> recordingIds)
        {
            _logger.Warn("SearchNewAlbumByRecordingIds: Discogs API does not support fingerprint search; returning empty list.");
            return new List<Album>();
        }

        public NzbDrone.Core.Music.Artist GetArtistInfo(DiscogsMetadataProxySettings settings, string lidarrId, int metadataProfileId)
        {
            _logger.Debug($"GetArtistInfo: id {lidarrId}");
            try
            {
                return GetArtistInfoAsync(settings, lidarrId, metadataProfileId).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Error($"GetArtistInfo error (id {lidarrId}): {ex}");
                throw;
            }
        }

        private async Task<NzbDrone.Core.Music.Artist> GetArtistInfoAsync(DiscogsMetadataProxySettings settings, string foreignArtistId, int metadataProfileId)
        {
            try
            {
                _logger.Trace($"GetArtistInfoAsync: id {foreignArtistId}");
                string rawArtistId = foreignArtistId;
                string url = $"{settings.BaseUrl?.TrimEnd('/')}/artists/{rawArtistId}";
                _logger.Trace($"Request URL: {url}");
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Tubifarry/" + Assembly.GetExecutingAssembly().ImageRuntimeVersion);
                if (!string.IsNullOrWhiteSpace(settings.AuthToken))
                    request.Headers.Add("Authorization", $"Discogs token={settings.AuthToken}");
                using HttpResponseMessage response = await _artistHttpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                    return null!;

                JObject artistJson = JObject.Parse(await response.Content.ReadAsStringAsync());
                string artistName = artistJson.Value<string>("name") ?? "Unknown Artist";
                string? resourceUrl = artistJson.Value<string>("resource_url");
                string artistId = artistJson.Value<int?>("id")?.ToString() ?? "0";

                List<MediaCover> images = new();
                if (artistJson.TryGetValue("images", out JToken? imagesToken) && imagesToken is JArray imagesArray)
                {
                    foreach (JObject imageToken in imagesArray.OfType<JObject>())
                    {
                        try
                        {
                            string? imageUri = imageToken.Value<string>("resource_url");
                            if (string.IsNullOrWhiteSpace(imageUri)) continue;
                            images.Add(new MediaCover
                            {
                                RemoteUrl = imageUri,
                                Url = imageUri,
                                CoverType = MapCoverType(imageToken.Value<string>("type") ?? string.Empty, true)
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "Error parsing artist image");
                        }
                    }
                }

                List<Links> links = new();
                if (artistJson.TryGetValue("urls", out JToken? urlsToken) && urlsToken is JArray urlsArray)
                {
                    foreach (JToken urlToken in urlsArray)
                    {
                        string? urlValue = urlToken?.ToString();
                        if (Uri.IsWellFormedUriString(urlValue, UriKind.Absolute))
                        {
                            links.Add(new Links
                            {
                                Url = urlValue,
                                Name = GetLinkNameFromUrl(urlValue)
                            });
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(resourceUrl))
                    links.Add(new Links { Url = resourceUrl, Name = "Discogs" });

                List<string> aliases = new();

                if (artistJson.TryGetValue("aliases", out JToken? aliasesToken) && aliasesToken is JArray aliasesArray)
                {
                    foreach (JObject aliasObj in aliasesArray.OfType<JObject>())
                    {
                        string? aliasName = aliasObj.Value<string>("name");
                        if (!string.IsNullOrWhiteSpace(aliasName))
                            aliases.Add(aliasName.Trim());
                    }
                }

                if (artistJson.TryGetValue("namevariations", out JToken? nameVariationsToken) && nameVariationsToken is JArray nameVariationsArray)
                {
                    foreach (JToken variationToken in nameVariationsArray)
                    {
                        string? variation = variationToken?.ToString();
                        if (!string.IsNullOrWhiteSpace(variation))
                            aliases.Add(variation.Trim());
                    }
                }

                ArtistMetadata artistMetadata = new()
                {
                    Name = artistName,
                    ForeignArtistId = artistId,
                    Aliases = aliases.Distinct().ToList(),
                    Overview = artistJson.Value<string>("profile") ?? string.Empty,
                    Genres = new List<string>(),
                    Images = images,
                    Links = links,
                    Ratings = new Ratings()
                };

                NzbDrone.Core.Music.Artist artist = new()
                {
                    CleanName = artistName.CleanArtistName(),
                    SortName = Parser.NormalizeTitle(artistName) ?? artistName,
                    LastInfoSync = DateTime.Now,
                    Metadata = new LazyLoaded<ArtistMetadata>(artistMetadata),
                    Albums = new LazyAlbumLoader(artistName, this, settings)
                };

                if (!artist.Metadata.Value.Images.Any())
                {
                    try
                    {
                        List<Album> albums = await FetchAlbumsForArtistAsync(settings, artistName);
                        MediaCover? fallback = albums?.SelectMany(a => a.Images ?? Enumerable.Empty<MediaCover>()).FirstOrDefault();

                        if (fallback != null)
                            artist.Metadata.Value.Images.Add(fallback);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Error fetching fallback album images");
                    }
                }
                _logger.Debug($"Successfully processed artist: {artistName}");
                string artistCacheKey = $"artist:{foreignArtistId}";
                SetCached(artistCacheKey, artist);
                return artist;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to process artist information");
                throw new ApplicationException("Error retrieving artist info", ex);
            }
        }

        private static string GetLinkNameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "Website";

            try
            {
                Uri uri = new(url);
                string[] hostParts = uri.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);

                if (hostParts.Contains("bandcamp")) return "Bandcamp";
                if (hostParts.Contains("facebook")) return "Facebook";
                if (hostParts.Contains("youtube")) return "YouTube";
                if (hostParts.Contains("soundcloud")) return "SoundCloud";
                if (hostParts.Contains("discogs")) return "Discogs";

                string mainDomain = hostParts.Length > 1 ? hostParts[^2] : hostParts[0];
                return $"{mainDomain.ToUpper()}";
            }
            catch
            {
                return "Website";
            }
        }

        internal async Task<List<Album>> FetchAlbumsForArtistAsync(DiscogsMetadataProxySettings settings, string artistName)
        {
            string key = $"albums:{artistName}";
            List<Album>? cached = GetCached<List<Album>>(key);
            if (cached != null)
                return cached;

            _logger.Trace($"FetchAlbumsForArtistAsync: artist '{artistName}'");
            SearchResults searchResults = await SearchAsync(settings, new SearchCriteria
            {
                Query = artistName,
                Type = "release",
                Artist = artistName
            });
            List<Album> results = new();
            foreach (SearchResult? r in searchResults.Results)
            {
                try
                {
                    Album album = await MapExtendedAlbumAsync(settings, r);
                    if (album != null)
                        results.Add(album);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Lazy loading album failed: {ex}");
                }
            }
            SetCached(key, results);
            return results.GroupBy(a => new { a.CleanTitle, a.AlbumType }).Select(g => g.First()).ToList();
        }

        private static bool IsDiscogsidQuery(string query) => query.StartsWith("discogs:") || query.StartsWith("discogsid:");
        #endregion

        #region Extended Mapping Methods

        private async Task<Album> MapExtendedAlbumAsync(DiscogsMetadataProxySettings settings, SearchResult searchResult)
        {
            string key = $"release:{searchResult.ReleaseId}";
            Release? cachedRelease = GetCached<Release>(key);
            if (cachedRelease == null)
            {
                cachedRelease = await ExecuteWithRetryAsync(() => CreateDiscogsClient(settings).GetReleaseAsync(searchResult.ReleaseId));
                SetCached(key, cachedRelease);
            }
            return MapExtendedAlbumFromRelease(settings, cachedRelease);
        }

        private Album MapExtendedAlbumFromRelease(DiscogsMetadataProxySettings settings, Release release)
        {
            if (release.Artists?.Any() != true || release.Artists[0].Id == 0)
                return null!;

            DateTime? releaseDate = ParseReleaseDate(release);

            List<MediaCover?> mappedImages = release.Images?
                .Select(img => MapImage(img,false))
                .Where(x => x != null)
                .ToList() ?? new();

            Album album = new()
            {
                ForeignAlbumId = release.ReleaseId.ToString(),
                Title = release.Title,
                ReleaseDate = releaseDate,
                Genres = release.Genres ?? new List<string>(),
                CleanTitle = Parser.NormalizeTitle(release.Title),
                Overview = release.Notes ?? string.Empty,
                Images = mappedImages,
                Links = new List<Links> { new() { Url = release.ResourceUrl, Name = "Discogs" } },
                Ratings = new Ratings()
            };

            if (release.Artists?.Any() == true)
            {
                ParkSquare.Discogs.Dto.Artist primaryArtist = release.Artists[0];
                NzbDrone.Core.Music.Artist? artist = GetCached<NzbDrone.Core.Music.Artist>($"artist:{primaryArtist.Id}");
                ArtistMetadata artistMetadata = artist == null ? new ArtistMetadata()
                {
                    Name = primaryArtist.Name,
                    ForeignArtistId = primaryArtist.Id.ToString(),
                    Overview = primaryArtist.Join,
                    Images = release.Images?.Select(img => MapImage(img, true)).Where(x => x != null).ToList() ?? new(),
                    Links = new List<Links> { new() { Url = primaryArtist.ResourceUrl, Name = "Discogs" } },
                    Ratings = new Ratings()
                } : artist.Metadata;
            }
            else
            {
                string defaultArtistName = "Unknown";
                if (!string.IsNullOrWhiteSpace(release.Title) && release.Title.Contains('-'))
                    defaultArtistName = release.Title.Split('-').First().Trim();

                album.Artist = new NzbDrone.Core.Music.Artist
                {
                    Metadata = new LazyLoaded<ArtistMetadata>(new ArtistMetadata
                    {
                        Name = defaultArtistName,
                        Ratings = new Ratings(),
                        ForeignArtistId = ""
                    })
                };
            }

            AlbumRelease albumRelease = new()
            {
                ForeignReleaseId = release.ReleaseId.ToString(),
                Title = release.Title,
                AlbumId = album.Id,
                Album = album,
                Label = release.Labels?.Select(l => l.Name).ToList() ?? new List<string>(),
                ReleaseDate = ParseReleaseDate(release),
                Country = release.Country == null ? new List<string>() : new List<string> { release.Country },
            };

            List<Track> tracks = release.Tracklist?
                .Select(t => MapTrack(t, release, album, albumRelease)).ToList() ?? new List<Track>();
            _logger.Trace($"Discogs reported {release.Tracklist?.Count ?? 0} track(s); mapped {tracks.Count} track(s).");

            albumRelease.TrackCount = tracks.Count;
            albumRelease.Duration = tracks.Sum(x => x.Duration);
            albumRelease.Monitored = tracks.Count > 0;

            album.AlbumReleases = new LazyLoaded<List<AlbumRelease>>(new List<AlbumRelease> { albumRelease });
            album.AnyReleaseOk = true;
            album.ArtistMetadata = album.Artist.Value.Metadata;

            if (album.ArtistMetadata == null || string.IsNullOrWhiteSpace(album.ArtistMetadata.Value.ForeignArtistId))
                return null!;

            MapAlbumTypes(release, album);

            return album;
        }

        private static Track MapTrack(Tracklist t, Release release, Album album, AlbumRelease albumRelease)
        {
            Track track = new()
            {
                ForeignTrackId = $"{release.ReleaseId}_{t.Position}",
                Title = t.Title,
                Duration = ParseDuration(t.Duration),
                TrackNumber = t.Position,
                Explicit = false,
                Ratings = new Ratings(),
                ForeignRecordingId = $"{release.ReleaseId}_{t.Position}",
                OldForeignTrackIds = new List<string>(),
                Album = album,
                ArtistMetadata = album.Artist.Value.Metadata,
                Artist = album.Artist,
                AlbumId = album.Id,
                AlbumRelease = new(albumRelease),
                OldForeignRecordingIds = new List<string>()
            };
            int absoluteNumber = 0;
            string digits = new(t.Position.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(digits))
                int.TryParse(digits, out absoluteNumber);
            track.AbsoluteTrackNumber = absoluteNumber;
            if (!string.IsNullOrEmpty(t.Position) && char.IsLetter(t.Position[0]))
                track.MediumNumber = char.ToUpper(t.Position[0]) - 'A' + 1;
            else
                track.MediumNumber = 1;
            return track;
        }

        private static DateTime? ParseReleaseDate(Release release)
        {
            if (release.Year > 0)
                return new DateTime(release.Year, 1, 1);
            if (DateTime.TryParse(release.Released, out DateTime parsedDate))
                return parsedDate;
            return null;
        }

        private static int ParseDuration(string duration)
        {
            if (string.IsNullOrWhiteSpace(duration))
                return 0;
            string[] parts = duration.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int m) && int.TryParse(parts[1], out int s))
                return (m * 60) + s;
            return 0;
        }

        private static MediaCover? MapImage(Image img, bool isArtist) => new()
        {
            Url = img.Uri,
            RemoteUrl = img.Uri,
            CoverType = MapCoverType(img.Type, isArtist)
        };

        private static MediaCoverTypes MapCoverType(string type, bool isArtist)
        {
            if (isArtist)
            {
                return type.ToLowerInvariant() switch
                {
                    "primary" or "avatar" => MediaCoverTypes.Poster,
                    "banner" => MediaCoverTypes.Banner,
                    "background" => MediaCoverTypes.Headshot,
                    _ => MediaCoverTypes.Poster
                };
            }

            return type.ToLowerInvariant() switch
            {
                "primary" => MediaCoverTypes.Cover,
                "secondary" => MediaCoverTypes.Fanart,
                _ => MediaCoverTypes.Cover
            };
        }

        private static readonly Dictionary<string, string> PrimaryTypeMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Album"] = "Album",
                ["Broadcast"] = "Broadcast",
                ["EP"] = "EP",
                ["Single"] = "Single"
            };

        private static readonly Dictionary<string, SecondaryAlbumType> SecondaryTypeMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Compilation"] = SecondaryAlbumType.Compilation,
                ["Soundtrack"] = SecondaryAlbumType.Soundtrack,
                ["Spokenword"] = SecondaryAlbumType.Spokenword,
                ["Interview"] = SecondaryAlbumType.Interview,
                ["Live"] = SecondaryAlbumType.Live,
                ["Remix"] = SecondaryAlbumType.Remix,
                ["DJ Mix"] = SecondaryAlbumType.DJMix,
                ["Mixtape"] = SecondaryAlbumType.Mixtape,
                ["Demo"] = SecondaryAlbumType.Demo,
                ["Audio Drama"] = SecondaryAlbumType.Audiodrama
            };

        private void MapAlbumTypes(Release release, Album album)
        {
            album.AlbumType = "Studio";

            foreach (Format format in release.Formats ?? Enumerable.Empty<Format>())
            {
                foreach (string desc in format.Descriptions ?? Enumerable.Empty<string>())
                {
                    if (PrimaryTypeMap.TryGetValue(desc, out string? primaryType))
                    {
                        album.AlbumType = primaryType;
                        break;
                    }
                }
                if (album.AlbumType != "Other") break;
            }

            HashSet<SecondaryAlbumType> secondaryTypes = new();
            foreach (Format format in release.Formats ?? Enumerable.Empty<Format>())
                foreach (string desc in format.Descriptions ?? Enumerable.Empty<string>())
                    if (SecondaryTypeMap.TryGetValue(desc, out SecondaryAlbumType? secondaryType))
                        secondaryTypes.Add(secondaryType);
            album.SecondaryTypes = secondaryTypes.ToList();
        }

        #endregion
    }
}
