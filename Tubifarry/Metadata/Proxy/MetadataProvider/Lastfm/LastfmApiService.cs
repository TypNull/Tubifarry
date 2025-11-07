using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using System.Net;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Records;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Lastfm
{
    public class LastfmApiService
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly ICircuitBreaker _circuitBreaker;
        private readonly string _userAgent;

        public string? ApiKey { get; set; }
        public string BaseUrl { get; set; } = "https://ws.audioscrobbler.com/2.0/";
        public int MaxRetries { get; set; } = 5;
        public int InitialRetryDelayMs { get; set; } = 1000;
        public int MaxPageLimit { get; set; } = 5;
        public int PageSize { get; set; } = 30;

        private readonly TimeSpan _rateLimit = TimeSpan.FromSeconds(0.25);

        public LastfmApiService(IHttpClient httpClient, string userAgent)
        {
            _userAgent = userAgent;
            _httpClient = httpClient;
            _logger = NzbDroneLogger.GetLogger(this);
            _circuitBreaker = CircuitBreakerFactory.GetBreaker(this);
        }

        /// <summary>
        /// Gets artist info by name
        /// </summary>
        public async Task<LastfmArtist?> GetArtistInfoAsync(string artistName)
        {
            Dictionary<string, string> parameters = new() { { "artist", artistName } };

            JsonElement json = await ExecuteRequestWithRetryAsync(BuildRequest("artist.getinfo", parameters));
            if (json.ValueKind == JsonValueKind.Undefined)
                return null;

            try
            {
                LastfmArtistInfoResponse? response = JsonSerializer.Deserialize<LastfmArtistInfoResponse>(json.GetRawText());
                return MappingAgent.MapAgent(response?.Artist, _userAgent);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error deserializing artist info response");
                return null;
            }
        }

        /// <summary>
        /// Gets artist info by MusicBrainz ID
        /// </summary>
        public async Task<LastfmArtist?> GetArtistInfoByMbidAsync(string mbid)
        {
            Dictionary<string, string> parameters = new() { { "mbid", mbid } };

            JsonElement json = await ExecuteRequestWithRetryAsync(BuildRequest("artist.getinfo", parameters));
            if (json.ValueKind == JsonValueKind.Undefined)
                return null;

            try
            {
                LastfmArtistInfoResponse? response = JsonSerializer.Deserialize<LastfmArtistInfoResponse>(json.GetRawText());
                return MappingAgent.MapAgent(response?.Artist, _userAgent);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error deserializing artist info response");
                return null;
            }
        }

        /// <summary>
        /// Searches for artists by name with pagination
        /// </summary>
        public async Task<List<LastfmArtist>?> SearchArtistsAsync(string query, int? maxPages = null, int? itemsPerPage = null) =>
           (await FetchPaginatedResultsAsync<LastfmArtist>(BuildRequest("artist.search", new() { { "artist", query } }), maxPages ?? MaxPageLimit, itemsPerPage ?? PageSize))?.Select(x => MappingAgent.MapAgent(x, _userAgent)!).ToList();

        /// <summary>
        /// Gets the albums for an artist on Last.fm, ordered by popularity with pagination
        /// </summary>
        public async Task<List<LastfmTopAlbum>?> GetTopAlbumsAsync(string? artistName = null, string? mbid = null, int? maxPages = null, int? itemsPerPage = null, bool autoCorrect = false)
        {
            if (string.IsNullOrEmpty(artistName) && string.IsNullOrEmpty(mbid))
                throw new ArgumentException("Either artistName or mbid must be provided");

            Dictionary<string, string> parameters = [];

            if (!string.IsNullOrEmpty(artistName))
                parameters.Add("artist", artistName);
            if (!string.IsNullOrEmpty(mbid))
                parameters.Add("mbid", mbid);
            if (autoCorrect)
                parameters.Add("autocorrect", "1");

            List<LastfmTopAlbum>? topAlbums = await FetchPaginatedResultsAsync<LastfmTopAlbum>(
                BuildRequest("artist.gettopalbums", parameters),
                maxPages ?? MaxPageLimit,
                itemsPerPage ?? PageSize);
            return topAlbums?.Select(x => MappingAgent.MapAgent(x, _userAgent)!).ToList();
        }

        /// <summary>
        /// Gets album info by name and artist
        /// </summary>
        public async Task<LastfmAlbum?> GetAlbumInfoAsync(string artistName, string albumName)
        {
            Dictionary<string, string> parameters = new()
            {
                { "artist", artistName },
                { "album", albumName }
            };

            JsonElement json = await ExecuteRequestWithRetryAsync(BuildRequest("album.getinfo", parameters));
            if (json.ValueKind == JsonValueKind.Undefined)
                return null;

            try
            {
                LastfmAlbumInfoResponse? response = JsonSerializer.Deserialize<LastfmAlbumInfoResponse>(json.GetRawText());
                return MappingAgent.MapAgent(response?.Album, _userAgent);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error deserializing album info response");
                return null;
            }
        }

        /// <summary>
        /// Gets album info by MusicBrainz ID
        /// </summary>
        public async Task<LastfmAlbum?> GetAlbumInfoByMbidAsync(string mbid)
        {
            Dictionary<string, string> parameters = new() { { "mbid", mbid } };

            JsonElement json = await ExecuteRequestWithRetryAsync(BuildRequest("album.getinfo", parameters));
            if (json.ValueKind == JsonValueKind.Undefined)
                return null;

            try
            {
                LastfmAlbumInfoResponse? response = JsonSerializer.Deserialize<LastfmAlbumInfoResponse>(json.GetRawText());
                return MappingAgent.MapAgent(response?.Album, _userAgent);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error deserializing album info response");
                return null;
            }
        }

        /// <summary>
        /// Searches for albums by name with pagination
        /// </summary>
        public async Task<List<LastfmAlbum>?> SearchAlbumsAsync(string query, int? maxPages = null, int? itemsPerPage = null) =>
            (await FetchPaginatedResultsAsync<LastfmAlbum>(BuildRequest("album.search", new() { { "album", query } }), maxPages ?? MaxPageLimit, itemsPerPage ?? PageSize))?.Select(x => MappingAgent.MapAgent(x, _userAgent)!).ToList();

        /// <summary>
        /// Gets track info by name and artist
        /// </summary>
        public async Task<LastfmTrack?> GetTrackInfoAsync(string artistName, string trackName)
        {
            Dictionary<string, string> parameters = new()
            {
                { "artist", artistName },
                { "track", trackName }
            };

            JsonElement json = await ExecuteRequestWithRetryAsync(BuildRequest("track.getinfo", parameters));
            if (json.ValueKind == JsonValueKind.Undefined)
                return null;

            try
            {
                LastfmTrackInfoResponse? response = JsonSerializer.Deserialize<LastfmTrackInfoResponse>(json.GetRawText());
                return response?.Track;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error deserializing track info response");
                return null;
            }
        }

        /// <summary>
        /// Gets track info by MusicBrainz ID
        /// </summary>
        public async Task<LastfmTrack?> GetTrackInfoByMbidAsync(string mbid)
        {
            Dictionary<string, string> parameters = new() { { "mbid", mbid } };

            JsonElement json = await ExecuteRequestWithRetryAsync(BuildRequest("track.getinfo", parameters));
            if (json.ValueKind == JsonValueKind.Undefined)
                return null;

            try
            {
                LastfmTrackInfoResponse? response = JsonSerializer.Deserialize<LastfmTrackInfoResponse>(json.GetRawText());
                return response?.Track;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error deserializing track info response");
                return null;
            }
        }

        /// <summary>
        /// Searches for tracks by name with pagination
        /// </summary>
        public async Task<List<LastfmTrack>?> SearchTracksAsync(string query, int? maxPages = null, int? itemsPerPage = null) =>
            await FetchPaginatedResultsAsync<LastfmTrack>(BuildRequest("track.search", new() { { "track", query } }), maxPages ?? MaxPageLimit, itemsPerPage ?? PageSize);


        /// <summary>
        /// Generic method for fetching paginated results from Last.fm API.
        /// </summary>
        /// ///TODO: Needs cleaning
        private async Task<List<T>?> FetchPaginatedResultsAsync<T>(HttpRequestBuilder requestBuilder, int maxPages, int itemsPerPage)
        {
            List<T> results = [];
            int page = 1;
            bool hasMorePages = true;

            while (hasMorePages && page <= maxPages)
            {
                HttpRequestBuilder pagedRequest = requestBuilder
                    .AddQueryParam("page", page.ToString(), true)
                    .AddQueryParam("limit", itemsPerPage.ToString(), true);

                JsonElement response = await ExecuteRequestWithRetryAsync(pagedRequest);
                if (response.ValueKind == JsonValueKind.Undefined)
                    break;
                _logger.Trace(response.GetRawText());
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(response.GetRawText());
                    JsonElement root = doc.RootElement;
                    JsonElement? dataArray = null;
                    foreach (JsonProperty property in root.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.Object)
                            continue;

                        // Check each property of this object for arrays
                        foreach (JsonProperty subProperty in property.Value.EnumerateObject())
                        {
                            // If we find an array, assume it's our data
                            if (subProperty.Value.ValueKind == JsonValueKind.Array)
                            {
                                dataArray = subProperty.Value;
                                break;
                            }

                            // Check one level deeper (common for search results)
                            if (subProperty.Value.ValueKind == JsonValueKind.Object)
                            {
                                foreach (JsonProperty subSubProperty in subProperty.Value.EnumerateObject())
                                {
                                    if (subSubProperty.Value.ValueKind == JsonValueKind.Array)
                                    {
                                        dataArray = subSubProperty.Value;
                                        break;
                                    }
                                }

                                if (dataArray.HasValue)
                                    break;
                            }
                        }

                        if (dataArray.HasValue)
                            break;
                    }

                    // If we found an array, deserialize and add to results
                    if (dataArray.HasValue)
                    {
                        List<T>? pageItems = JsonSerializer.Deserialize<List<T>>(dataArray.Value.GetRawText());
                        if (pageItems?.Count > 0)
                        {
                            results.AddRange(pageItems);
                        }
                        else
                        {
                            hasMorePages = false;
                        }
                    }
                    else
                    {
                        hasMorePages = false;
                    }

                    int totalPages = maxPages;

                    // Try to find pagination in common locations
                    foreach (JsonProperty property in root.EnumerateObject())
                    {
                        // Skip if not an object
                        if (property.Value.ValueKind != JsonValueKind.Object)
                            continue;

                        // Look for @attr inside this object
                        if (property.Value.TryGetProperty("@attr", out JsonElement attrElement))
                        {
                            // Try to get totalPages directly
                            if (attrElement.TryGetProperty("totalPages", out JsonElement totalPagesElement) &&
                                totalPagesElement.ValueKind == JsonValueKind.String && int.TryParse(totalPagesElement.GetString(), out int parsedPages))
                            {
                                totalPages = Math.Min(parsedPages, maxPages);
                                break;
                            }

                            // If no totalPages, try calculating from total and perPage
                            if (attrElement.TryGetProperty("total", out JsonElement totalElement) &&
                                attrElement.TryGetProperty("perPage", out JsonElement perPageElement) &&
                                totalElement.ValueKind == JsonValueKind.String &&
                                perPageElement.ValueKind == JsonValueKind.String)
                            {
                                if (int.TryParse(totalElement.GetString(), out int total) &&
                                    int.TryParse(perPageElement.GetString(), out int perPage) &&
                                    perPage > 0)
                                {
                                    totalPages = Math.Min((int)Math.Ceiling((double)total / perPage), maxPages);
                                    break;
                                }
                            }
                        }

                        // Try OpenSearch format (used in search results)
                        if (property.Name == "results")
                        {
                            if (property.Value.TryGetProperty("opensearch:totalResults", out JsonElement totalResults) &&
                                property.Value.TryGetProperty("opensearch:itemsPerPage", out JsonElement itemsPerPageElement) &&
                                totalResults.ValueKind == JsonValueKind.String &&
                                itemsPerPageElement.ValueKind == JsonValueKind.String)
                            {
                                if (int.TryParse(totalResults.GetString(), out int total) &&
                                    int.TryParse(itemsPerPageElement.GetString(), out int perPage) &&
                                    perPage > 0)
                                {
                                    totalPages = Math.Min((int)Math.Ceiling((double)total / perPage), maxPages);
                                    break;
                                }
                            }
                        }
                    }

                    hasMorePages = hasMorePages && page < totalPages;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Error processing response for page {page}");
                    break;
                }

                page++;
            }

            _logger.Trace($"Fetched {results.Count} results across {page - 1} pages.");
            return results;
        }

        /// <summary>
        /// Constructs an HTTP request with the Last.fm API parameters
        /// </summary>
        private HttpRequestBuilder BuildRequest(string method, Dictionary<string, string>? parameters = null)
        {
            HttpRequestBuilder builder = new(BaseUrl);
            builder.Headers.Add("User-Agent", _userAgent);

            builder.AddQueryParam("method", method);
            builder.AddQueryParam("api_key", ApiKey);
            builder.AddQueryParam("format", "json");

            if (parameters != null)
            {
                foreach (KeyValuePair<string, string> param in parameters)
                    builder.AddQueryParam(param.Key, param.Value);
            }

            builder.AllowAutoRedirect = true;
            builder.SuppressHttpError = true;
            builder.WithRateLimit(_rateLimit.TotalSeconds);

            _logger.Trace($"Building request for method: {method}");
            return builder;
        }

        /// <summary>
        /// Executes an HTTP request with retry logic
        /// </summary>
        private async Task<JsonElement> ExecuteRequestWithRetryAsync(HttpRequestBuilder requestBuilder, int retryCount = 0)
        {
            try
            {
                if (_circuitBreaker.IsOpen)
                {
                    _logger.Warn("Circuit breaker is open, skipping request to Last.fm API");
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
                _logger.Trace(response.Content);
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
        /// Logs error responses from the API
        /// </summary>
        private void HandleErrorResponse(HttpResponse response)
        {
            try
            {
                using JsonDocument jsonDoc = JsonDocument.Parse(response.Content);
                JsonElement root = jsonDoc.RootElement;
                if (root.TryGetProperty("error", out JsonElement errorCode) && root.TryGetProperty("message", out JsonElement errorMessage))
                    _logger.Warn($"Last.fm API Error {errorCode.GetInt32()}: {errorMessage.GetString()}");
                else
                    _logger.Warn($"API Error: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to parse API error response. Status Code: {response.StatusCode}");
            }
        }
    }
}