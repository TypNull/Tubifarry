using DownloadAssistant.Base;
using NLog;
using NzbDrone.Common.Instrumentation;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Records;
using Tubifarry.Core.Utilities;
using YouTubeMusicAPI.Client;
using YouTubeSessionGenerator;
using YouTubeSessionGenerator.Js.Environments;

namespace Tubifarry.Download.Clients.YouTube
{
    /// <summary>
    /// A centralized helper for managing YouTube trusted session authentication
    /// </summary>
    public class TrustedSessionHelper
    {
        private static readonly Logger _logger = NzbDroneLogger.GetLogger(typeof(TrustedSessionHelper));

        private static SessionTokens? _cachedTokens;
        private static bool? _nodeJsAvailable;
        private static object _nodeJsCheckLock = new();
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

        // Constants for retry logic
        private const int MaxRetries = 3;

        private const int RetryDelayMs = 2000;

        /// <summary>
        /// Gets trusted session tokens, using cache if available and valid
        /// </summary>
        public static async Task<SessionTokens> GetTrustedSessionTokensAsync(string? serviceUrl = null, bool forceRefresh = false, CancellationToken token = default)
        {
            try
            {
                await _semaphore.WaitAsync(token);

                if (!forceRefresh && _cachedTokens?.IsValid == true)
                {
                    _logger.Trace($"Using cached trusted session tokens from {_cachedTokens.Source}, expires in {_cachedTokens.TimeUntilExpiry:hh\\:mm\\:ss}");
                    return _cachedTokens;
                }

                SessionTokens newTokens = new("", "", DateTime.UtcNow.AddHours(12));

                if (!string.IsNullOrEmpty(serviceUrl))
                {
                    _logger.Trace($"Using web service approach with URL: {serviceUrl}");
                    newTokens = await GetTokensFromWebServiceAsync(serviceUrl, forceRefresh, token);
                }
                else if (IsNodeJsAvailable())
                {
                    _logger.Trace("Using local YouTubeSessionGenerator");
                    newTokens = await GetTokensFromLocalGeneratorAsync(token);
                }

                _cachedTokens = newTokens;
                return newTokens;
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

        /// <summary>
        /// Creates session information based on the provided authentication configuration
        /// </summary>
        public static async Task<ClientSessionInfo> CreateSessionInfoAsync(string? trustedSessionGeneratorUrl = null, string? cookiePath = null, bool forceRefresh = false)
        {
            SessionTokens? effectiveTokens = null;
            Cookie[]? cookies = null;
            try
            {
                effectiveTokens = await GetTrustedSessionTokensAsync(trustedSessionGeneratorUrl, forceRefresh);
                _logger.Trace($"Successfully retrieved tokens from {effectiveTokens.Source}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to retrieve tokens for session");
            }
            if (!string.IsNullOrEmpty(cookiePath))
                cookies = LoadCookies(cookiePath);

            return new ClientSessionInfo(effectiveTokens, cookies);
        }

        /// <summary>
        /// Creates an authenticated YouTube Music client from session information
        /// </summary>
        public static YouTubeMusicClient CreateAuthenticatedClient(ClientSessionInfo sessionInfo)
        {
            YouTubeMusicClient client = new(
                // logger: new YouTubeSessionGeneratorLogger(_logger),
                geographicalLocation: sessionInfo.GeographicalLocation,
                visitorData: sessionInfo.Tokens?.VisitorData,
                poToken: sessionInfo.Tokens?.PoToken,
                cookies: sessionInfo.Cookies
                );

            _logger.Debug($"Created YouTube client with: {sessionInfo.AuthenticationSummary}");
            return client;
        }

        /// <summary>
        /// Creates an authenticated YouTube Music client with the specified configuration
        /// </summary>
        public static async Task<YouTubeMusicClient> CreateAuthenticatedClientAsync(string? trustedSessionGeneratorUrl = null, string? cookiePath = null, bool forceRefresh = false)
        {
            ClientSessionInfo sessionInfo = await CreateSessionInfoAsync(trustedSessionGeneratorUrl, cookiePath, forceRefresh);
            return CreateAuthenticatedClient(sessionInfo);
        }

        public static Cookie[]? LoadCookies(string cookiePath)
        {
            _logger?.Debug($"Trying to parse cookies from {cookiePath}");
            try
            {
                if (File.Exists(cookiePath))
                {
                    Cookie[] cookies = CookieManager.ParseCookieFile(cookiePath);
                    if (cookies?.Length > 0)
                    {
                        _logger?.Trace($"Successfully parsed {cookies.Length} cookies");
                        return cookies;
                    }
                }
                else
                {
                    _logger?.Warn($"Cookie file not found: {cookiePath}");
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to parse cookies from {cookiePath}");
            }
            return null;
        }

        private static async Task<SessionTokens> GetTokensFromWebServiceAsync(string serviceUrl, bool forceRefresh, CancellationToken token)
        {
            if (string.IsNullOrEmpty(serviceUrl))
                throw new ArgumentNullException(nameof(serviceUrl), "Service URL cannot be null or empty");

            string baseUrl = serviceUrl.TrimEnd('/');
            string endpoint = forceRefresh ? "/update" : "/token";
            string url = baseUrl + endpoint;

            _logger.Trace($"Fetching trusted session tokens from {url}");

            if (forceRefresh)
                return await GetTokenWithRetryAsync(baseUrl, token);
            else
                return await FetchAndParseTokenAsync(url, token);
        }

        private static async Task<SessionTokens> GetTokensFromLocalGeneratorAsync(CancellationToken token)
        {
            NodeEnvironment? nodeEnvironment = null;
            try
            {
                _logger.Trace("Initializing Node.js environment for local token generation");
                nodeEnvironment = new NodeEnvironment();

                YouTubeSessionConfig config = new()
                {
                    JsEnvironment = nodeEnvironment,
                    HttpClient = HttpGet.HttpClient,
                };

                YouTubeSessionCreator creator = new(config);

                _logger.Trace("Generating visitor data and proof of origin token....");
                string visitorData = await creator.VisitorDataAsync(token);
                string poToken = await creator.ProofOfOriginTokenAsync(visitorData, cancellationToken: token);
                DateTime expiryTime = DateTime.UtcNow.AddHours(4);
                SessionTokens sessionTokens = new(poToken, visitorData, expiryTime, "Local Generator");

                _logger.Debug($"Successfully generated trusted session tokens locally. Expiry: {expiryTime}");
                return sessionTokens;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to generate tokens using local YouTubeSessionGenerator");
                throw new TrustedSessionException("Failed to generate tokens using local generator", ex);
            }
            finally
            {
                nodeEnvironment?.Dispose();
            }
        }

        private static async Task<SessionTokens> GetTokenWithRetryAsync(string baseUrl, CancellationToken token)
        {
            string updateUrl = $"{baseUrl}/update";
            HttpRequestMessage request = new(HttpMethod.Get, updateUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await HttpGet.HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            string responseContent = await response.Content.ReadAsStringAsync(token);
            _logger.Info(responseContent);

            if (responseContent.Contains("Update request accepted") || responseContent.Contains("new token will be generated") || !responseContent.Contains("potoken"))
            {
                _logger.Trace("Token update initiated, waiting for completion...");

                string tokenUrl = $"{baseUrl}/token";
                for (int i = 0; i < MaxRetries; i++)
                {
                    try
                    {
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

            try
            {
                return ParseTokenResponse(responseContent, "Web Service");
            }
            catch (JsonException)
            {
                throw new TrustedSessionException($"Received invalid JSON response: {responseContent}");
            }
        }

        private static async Task<SessionTokens> FetchAndParseTokenAsync(string url, CancellationToken token)
        {
            HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage response = await HttpGet.HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            string responseJson = await response.Content.ReadAsStringAsync(token);
            _logger.Trace(responseJson);
            return ParseTokenResponse(responseJson, "Web Service");
        }

        private static SessionTokens ParseTokenResponse(string responseJson, string source)
        {
            JsonDocument jsonDoc = JsonDocument.Parse(responseJson);
            JsonElement root = jsonDoc.RootElement;

            if (!root.TryGetProperty("potoken", out JsonElement poTokenElement) ||
                !root.TryGetProperty("visitor_data", out JsonElement visitorDataElement) ||
                !root.TryGetProperty("updated", out JsonElement updatedElement))
            {
                throw new TrustedSessionException($"Invalid response format from trusted session generator: {responseJson}");
            }

            string? poToken = poTokenElement.GetString();
            string? visitorData = visitorDataElement.GetString();

            if (string.IsNullOrEmpty(poToken) || string.IsNullOrEmpty(visitorData))
                throw new TrustedSessionException("Received empty token values from trusted session generator");

            long updatedTimestamp = updatedElement.GetInt64();
            DateTime updatedDateTime = DateTimeOffset.FromUnixTimeSeconds(updatedTimestamp).DateTime;
            DateTime expiryDateTime = updatedDateTime.AddHours(4);

            SessionTokens sessionTokens = new(poToken, visitorData, expiryDateTime, source);
            _logger.Trace($"Successfully fetched trusted session tokens from {source}. Expiry: {expiryDateTime}");

            return sessionTokens;
        }

        /// <summary>
        /// Validates authentication settings and connectivity
        /// </summary>
        public static async Task ValidateAuthenticationSettingsAsync(string? trustedSessionGeneratorUrl = null, string? cookiePath = null)
        {
            if (string.IsNullOrEmpty(trustedSessionGeneratorUrl) && !IsNodeJsAvailable())
                _logger.Warn("Node.js environment is not available for local token generation.");

            if (!string.IsNullOrEmpty(trustedSessionGeneratorUrl))
            {
                if (!Uri.TryCreate(trustedSessionGeneratorUrl, UriKind.Absolute, out _))
                    throw new ArgumentException($"Invalid trusted session generator URL: {trustedSessionGeneratorUrl}");

                try
                {
                    HttpRequestMessage request = new(HttpMethod.Get, $"{trustedSessionGeneratorUrl.TrimEnd('/')}/token");
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    using HttpResponseMessage response = await HttpGet.HttpClient.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    _logger.Trace("Successfully connected to trusted session generator");
                }
                catch (Exception ex)
                {
                    throw new ArgumentException($"Error connecting to the trusted session generator: {ex.Message}");
                }
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
                        throw new ArgumentException("No valid cookies found in the cookie file");
                }
                catch (Exception ex) when (ex is not ArgumentException && ex is not FileNotFoundException)
                {
                    throw new ArgumentException($"Error parsing cookie file: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Checks if Node.js is available for local token generation
        /// </summary>
        private static bool IsNodeJsAvailable()
        {
            lock (_nodeJsCheckLock)
            {
                if (_nodeJsAvailable.HasValue)
                    return _nodeJsAvailable.Value;
                NodeEnvironment? testEnv = null;
                try
                {
                    _logger.Trace("Checking Node.js availability...");
                    testEnv = new();
                    _nodeJsAvailable = true;
                    _logger.Debug("Node.js environment is available for local token generation");
                    return true;
                }
                catch (Exception ex)
                {
                    _nodeJsAvailable = false;
                    _logger.Trace(ex, "Node.js environment is not available for local token generation: {Message}", ex.Message);
                    return false;
                }
                finally
                {
                    testEnv?.Dispose();
                }
            }
        }

        public static void ClearCache()
        {
            _cachedTokens = null;
            _logger.Trace("Token cache cleared");
        }

        public static SessionTokens? GetCachedTokens() => _cachedTokens;

        /// <summary>
        /// Logger adapter to bridge NLog with Microsoft.Extensions.Logging for YouTubeSessionGenerator
        /// </summary>
        private class YouTubeSessionGeneratorLogger : Microsoft.Extensions.Logging.ILogger
        {
            private readonly Logger _nlogLogger;

            public YouTubeSessionGeneratorLogger(Logger nlogLogger) => _nlogLogger = nlogLogger;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;

                string message = formatter(state, exception);

                LogLevel nlogLevel = logLevel switch
                {
                    Microsoft.Extensions.Logging.LogLevel.Trace => LogLevel.Trace,
                    Microsoft.Extensions.Logging.LogLevel.Debug => LogLevel.Trace,
                    Microsoft.Extensions.Logging.LogLevel.Information => LogLevel.Trace,
                    Microsoft.Extensions.Logging.LogLevel.Warning => LogLevel.Warn,
                    Microsoft.Extensions.Logging.LogLevel.Error => LogLevel.Error,
                    Microsoft.Extensions.Logging.LogLevel.Critical => LogLevel.Fatal,
                    _ => LogLevel.Info
                };

                _nlogLogger.Log(nlogLevel, exception, message);
            }
        }
    }
}