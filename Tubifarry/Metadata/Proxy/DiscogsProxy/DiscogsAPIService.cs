using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using System.Net;
using System.Text.Json;

namespace Tubifarry.Metadata.Proxy.DiscogsProxy
{
    public class DiscogsApiService
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public string? AuthToken { get; set; }
        public string BaseUrl { get; set; } = "https://api.discogs.com";
        public int MaxRetries { get; set; } = 5;
        public int InitialRetryDelayMs { get; set; } = 1000;
        public int MaxPageLimit { get; set; } = 5;
        public int PageSize { get; set; } = 30;

        private int _rateLimitTotal = 60;
        private int _rateLimitUsed = 0;
        private int _rateLimitRemaining = 60;
        private DateTime _lastRequestTime = DateTime.MinValue;
        private TimeSpan _rateLimit = TimeSpan.FromSeconds(0.5);

        public DiscogsApiService(IHttpClient httpClient)
        {
            _httpClient = httpClient;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public async Task<DiscogsRelease?> GetReleaseAsync(int releaseId, string? currency = null)
        {
            HttpRequestBuilder request = BuildRequest($"releases/{releaseId}");
            AddQueryParamIfNotNull(request, "curr_abbr", currency);
            JsonElement response = await ExecuteRequestWithRetryAsync(request);
            return response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DiscogsRelease>(response.GetRawText());
        }

        public async Task<DiscogsMasterRelease?> GetMasterReleaseAsync(int masterId)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"masters/{masterId}"));
            return response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DiscogsMasterRelease>(response.GetRawText());
        }

        public async Task<List<DiscogsMasterReleaseVersion>> GetMasterVersionsAsync(int masterId, int? maxPages = null, string? format = null, string? label = null, string? released = null, string? country = null, string? sort = null, string? sortOrder = null)
        {
            HttpRequestBuilder request = BuildRequest($"masters/{masterId}/versions");
            AddQueryParamIfNotNull(request, "format", format);
            AddQueryParamIfNotNull(request, "label", label);
            AddQueryParamIfNotNull(request, "released", released);
            AddQueryParamIfNotNull(request, "country", country);
            AddQueryParamIfNotNull(request, "sort", sort);
            AddQueryParamIfNotNull(request, "sort_order", sortOrder);
            return await FetchPaginatedResultsAsync<DiscogsMasterReleaseVersion>(request, maxPages ?? MaxPageLimit, PageSize) ?? new();
        }

        public async Task<DiscogsArtist?> GetArtistAsync(int artistId)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"artists/{artistId}"));
            return response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DiscogsArtist>(response.GetRawText());
        }

        public async Task<List<DiscogsArtistRelease>> GetArtistReleasesAsync(int artistId, int? maxPages = null, int? itemsPerPage = null, string? sort = null, string? sortOrder = null)
        {
            HttpRequestBuilder request = BuildRequest($"artists/{artistId}/releases");
            AddQueryParamIfNotNull(request, "sort", sort);
            AddQueryParamIfNotNull(request, "sort_order", sortOrder);
            return await FetchPaginatedResultsAsync<DiscogsArtistRelease>(request, maxPages ?? MaxPageLimit, itemsPerPage ?? PageSize) ?? new();
        }

        public async Task<DiscogsLabel?> GetLabelAsync(int labelId)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"labels/{labelId}"));
            return response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DiscogsLabel>(response.GetRawText());
        }

        public async Task<List<DiscogsLabelRelease>> GetLabelReleasesAsync(int labelId, int? maxPages = null)
        {
            return await FetchPaginatedResultsAsync<DiscogsLabelRelease>(BuildRequest($"labels/{labelId}/releases"), maxPages ?? MaxPageLimit, PageSize) ?? new();
        }

        public async Task<List<DiscogsSearchItem>> SearchAsync(DiscogsSearchParameter searchRequest, int? maxPages = null)
        {
            HttpRequestBuilder request = BuildRequest("database/search");
            AddSearchParams(request, searchRequest);
            return await FetchPaginatedResultsAsync<DiscogsSearchItem>(request, maxPages ?? MaxPageLimit, PageSize) ?? new();
        }

        public async Task<DiscogsStats?> GetReleaseStatsAsync(int releaseId)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"releases/{releaseId}/stats"));
            return response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DiscogsStats>(response.GetRawText());
        }

        public async Task<DiscogsRating?> GetCommunityRatingAsync(int releaseId)
        {
            JsonElement response = await ExecuteRequestWithRetryAsync(BuildRequest($"releases/{releaseId}/rating"));
            return response.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<DiscogsRating>(response.GetRawText());
        }

        private HttpRequestBuilder BuildRequest(string? endpoint)
        {
            HttpRequestBuilder req = new HttpRequestBuilder(BaseUrl)
                .Resource(endpoint);
            if (!string.IsNullOrWhiteSpace(AuthToken))
                req.SetHeader("Authorization", $"Discogs token={AuthToken}");
            req.AllowAutoRedirect = true;
            req.SuppressHttpError = true;
            _logger.Trace($"Building request for endpoint: {endpoint}");
            return req;
        }

        private async Task<JsonElement> ExecuteRequestWithRetryAsync(HttpRequestBuilder requestBuilder, int retryCount = 0)
        {
            try
            {
                await WaitForRateLimit();
                requestBuilder.WithRateLimit(_rateLimit.TotalSeconds);
                HttpRequest request = requestBuilder.Build();
                HttpResponse response = await _httpClient.GetAsync(request);
                UpdateRateLimitTracking(response);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (retryCount >= MaxRetries)
                        return default;

                    int delayMs = InitialRetryDelayMs * (int)Math.Pow(2, retryCount);
                    _logger.Warn($"Rate limit exceeded. Retrying in {delayMs}ms...");
                    await Task.Delay(delayMs);
                    return await ExecuteRequestWithRetryAsync(requestBuilder, retryCount + 1);
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    HandleErrorResponse(response);
                    return default;
                }

                using JsonDocument jsonDoc = JsonDocument.Parse(response.Content);
                return jsonDoc.RootElement.Clone();
            }
            catch (HttpException ex)
            {
                _logger.Warn($"API Error: {ex.Message}");
                return default;
            }
        }

        private async Task WaitForRateLimit()
        {
            if (_rateLimitRemaining <= 0)
            {
                TimeSpan timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
                TimeSpan timeToWait = TimeSpan.FromSeconds(60) - timeSinceLastRequest;
                if (timeToWait > TimeSpan.Zero)
                {
                    _logger.Debug($"Rate limit reached. Waiting for {timeToWait.TotalSeconds} seconds...");
                    await Task.Delay(timeToWait);
                }
                _rateLimitRemaining = _rateLimitTotal;
                _rateLimitUsed = 0;
            }
        }

        private void UpdateRateLimitTracking(HttpResponse response)
        {
            string? totalHeader = response.Headers.Get("X-Discogs-Ratelimit");
            string? usedHeader = response.Headers.Get("X-Discogs-Ratelimit-Used");
            string? remainingHeader = response.Headers.Get("X-Discogs-Ratelimit-Remaining");

            if (!string.IsNullOrEmpty(totalHeader) && int.TryParse(totalHeader, out int total))
                _rateLimitTotal = total;

            if (!string.IsNullOrEmpty(usedHeader) && int.TryParse(usedHeader, out int used))
                _rateLimitUsed = used;

            if (!string.IsNullOrEmpty(remainingHeader) && int.TryParse(remainingHeader, out int remaining))
                _rateLimitRemaining = remaining;

            _lastRequestTime = DateTime.UtcNow;
            _logger.Trace($"Rate limit updated - Total: {_rateLimitTotal}, Used: {_rateLimitUsed}, Remaining: {_rateLimitRemaining}");
        }

        private async Task<List<T>?> FetchPaginatedResultsAsync<T>(HttpRequestBuilder requestBuilder, int? maxPages, int? itemsPerPage)
        {
            List<T> results = new();
            int page = 1;
            bool hasNextPage = true;

            while (hasNextPage)
            {
                HttpRequestBuilder pagedRequest = requestBuilder
                    .AddQueryParam("page", page.ToString(), true)
                    .AddQueryParam("per_page", itemsPerPage.ToString(), true);
                JsonElement response = await ExecuteRequestWithRetryAsync(pagedRequest);

                if (response.TryGetProperty("results", out JsonElement resultsElement) || response.TryGetProperty("releases", out resultsElement))
                {
                    List<T>? pageResults = JsonSerializer.Deserialize<List<T>>(resultsElement.GetRawText());
                    if (pageResults != null)
                        results.AddRange(pageResults);
                }
                else break;

                hasNextPage = response.TryGetProperty("pagination", out JsonElement pagination) && pagination.TryGetProperty("pages", out JsonElement pages) && pages.TryGetInt32(out int pagesInt) && pagesInt > page;

                if (maxPages.HasValue && page >= maxPages.Value) break;
                else if (page >= MaxPageLimit) break;


                page++;
            }

            _logger.Trace($"Fetched {results.Count} results across {page} pages.");
            return results;
        }

        private static void AddSearchParams(HttpRequestBuilder requestBuilder, DiscogsSearchParameter searchRequest)
        {
            foreach (KeyValuePair<string, string> param in searchRequest.ToDictionary())
                requestBuilder.AddQueryParam(param.Key, param.Value);
        }

        private void HandleErrorResponse(HttpResponse response)
        {
            try
            {
                using JsonDocument jsonDoc = JsonDocument.Parse(response.Content);
                JsonElement root = jsonDoc.RootElement;
                string errorMessage = root.GetProperty("message").GetString() ?? $"API Error: {response.StatusCode}";
                _logger.Warn(errorMessage);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to parse API error response. Status Code: {response.StatusCode}");
            }
        }

        private HttpRequestBuilder AddQueryParamIfNotNull(HttpRequestBuilder request, string key, string? value) => value != null ? request.AddQueryParam(key, value) : request;

    }
}
