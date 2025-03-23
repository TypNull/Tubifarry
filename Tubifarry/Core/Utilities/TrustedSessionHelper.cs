using NLog;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using YouTubeMusicAPI.Client;

namespace Tubifarry.Core.Utilities
{
    /// <summary>
    /// Exceptions specific to the YouTube trusted session authentication process
    /// </summary>
    public class TrustedSessionException : Exception
    {
        public TrustedSessionException(string message) : base(message) { }
        public TrustedSessionException(string message, Exception innerException) : base(message, innerException) { }
        public TrustedSessionException() { }
    }

    /// <summary>
    /// A centralized helper for managing YouTube trusted session authentication
    /// </summary>
    public class TrustedSessionHelper
    {
        private static readonly HttpClient _httpClient = new();
        private static readonly Logger _logger = NzbDrone.Common.Instrumentation.NzbDroneLogger.GetLogger(typeof(TrustedSessionHelper));

        private static string? _poToken;
        private static string? _visitorData;
        private static DateTime _tokenExpiry = DateTime.MinValue;
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

        // Constants for retry logic
        private const int MaxRetries = 3;
        private const int RetryDelayMs = 2000;

        public static async Task<(string poToken, string visitorData)> GetTrustedSessionTokensAsync(string serviceUrl, bool forceRefresh = false, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(serviceUrl))
                throw new ArgumentNullException(nameof(serviceUrl), "Service URL cannot be null or empty");

            try
            {
                await _semaphore.WaitAsync(token);

                if (!forceRefresh && !string.IsNullOrEmpty(_poToken) && !string.IsNullOrEmpty(_visitorData) && DateTime.UtcNow < _tokenExpiry)
                {
                    _logger.Trace("Using cached trusted session tokens");
                    return (_poToken, _visitorData);
                }

                string baseUrl = serviceUrl.TrimEnd('/');
                string endpoint = forceRefresh ? "/update" : "/token";
                string url = $"{baseUrl}{endpoint}";

                _logger.Trace($"Fetching trusted session tokens from {url}");

                if (forceRefresh)
                {
                    // For update requests, use the retry mechanism
                    return await GetTokenWithRetryAsync(baseUrl, token);
                }
                else
                {
                    // For regular token requests
                    return await FetchAndParseTokenAsync(url, token);
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.Error(ex, $"HTTP request to trusted session generator failed: {ex.Message}");
                throw;
            }
            catch (JsonException ex)
            {
                throw new TrustedSessionException("Failed to parse JSON response from trusted session generator", ex);
            }
            catch (Exception ex) when (ex is not TrustedSessionException)
            {
                throw new TrustedSessionException($"Unexpected error fetching trusted session tokens: {ex.Message}", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static async Task<(string poToken, string visitorData)> GetTokenWithRetryAsync(string baseUrl, CancellationToken token)
        {
            // First, trigger the update
            string updateUrl = $"{baseUrl}/update";
            HttpRequestMessage request = new(HttpMethod.Get, updateUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await _httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            string responseContent = await response.Content.ReadAsStringAsync(token);
            _logger.Info(responseContent);

            // Check if we got a "token updating" message
            if (responseContent.Contains("Update request accepted") ||
                responseContent.Contains("new token will be generated") ||
                !responseContent.Contains("potoken"))
            {
                _logger.Trace("Token update initiated, waiting for completion...");

                // Switch to the regular token endpoint for subsequent requests
                string tokenUrl = $"{baseUrl}/token";

                // Try to get the token with retries
                for (int i = 0; i < MaxRetries; i++)
                {
                    try
                    {
                        // Wait before retrying
                        await Task.Delay(RetryDelayMs * (i + 1), token);

                        _logger.Trace($"Retry {i + 1}/{MaxRetries} to get updated token");
                        return await FetchAndParseTokenAsync(tokenUrl, token);
                    }
                    catch (Exception ex)
                    {
                        if (i == MaxRetries - 1)
                        {
                            _logger.Error(ex, "Failed to retrieve token after maximum retry attempts");
                            throw;
                        }

                        _logger.Warn(ex, $"Retry {i + 1}/{MaxRetries} failed. Will try again shortly.");
                    }
                }

                throw new TrustedSessionException("Failed to get token after multiple retries");
            }

            // If we immediately got a token response, parse it
            try
            {
                return ParseTokenResponse(responseContent);
            }
            catch (JsonException)
            {
                throw new TrustedSessionException($"Received invalid JSON response: {responseContent}");
            }
        }

        private static async Task<(string poToken, string visitorData)> FetchAndParseTokenAsync(string url, CancellationToken token)
        {
            HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await _httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync(token);
            _logger.Info(responseJson);

            return ParseTokenResponse(responseJson);
        }

        private static (string poToken, string visitorData) ParseTokenResponse(string responseJson)
        {
            JsonDocument jsonDoc = JsonDocument.Parse(responseJson);
            JsonElement root = jsonDoc.RootElement;

            if (!root.TryGetProperty("potoken", out JsonElement poTokenElement) ||
                !root.TryGetProperty("visitor_data", out JsonElement visitorDataElement) ||
                !root.TryGetProperty("updated", out JsonElement updatedElement))
            {
                throw new TrustedSessionException($"Invalid response format from trusted session generator: {responseJson}");
            }

            string? newPoToken = poTokenElement.GetString();
            string? newVisitorData = visitorDataElement.GetString();

            if (string.IsNullOrEmpty(newPoToken) || string.IsNullOrEmpty(newVisitorData))
                throw new TrustedSessionException("Received empty token values from trusted session generator");

            long updatedTimestamp = updatedElement.GetInt64();
            DateTime updatedDateTime = DateTimeOffset.FromUnixTimeSeconds(updatedTimestamp).DateTime;

            _poToken = newPoToken;
            _visitorData = newVisitorData;
            _tokenExpiry = updatedDateTime.AddHours(4);

            _logger.Trace($"Successfully fetched trusted session tokens. Expiry: {_tokenExpiry}");
            return (newPoToken, newVisitorData);
        }

        public static async Task<YouTubeMusicClient> CreateAuthenticatedClientAsync(
            string? trustedSessionGeneratorUrl = null,
            string? poToken = null,
            string? visitorData = null,
            string? cookiePath = null,
            bool forceRefresh = false,
            Logger? logger = null)
        {
            logger ??= _logger;

            string? effectivePoToken = poToken;
            string? effectiveVisitorData = visitorData;
            Cookie[]? cookies = null;

            // If we have a trusted session generator URL, try to use it
            if (!string.IsNullOrEmpty(trustedSessionGeneratorUrl))
            {
                logger.Trace($"Using trusted session generator at {trustedSessionGeneratorUrl}");
                try
                {
                    (string fetchedPoToken, string fetchedVisitorData) = await GetTrustedSessionTokensAsync(trustedSessionGeneratorUrl, forceRefresh);

                    effectivePoToken = fetchedPoToken;
                    effectiveVisitorData = fetchedVisitorData;
                    logger.Trace("Successfully retrieved tokens from trusted session generator");
                }
                catch (Exception ex)
                {
                    // Log the error but continue with other auth methods if available
                    logger.Error(ex, "Failed to retrieve tokens from trusted session generator");
                }
            }

            // If we have a cookie path, try to use it
            if (!string.IsNullOrEmpty(cookiePath))
            {
                logger.Debug($"Trying to parse cookies from {cookiePath}");
                try
                {
                    if (File.Exists(cookiePath))
                    {
                        cookies = CookieManager.ParseCookieFile(cookiePath);
                        if (cookies?.Length > 0)
                            logger.Trace($"Successfully parsed {cookies.Length} cookies");
                        else
                            logger.Warn($"No valid cookies found in {cookiePath}");
                    }
                    else
                    {
                        logger.Warn($"Cookie file not found: {cookiePath}");
                    }
                }
                catch (Exception ex)
                {
                    // Log the error but continue
                    logger.Error(ex, $"Failed to parse cookies from {cookiePath}");
                }
            }

            // Create client with whatever authentication data we have
            YouTubeMusicClient client = new(
                geographicalLocation: "US",
                visitorData: effectiveVisitorData,
                poToken: effectivePoToken,
                cookies: cookies
            );

            logger.Debug($"Created YouTube client with: cookies={cookies != null}, " +
                $"poToken={!string.IsNullOrEmpty(effectivePoToken)}, " +
                $"visitorData={!string.IsNullOrEmpty(effectiveVisitorData)}");

            return client;
        }

        public static async Task ValidateAuthenticationSettingsAsync(
            string? trustedSessionGeneratorUrl = null,
            string? poToken = null,
            string? visitorData = null,
            string? cookiePath = null)
        {
            // If nothing is provided, validation should pass
            if (string.IsNullOrEmpty(trustedSessionGeneratorUrl) &&
                string.IsNullOrEmpty(poToken) &&
                string.IsNullOrEmpty(visitorData) &&
                string.IsNullOrEmpty(cookiePath))
            {
                _logger.Trace("No authentication settings provided, validation passed");
                return;
            }

            // Validate trusted session generator URL if provided
            if (!string.IsNullOrEmpty(trustedSessionGeneratorUrl))
            {
                if (!Uri.TryCreate(trustedSessionGeneratorUrl, UriKind.Absolute, out _))
                    throw new ArgumentException($"Invalid trusted session generator URL: {trustedSessionGeneratorUrl}", nameof(trustedSessionGeneratorUrl));

                try
                {
                    // Just check if we can connect, don't force refresh during validation
                    HttpRequestMessage request = new(HttpMethod.Get, $"{trustedSessionGeneratorUrl.TrimEnd('/')}/token");
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    using HttpResponseMessage response = await _httpClient.SendAsync(request);
                    response.EnsureSuccessStatusCode();

                    // We don't need to parse the response - just checking if the endpoint is available
                    _logger.Trace("Successfully connected to trusted session generator");
                }
                catch (Exception ex)
                {
                    throw new ArgumentException($"Error connecting to the trusted session generator: {ex.Message}", nameof(trustedSessionGeneratorUrl));
                }
            }

            // Validate poToken and visitorData consistency if either is provided
            if (!string.IsNullOrEmpty(poToken))
            {
                if (poToken.Length < 32 || poToken.Length > 256)
                    throw new ArgumentException("poToken length must be between 32 and 256 characters", nameof(poToken));

                if (string.IsNullOrEmpty(visitorData))
                    throw new ArgumentException("visitorData is required when poToken is provided", nameof(visitorData));
            }
            else if (!string.IsNullOrEmpty(visitorData))
            {
                throw new ArgumentException("poToken is required when visitorData is provided", nameof(poToken));
            }

            // Validate cookie path if provided
            if (!string.IsNullOrEmpty(cookiePath))
            {
                if (!File.Exists(cookiePath))
                    throw new FileNotFoundException("Cookie file not found", cookiePath);

                try
                {
                    Cookie[]? cookies = CookieManager.ParseCookieFile(cookiePath);
                    if (cookies == null || cookies.Length == 0)
                        throw new ArgumentException("No valid cookies found in the cookie file", nameof(cookiePath));
                }
                catch (Exception ex) when (ex is not ArgumentException && ex is not FileNotFoundException)
                {
                    throw new ArgumentException($"Error parsing cookie file: {ex.Message}", nameof(cookiePath));
                }
            }
        }
    }
}