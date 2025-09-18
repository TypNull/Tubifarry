using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using System.Net;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Records;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Deezer
{
    public class DeezerApiService
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly ICircuitBreaker _circuitBreaker;
        private readonly string _userAgent;

        /// <summary>
        /// OAuth access token. When set, requests include access_token.
        /// </summary>
        public string? AuthToken { get; set; }

        public string BaseUrl { get; set; } = "https://api.deezer.com";
        public int MaxRetries { get; set; } = 5;
        public int InitialRetryDelayMs { get; set; } = 1000;
        public int MaxPageLimit { get; set; } = 5;
        public int PageSize { get; set; } = 25;

        private readonly TimeSpan _rateLimit = TimeSpan.FromSeconds(0.5);

        public DeezerApiService(IHttpClient httpClient, string userAgent)
        {
            _httpClient = httpClient;
            _logger = NzbDroneLogger.GetLogger(this);
            _circuitBreaker = CircuitBreakerFactory.GetBreaker(this);
            _userAgent = userAgent;
        }

        /// <summary>
        /// Constructs an HTTP request builder for a given endpoint.
        /// Includes authentication parameters if set.
        /// </summary>
        private HttpRequestBuilder BuildRequest(string endpoint)
        {
            HttpRequestBuilder builder = new HttpRequestBuilder(BaseUrl).Resource(endpoint);
            if (!string.IsNullOrWhiteSpace(AuthToken))
                builder.AddQueryParam("access_token", AuthToken);
            builder.AllowAutoRedirect = true;
            builder.SuppressHttpError = true;
            builder.Headers.Add("User-Agent", _userAgent);
            builder.WithRateLimit(_rateLimit.TotalSeconds);
            _logger.Trace($"Building request for endpoint: {endpoint}");
            return builder;
        }

        /// <summary>
        /// Executes an HTTP request with retry logic for HTTP 429.
        /// </summary>
        private async Task<JsonElement> ExecuteRequestWithRetryAsync(HttpRequestBuilder requestBuilder, int retryCount = 0)
        {
            try
            {
                if (_circuitBreaker.IsOpen)
                {
                    _logger.Warn("Circuit breaker is open, skipping request to Deezer API");
                    return default;
                }

                HttpRequest request = requestBuilder.Build();
                HttpResponse response = await _httpClient.GetAsync(request);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (retryCount >= MaxRetries)
                    {
                        _logger.Warn("Max retries reached due to rate limiting.");
                        _circuitBreaker.RecordFailure();
                        return default;
                    }
                    int delayMs = InitialRetryDelayMs * (int)Math.Pow(2, retryCount);
                    _logger.Warn($"Rate limit exceeded. Retrying in {delayMs}ms...");
                    await Task.Delay(delayMs);
                    return await ExecuteRequestWithRetryAsync(requestBuilder, retryCount + 1);
                }
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    HandleErrorResponse(response);
                    _circuitBreaker.RecordFailure();
                    return default;
                }
                using JsonDocument jsonDoc = JsonDocument.Parse(response.Content);
                _circuitBreaker.RecordSuccess();
                return jsonDoc.RootElement.Clone();
            }
            catch (HttpException ex)
            {
                _logger.Warn($"API Error: {ex.Message}");
                _circuitBreaker.RecordFailure();
                return default;
            }
        }

        /// <summary>
        /// Logs error responses from the API.
        /// </summary>
        private void HandleErrorResponse(HttpResponse response)
        {
            try
            {
                using JsonDocument jsonDoc = JsonDocument.Parse(response.Content);
                JsonElement root = jsonDoc.RootElement;
                string errorMessage = root.TryGetProperty("error", out JsonElement errorElem) &&
                                      errorElem.TryGetProperty("message", out JsonElement msgElem)
                                          ? msgElem.GetString() ?? $"API Error: {response.StatusCode}"
                                          : $"API Error: {response.StatusCode}";
                _logger.Warn(errorMessage);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to parse API error response. Status Code: {response.StatusCode}");
            }
        }

        /// <summary>
        /// Fetches paginated results from a Deezer endpoint.
        /// </summary>
        private async Task<List<T>?> FetchPaginatedResultsAsync<T>(HttpRequestBuilder requestBuilder, int maxPages, int itemsPerPage)
        {
            List<T> results = new();
            int page = 0;
            bool hasNextPage = true;

            while (hasNextPage)
            {
                HttpRequestBuilder pagedRequest = requestBuilder
                    .AddQueryParam("index", (page * itemsPerPage).ToString(), true)
                    .AddQueryParam("limit", itemsPerPage.ToString(), true);
                JsonElement response = await ExecuteRequestWithRetryAsync(pagedRequest);

                if (response.TryGetProperty("data", out JsonElement dataElement))
                {
                    List<T>? pageResults = JsonSerializer.Deserialize<List<T>>(dataElement.GetRawText());
                    if (pageResults != null)
                        results.AddRange(pageResults);
                }
                else { break; }

                hasNextPage = response.TryGetProperty("next", out JsonElement nextElement) &&
                              !string.IsNullOrWhiteSpace(nextElement.GetString());

                if (page >= maxPages - 1)
                    break;

                page++;
            }
            _logger.Trace($"Fetched {results.Count} results across {page + 1} pages.");
            return results;
        }

        // Generic helper for endpoints that return paginated lists.
        private async Task<List<T>?> GetPaginatedDataAsync<T>(string endpoint, int? maxPages = null) where T : MappingAgent
        {
            return MappingAgent.MapAgent(await FetchPaginatedResultsAsync<T>(BuildRequest(endpoint), maxPages ?? MaxPageLimit, PageSize), _userAgent);
        }

        // Single-object fetch methods remain unchanged.
        public async Task<DeezerAlbum?> GetAlbumAsync(long albumId)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"album/{albumId}"));
            DeezerAlbum? album = response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DeezerAlbum>(response.GetRawText());
            return MappingAgent.MapAgent(album, _userAgent);
        }

        public async Task<DeezerArtist?> GetArtistAsync(long artistId)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"artist/{artistId}"));
            return MappingAgent.MapAgent(response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DeezerArtist>(response.GetRawText()), _userAgent);
        }

        public async Task<DeezerChart?> GetChartAsync(int chartId = 0)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"chart/{chartId}"));
            return MappingAgent.MapAgent(response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DeezerChart>(response.GetRawText()), _userAgent);
        }

        public async Task<DeezerEditorial?> GetEditorialAsync(long editorialId)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"editorial/{editorialId}"));
            return MappingAgent.MapAgent(response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DeezerEditorial>(response.GetRawText()), _userAgent);
        }

        public async Task<DeezerGenre?> GetGenreAsync(long genreId)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"genre/{genreId}"));
            return MappingAgent.MapAgent(response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DeezerGenre>(response.GetRawText()), _userAgent);
        }

        public async Task<List<DeezerGenre>?> ListGenresAsync()
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest("genre"));
            return MappingAgent.MapAgent(response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<List<DeezerGenre>>(response.GetRawText()), _userAgent);
        }

        public async Task<DeezerPlaylist?> GetPlaylistAsync(long playlistId)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"playlist/{playlistId}"));
            return MappingAgent.MapAgent(response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DeezerPlaylist>(response.GetRawText()), _userAgent);
        }

        public async Task<DeezerPodcast?> GetPodcastAsync(long podcastId)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"podcast/{podcastId}"));
            return MappingAgent.MapAgent(response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DeezerPodcast>(response.GetRawText()), _userAgent);
        }

        public async Task<DeezerRadio?> GetRadioAsync(long radioId)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"radio/{radioId}"));
            return MappingAgent.MapAgent(response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DeezerRadio>(response.GetRawText()), _userAgent);
        }

        public async Task<DeezerTrack?> GetTrackAsync(long trackId)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"track/{trackId}"));
            return MappingAgent.MapAgent(response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DeezerTrack>(response.GetRawText()), _userAgent);
        }

        public async Task<DeezerUser?> GetUserAsync(long? userId = null)
        {
            string userSegment = userId.HasValue ? userId.Value.ToString() : "me";
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"user/{userSegment}"));
            return MappingAgent.MapAgent(response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DeezerUser>(response.GetRawText()), _userAgent);
        }

        public async Task<DeezerOptions?> GetOptionsAsync()
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest("options"));
            return MappingAgent.MapAgent(response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DeezerOptions>(response.GetRawText()), _userAgent);
        }

        // --- Generic Search Methods ---
        private static readonly Dictionary<Type, string> SearchEndpointMapping = new()
    {
        { typeof(DeezerAlbum), "album" },
        { typeof(DeezerArtist), "artist" },
        { typeof(DeezerPlaylist), "playlist" },
        { typeof(DeezerPodcast), "podcast" },
        { typeof(DeezerRadio), "radio" },
        { typeof(DeezerTrack), "track" },
        { typeof(DeezerUser), "user" }
    };

        public async Task<List<T>?> SearchAsync<T>(DeezerSearchParameter searchRequest, int? maxPages = null)
        {
            if (!SearchEndpointMapping.TryGetValue(typeof(T), out string? endpointSegment))
                throw new InvalidOperationException($"No search endpoint mapping for type {typeof(T).Name}.");
            string endpoint = $"search/{endpointSegment}";
            HttpRequestBuilder request = BuildRequest(endpoint);
            string q = BuildAdvancedSearchQuery(searchRequest);
            request.AddQueryParam("q", q);
            return await FetchPaginatedResultsAsync<T>(request, maxPages ?? MaxPageLimit, PageSize);
        }

        // Alternatively, keep your advanced search method as is.
        private static string BuildAdvancedSearchQuery(DeezerSearchParameter search)
        {
            List<string> parts = new();
            if (!string.IsNullOrWhiteSpace(search.Query))
                parts.Add(search.Query.Trim());
            if (!string.IsNullOrWhiteSpace(search.Artist))
                parts.Add($"artist:\"{search.Artist.Trim()}\"");
            if (!string.IsNullOrWhiteSpace(search.Album))
                parts.Add($"album:\"{search.Album.Trim()}\"");
            if (!string.IsNullOrWhiteSpace(search.Track))
                parts.Add($"track:\"{search.Track.Trim()}\"");
            if (!string.IsNullOrWhiteSpace(search.Label))
                parts.Add($"label:\"{search.Label.Trim()}\"");
            if (search.DurMin.HasValue)
                parts.Add($"dur_min:{search.DurMin.Value}");
            if (search.DurMax.HasValue)
                parts.Add($"dur_max:{search.DurMax.Value}");
            if (search.BpmMin.HasValue)
                parts.Add($"bpm_min:{search.BpmMin.Value}");
            if (search.BpmMax.HasValue)
                parts.Add($"bpm_max:{search.BpmMax.Value}");
            return string.Join(" ", parts);
        }

        public async Task<List<DeezerSearchItem>?> SearchAsync(DeezerSearchParameter searchRequest, int? maxPages = null)
        {
            HttpRequestBuilder request = BuildRequest("search");
            string q = BuildAdvancedSearchQuery(searchRequest);
            request.AddQueryParam("q", q);
            return await FetchPaginatedResultsAsync<DeezerSearchItem>(request, maxPages ?? MaxPageLimit, PageSize);
        }

        // --- Generic Chart Method ---
        private static readonly Dictionary<Type, string> ChartEndpointMapping = new()
    {
        { typeof(DeezerAlbum), "chart/0/albums" },
        { typeof(DeezerArtist), "chart/0/artists" },
        { typeof(DeezerTrack), "chart/0/tracks" }
    };

        public async Task<List<T>?> GetChartDataAsync<T>(int? maxPages = null) where T : MappingAgent
        {
            if (!ChartEndpointMapping.TryGetValue(typeof(T), out string? endpoint))
                throw new InvalidOperationException($"No chart endpoint defined for type {typeof(T).Name}.");
            return await GetPaginatedDataAsync<T>(endpoint, maxPages);
        }

        // --- Generic Artist Sub-Endpoints using static type mappings ---
        private static readonly Dictionary<Type, string> ArtistSubEndpointMapping = new()
    {
        { typeof(DeezerPlaylist), "playlists" },
        { typeof(DeezerRadio), "radio" },
        { typeof(DeezerArtist), "related" },
        { typeof(DeezerAlbum), "albums" },
        { typeof(DeezerTrack), "top" }
    };

        public async Task<List<T>?> GetArtistDataAsync<T>(long artistId, int? maxPages = null) where T : MappingAgent
        {
            if (!ArtistSubEndpointMapping.TryGetValue(typeof(T), out string? subEndpoint))
                throw new InvalidOperationException($"No artist sub endpoint defined for type {typeof(T).Name}.");
            string endpoint = $"artist/{artistId}/{subEndpoint}";
            return await GetPaginatedDataAsync<T>(endpoint, maxPages);
        }


        // --- Generic Album Sub-Endpoints using static type mappings ---
        private static readonly Dictionary<Type, string> AlbumSubEndpointMapping = new() { { typeof(DeezerTrack), "tracks" } };

        public async Task<List<T>?> GetAlbumDataAsync<T>(long albumId, int? maxPages = null) where T : MappingAgent
        {
            if (!AlbumSubEndpointMapping.TryGetValue(typeof(T), out string? subEndpoint))
                throw new InvalidOperationException($"No album sub endpoint defined for type {typeof(T).Name}.");
            string endpoint = $"album/{albumId}/{subEndpoint}";
            return await GetPaginatedDataAsync<T>(endpoint, maxPages);
        }

        // --- Generic Radio Endpoints ---
        public Task<List<DeezerRadio>?> GetRadioListsAsync(int? maxPages = null) =>
            GetPaginatedDataAsync<DeezerRadio>("radio/lists", maxPages);

        public Task<List<DeezerRadio>?> GetRadioTopAsync(int? maxPages = null) =>
            GetPaginatedDataAsync<DeezerRadio>("radio/top", maxPages);

        public Task<List<DeezerGenre>?> GetRadioGenresAsync(int? maxPages = null) =>
            GetPaginatedDataAsync<DeezerGenre>("radio/genres", maxPages);

        public Task<List<DeezerRadio>?> GetPlaylistRadioAsync(long playlistId, int? maxPages = null) =>
            GetPaginatedDataAsync<DeezerRadio>($"playlist/{playlistId}/radio", maxPages);

        public Task<List<DeezerRadio>?> GetRadiosAsync(int? maxPages = null) =>
            GetPaginatedDataAsync<DeezerRadio>("radio", maxPages);

        public Task<List<DeezerTrack>?> GetPlaylistTracksAsync(long playlistId, int? maxPages = null) =>
            GetPaginatedDataAsync<DeezerTrack>($"playlist/{playlistId}/tracks", maxPages);
    }
}