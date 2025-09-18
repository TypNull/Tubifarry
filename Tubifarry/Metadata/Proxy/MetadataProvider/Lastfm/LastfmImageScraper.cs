using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using System.Net;
using System.Text.RegularExpressions;
using Tubifarry.Core.Model;
using Tubifarry.Core.Replacements;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Lastfm
{
    /// <summary>
    /// Simple scraper that extracts image IDs from Last.fm's artist page
    /// and constructs direct URLs to high-quality images
    /// </summary>
    public class LastfmImageScraper
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly ICircuitBreaker _circuitBreaker;
        private readonly string _userAgent;
        private readonly CacheService _cache;

        private static readonly Regex UlContainerRegex = new(
            @"<ul\s+class=""image-list"">(.+?)</ul>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex ImageIdRegex = new(
            @"href=""[^""]*\/\+images\/([0-9a-f]{32})""",
            RegexOptions.Compiled);

        public LastfmImageScraper(IHttpClient httpClient, string userAgent, CacheService cache)
        {
            _httpClient = httpClient;
            _userAgent = userAgent;
            _logger = NzbDroneLogger.GetLogger(this);
            _circuitBreaker = CircuitBreakerFactory.GetBreaker(this);
            _cache = cache;
        }

        /// <summary>
        /// Gets high-quality images for an artist by extracting image IDs from their Last.fm page
        /// </summary>
        /// <param name="artistName">The name of the artist</param>
        /// <returns>A list of direct image URLs</returns>
        public async Task<List<string>> GetArtistImagesAsync(string artistName)
        {
            if (string.IsNullOrWhiteSpace(artistName))
            {
                _logger.Warn("Cannot fetch artist images: Artist name is empty");
                return new List<string>();
            }
            string cacheKey = $"lastfm_artist_images_{artistName.ToLowerInvariant().Replace(" ", "_")}";

            try
            {
                List<string>? cachedResult = await _cache.GetAsync<List<string>>(cacheKey);
                if (cachedResult != null)
                {
                    _logger.Trace($"Cache hit for {artistName}");
                    return cachedResult;
                }
                List<string> fetchedResult = await FetchArtistImagesAsync(artistName);
                if (fetchedResult.Count > 0)
                {
                    await _cache.SetAsync(cacheKey, fetchedResult);
                    _logger.Trace($"Cached {fetchedResult.Count} images for artist {artistName}");
                }
                return fetchedResult;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error in cache operation for {artistName}");
                return new List<string>();
            }
        }

        /// <summary>
        /// Fetches artist images from Last.fm
        /// </summary>
        /// <param name="artistName">The name of the artist</param>
        /// <returns>A list of direct image URLs</returns>
        private async Task<List<string>> FetchArtistImagesAsync(string artistName)
        {
            if (_circuitBreaker.IsOpen)
            {
                _logger.Warn("Circuit breaker is open, skipping request to Last.fm website");
                return new List<string>();
            }

            try
            {
                string safeArtistName = artistName.Replace(" ", "+").Replace("&", "%26");
                string url = $"https://www.last.fm/music/{WebUtility.UrlEncode(safeArtistName)}/+images";

                _logger.Trace($"Fetching artist images from: {url}");

                HttpRequest request = new(url);
                request.Headers.Add("User-Agent", _userAgent);
                HttpResponse response = await _httpClient.GetAsync(request);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Warn($"Failed to get artist images. Status: {response.StatusCode}");
                    _circuitBreaker.RecordFailure();
                    return new List<string>();
                }

                _circuitBreaker.RecordSuccess();
                List<string> imageUrls = ExtractImageUrls(response.Content);

                _logger.Trace($"Found {imageUrls.Count} high-quality images for artist {artistName}");
                return imageUrls;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error fetching artist images for {artistName}");
                _circuitBreaker.RecordFailure();
                return new List<string>();
            }
        }

        /// <summary>
        /// Extracts image IDs from the HTML and constructs direct image URLs
        /// </summary>
        /// <param name="html">HTML content from the artist's images page</param>
        /// <returns>List of full-size image URLs</returns>
        private List<string> ExtractImageUrls(string html)
        {
            List<string> imageUrls = new();

            Match ulMatch = UlContainerRegex.Match(html);
            if (ulMatch.Success && ulMatch.Groups.Count > 1)
            {
                string ulContent = ulMatch.Groups[1].Value;
                _logger.Trace("Found image list container");

                MatchCollection idMatches = ImageIdRegex.Matches(ulContent);
                _logger.Trace($"Found {idMatches.Count} image IDs in container");

                foreach (Match idMatch in idMatches)
                {
                    if (idMatch.Success && idMatch.Groups.Count > 1)
                    {
                        string imageId = idMatch.Groups[1].Value;
                        if (!string.IsNullOrEmpty(imageId))
                        {
                            string imageUrl = $"https://lastfm.freetls.fastly.net/i/u/{imageId}.jpg?{FlexibleHttpDispatcher.UA_PARAM}={_userAgent}";
                            _logger.Trace($"Found image: {imageUrl}");
                            imageUrls.Add(imageUrl);
                        }
                    }
                }
            }
            else
            {
                _logger.Warn("Could not find the image list container in the HTML");
            }

            return imageUrls;
        }
    }
}