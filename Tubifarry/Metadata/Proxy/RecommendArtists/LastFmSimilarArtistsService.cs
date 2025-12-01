using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists.LastFm;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using System.Text.Json;
using Tubifarry.Core.Utilities;
using Tubifarry.ImportLists.LastFmRecommendation;
using Tubifarry.Metadata.Proxy.MetadataProvider.Lastfm;

namespace Tubifarry.Metadata.Proxy.RecommendArtists
{
    public interface ILastFmSimilarArtistsService
    {
        public List<Artist> GetSimilarArtistsWithMetadata(string artistIdentifier, SimilarArtistsProxySettings settings);
    }

    /// <summary>
    /// Service for fetching similar artists from Last.fm API with full metadata
    /// </summary>
    public class LastFmSimilarArtistsService : ILastFmSimilarArtistsService
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private CacheService _cache = null!;
        private LastfmImageScraper _imageScraper = null!;
        private LastfmApiService _apiService = null!;

        private const string LASTFM_API_BASE = "https://ws.audioscrobbler.com/2.0/";

        public LastFmSimilarArtistsService(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public List<Artist> GetSimilarArtistsWithMetadata(string artistIdentifier, SimilarArtistsProxySettings settings)
        {
            if (string.IsNullOrWhiteSpace(artistIdentifier))
            {
                _logger.Warn("Artist identifier is empty, cannot fetch similar artists");
                return [];
            }

            if (string.IsNullOrWhiteSpace(settings?.ApiKey))
            {
                _logger.Warn("Last.fm API key not configured");
                return [];
            }

            InitServices(settings);

            List<LastFmArtist> similarArtists = FetchSimilarArtistsFromApi(artistIdentifier, settings, settings.ResultLimit);

            if (similarArtists.Count == 0)
            {
                _logger.Debug($"No similar artists found for: {artistIdentifier}");
                return [];
            }

            _logger.Trace($"Found {similarArtists.Count} similar artists for '{artistIdentifier}', fetching detailed info...");

            List<Artist> results = [];

            foreach (LastFmArtist similarArtist in similarArtists)
            {
                try
                {
                    // Get detailed artist info from Last.fm API with caching
                    LastfmArtist? detailedArtist = _cache.FetchAndCacheAsync(
                        $"artist:{similarArtist.Name}",
                        () => _apiService.GetArtistInfoAsync(similarArtist.Name)
                    ).GetAwaiter().GetResult();

                    if (detailedArtist == null)
                        continue;
                    if (string.IsNullOrWhiteSpace(detailedArtist.MBID))
                        continue;

                    Artist artist = MapArtistFromLastfmArtist(detailedArtist, artistIdentifier, settings, _imageScraper);
                    results.Add(artist);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, $"Failed to process artist: {similarArtist.Name}");
                }
            }

            _logger.Trace($"Returning {results.Count} similar artists with valid MusicBrainz IDs for '{artistIdentifier}'");
            return results;
        }

        private void InitServices(SimilarArtistsProxySettings settings)
        {
            _cache ??= new CacheService
            {
                CacheDuration = TimeSpan.FromDays(21),
                CacheDirectory = settings.CacheDirectory,
                CacheType = (CacheType)settings.RequestCacheType,
            };

            _imageScraper ??= new LastfmImageScraper(_httpClient, Tubifarry.UserAgent, _cache);

            _apiService ??= new LastfmApiService(_httpClient, Tubifarry.UserAgent)
            {
                ApiKey = settings.ApiKey,
                PageSize = 50,
                MaxPageLimit = 1
            };
        }

        private List<LastFmArtist> FetchSimilarArtistsFromApi(string artistIdentifier, SimilarArtistsProxySettings settings, int limit)
        {
            try
            {
                bool isMbid = Guid.TryParse(artistIdentifier, out _);
                _logger.Trace($"Fetching similar artists for: {artistIdentifier} (using {(isMbid ? "MBID" : "artist name")})");

                HttpRequestBuilder requestBuilder = new HttpRequestBuilder(LASTFM_API_BASE)
                    .AddQueryParam("method", "artist.getSimilar")
                    .AddQueryParam("api_key", settings.ApiKey)
                    .AddQueryParam("format", "json")
                    .AddQueryParam("limit", limit)
                    .SetHeader("User-Agent", Tubifarry.UserAgent)
                    .Accept(HttpAccept.Json);

                if (isMbid)
                    requestBuilder.AddQueryParam("mbid", artistIdentifier);
                else
                    requestBuilder.AddQueryParam("artist", artistIdentifier);

                HttpRequest request = requestBuilder.Build();
                HttpResponse response = _httpClient.Get(request);

                if (response.HasHttpError)
                {
                    _logger.Warn($"HTTP error fetching similar artists for: {artistIdentifier}");
                    return [];
                }

                LastFmSimilarArtistsResponse? apiResponse = JsonSerializer.Deserialize<LastFmSimilarArtistsResponse>(response.Content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (apiResponse?.SimilarArtists?.Artist?.Count > 0)
                {
                    _logger.Debug($"Found {apiResponse.SimilarArtists.Artist.Count} similar artists for '{artistIdentifier}'");
                    return apiResponse.SimilarArtists.Artist;
                }

                _logger.Debug($"No similar artists found for: {artistIdentifier}");
                return [];
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error fetching similar artists for: {artistIdentifier}");
                return [];
            }
        }

        /// <summary>
        /// Maps a Last.fm artist to a Lidarr Artist object using the MusicBrainz ID
        /// </summary>
        private Artist MapArtistFromLastfmArtist(LastfmArtist lastfmArtist, string sourceArtistIdentifier, SimilarArtistsProxySettings settings, LastfmImageScraper imageScraper)
        {
            string foreignArtistId = lastfmArtist.MBID;

            ArtistMetadata metadata = new()
            {
                ForeignArtistId = foreignArtistId,
                Name = lastfmArtist.Name ?? string.Empty,
                Links =
                [
                    new Links { Url = lastfmArtist.Url, Name = "Last.fm" },
                    new Links
                    {
                        Url = $"https://musicbrainz.org/artist/{foreignArtistId}",
                        Name = "MusicBrainz"
                    }
                ],
                Genres = lastfmArtist.Tags?.Tag?.Select(t => t.Name).ToList() ?? [],
                Status = ArtistStatusType.Continuing,
                Type = string.Empty,
                Aliases = []
            };

            // Set overview
            if (lastfmArtist.Bio != null && !string.IsNullOrEmpty(lastfmArtist.Bio.Summary))
            {
                metadata.Overview = $"Artist similar to {sourceArtistIdentifier}. {lastfmArtist.Bio.Summary}";
            }
            else if (lastfmArtist.Stats != null)
            {
                List<string> overviewParts = [$"Artist similar to {sourceArtistIdentifier}"];
                if (lastfmArtist.Stats.PlayCount > 0)
                    overviewParts.Add($"Playcount: {lastfmArtist.Stats.PlayCount:N0}");
                if (!string.IsNullOrEmpty(lastfmArtist.Stats.Listeners))
                    overviewParts.Add($"Listeners: {int.Parse(lastfmArtist.Stats.Listeners):N0}");
                metadata.Overview = string.Join(" • ", overviewParts);
            }
            else
            {
                metadata.Overview = $"Artist similar to {sourceArtistIdentifier}. Found on Last.fm";
            }

            // Calculate rating
            metadata.Ratings = LastfmMappingHelper.ComputeLastfmRating(lastfmArtist.Stats?.Listeners ?? "0", lastfmArtist.Stats?.PlayCount ?? 0);

            // Fetch images if enabled
            if (settings.FetchImages)
                metadata.Images = FetchArtistImages(lastfmArtist.Name!, imageScraper);
            else
                metadata.Images = MapLastfmImages(lastfmArtist.Images);

            return new Artist
            {
                ForeignArtistId = foreignArtistId,
                Name = lastfmArtist.Name,
                SortName = lastfmArtist.Name,
                CleanName = lastfmArtist.Name.CleanArtistName(),
                Monitored = false,
                Metadata = new LazyLoaded<ArtistMetadata>(metadata),
                Albums = new LazyLoaded<List<Album>>([])
            };
        }

        /// <summary>
        /// Fetches artist images using web scraping
        /// </summary>
        private List<MediaCover> FetchArtistImages(string artistName, LastfmImageScraper imageScraper)
        {
            try
            {
                _logger.Trace($"Fetching images for artist: {artistName}");

                List<string> imageUrls = imageScraper.GetArtistImagesAsync(artistName).GetAwaiter().GetResult();

                if (imageUrls.Count == 0)
                {
                    _logger.Trace($"No images found for artist: {artistName}");
                    return [];
                }

                List<MediaCover> mediaCovers = [];
                for (int i = 0; i < Math.Min(imageUrls.Count, 4); i++)
                {
                    MediaCoverTypes coverType = i switch
                    {
                        0 => MediaCoverTypes.Poster,
                        1 => MediaCoverTypes.Fanart,
                        2 => MediaCoverTypes.Banner,
                        _ => MediaCoverTypes.Cover
                    };

                    mediaCovers.Add(new MediaCover(coverType, imageUrls[i]));
                }

                _logger.Trace("Fetched {0} images for {1}", mediaCovers.Count, artistName);
                return mediaCovers;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to fetch images for artist: {artistName}");
                return [];
            }
        }

        /// <summary>
        /// Maps Last.fm API images to MediaCover objects
        /// </summary>
        private static List<MediaCover> MapLastfmImages(List<LastfmImage>? images) => images?
            .Where(i => !string.IsNullOrEmpty(i.Url))
            .Select(i => new MediaCover
            {
                Url = i.Url,
                CoverType = MapImageSize(i.Size)
            }).ToList() ?? [];

        /// <summary>
        /// Maps Last.fm image size to MediaCoverTypes
        /// </summary>
        private static MediaCoverTypes MapImageSize(string size) => size?.ToLowerInvariant() switch
        {
            "mega" or "extralarge" => MediaCoverTypes.Poster,
            "large" => MediaCoverTypes.Fanart,
            "medium" => MediaCoverTypes.Headshot,
            "small" => MediaCoverTypes.Logo,
            _ => MediaCoverTypes.Poster
        };
    }
}