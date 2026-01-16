using DownloadAssistant.Base;
using NLog;
using NzbDrone.Common.Instrumentation;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Records;
using Tubifarry.Core.Utilities;
using YouTubeMusicAPI.Client;
using YouTubeSessionGenerator;
using YouTubeSessionGenerator.BotGuard;
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
        private static string? _cachedVisitorData;
        private static DateTime _visitorDataExpiry = DateTime.MinValue;
        private static bool? _nodeJsAvailable;
        private static readonly object _nodeJsCheckLock = new();
        private static readonly SemaphoreSlim _semaphore = new(1, 1);
        private static readonly SemaphoreSlim _visitorDataSemaphore = new(1, 1);

        /// <summary>
        /// Gets trusted session tokens (session-level), using cache if available and valid
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
                    newTokens = await GetTokensFromWebServiceAsync(serviceUrl, null, token);
                }
                else if (IsNodeJsAvailable())
                {
                    _logger.Trace("Using local YouTubeSessionGenerator");
                    newTokens = await GetTokensFromLocalGeneratorAsync(null, token);
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
        /// Gets video-bound tokens for a specific video ID (not cached)
        /// </summary>
        public static async Task<SessionTokens> GetVideoBoundTokensAsync(string videoId, string? serviceUrl = null, CancellationToken token = default)
        {
            try
            {
                if (!string.IsNullOrEmpty(serviceUrl))
                {
                    _logger.Trace($"Generating video-bound token for {videoId} using bgutil service");
                    return await GetTokensFromWebServiceAsync(serviceUrl, videoId, token);
                }
                else if (IsNodeJsAvailable())
                {
                    _logger.Trace($"Generating video-bound token for {videoId} using local generator");
                    return await GetTokensFromLocalGeneratorAsync(videoId, token);
                }
                else
                {
                    _logger.Warn("No token generation method available. Returning empty tokens.");
                    return new SessionTokens("", "", DateTime.UtcNow.AddMinutes(30));
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to generate video-bound token for {videoId}");
                throw new TrustedSessionException($"Failed to generate video-bound token for {videoId}: {ex.Message}", ex);
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
            HttpClientHandler handler = new()
            {
                UseCookies = false
            };
            HttpClient httpClient = new(handler);

            YouTubeMusicClient client = new(
                // logger: new YouTubeSessionGeneratorLogger(_logger),
                geographicalLocation: sessionInfo.GeographicalLocation,
                visitorData: sessionInfo.Tokens?.VisitorData,
                poToken: sessionInfo.Tokens?.PoToken,
                cookies: sessionInfo.Cookies,
                httpClient: httpClient
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

        /// <summary>
        /// Updates an existing YouTube Music client with video-bound tokens
        /// </summary>
        public static async Task UpdateClientWithVideoBoundTokensAsync(YouTubeMusicClient client, string videoId, string? serviceUrl = null, CancellationToken token = default)
        {
            SessionTokens videoBoundTokens = await GetVideoBoundTokensAsync(videoId, serviceUrl, token);

            client.PoToken = videoBoundTokens.PoToken;
            client.VisitorData = videoBoundTokens.VisitorData;

            _logger.Trace($"Updated client with video-bound tokens for {videoId}");
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

        /// <summary>
        /// Fetches tokens from a Web Service
        /// </summary>
        private static async Task<SessionTokens> GetTokensFromWebServiceAsync(string serviceUrl, string? videoId, CancellationToken token)
        {
            if (string.IsNullOrEmpty(serviceUrl))
                throw new ArgumentNullException(nameof(serviceUrl), "Service URL cannot be null or empty");

            string baseUrl = serviceUrl.TrimEnd('/');
            string url = baseUrl + "/get_pot";

            _logger.Trace($"Fetching token from bgutil service: {url}" + (videoId != null ? $" for video {videoId}" : " (session-level)"));

            // Build request body
            Dictionary<string, object> requestBody = new()
            {
                ["bypass_cache"] = false
            };

            if (!string.IsNullOrEmpty(videoId))
            {
                requestBody["content_binding"] = videoId;
            }

            string jsonBody = JsonSerializer.Serialize(requestBody);

            HttpRequestMessage request = new(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await HttpGet.HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            string responseContent = await response.Content.ReadAsStringAsync(token);
            _logger.Trace($"Received response: {responseContent}");

            return ParseResponse(responseContent, videoId != null ? "bgutil (Video-Bound)" : "bgutil (Session)");
        }

        private static SessionTokens ParseResponse(string responseJson, string source)
        {
            JsonDocument jsonDoc = JsonDocument.Parse(responseJson);
            JsonElement root = jsonDoc.RootElement;

            if (!root.TryGetProperty("poToken", out JsonElement poTokenElement) ||
                !root.TryGetProperty("contentBinding", out JsonElement contentBindingElement) ||
                !root.TryGetProperty("expiresAt", out JsonElement expiresAtElement))
            {
                throw new TrustedSessionException($"Invalid response format from bgutil service: {responseJson}");
            }

            string? poToken = poTokenElement.GetString();
            string? contentBinding = contentBindingElement.GetString();
            string? expiresAtStr = expiresAtElement.GetString();

            if (string.IsNullOrEmpty(poToken) || string.IsNullOrEmpty(contentBinding))
                throw new TrustedSessionException("Received empty token values from bgutil service");

            DateTime expiryDateTime = DateTime.Parse(expiresAtStr!);

            SessionTokens sessionTokens = new(poToken, contentBinding, expiryDateTime, source);
            _logger.Trace($"Successfully fetched tokens from {source}. Expiry: {expiryDateTime}");

            return sessionTokens;
        }

        /// <summary>
        /// Gets or creates cached visitor data (prevents rate limiting from YouTube)
        /// </summary>
        private static async Task<string> GetOrCreateVisitorDataAsync(CancellationToken token)
        {
            await _visitorDataSemaphore.WaitAsync(token);
            try
            {
                if (!string.IsNullOrEmpty(_cachedVisitorData) && DateTime.UtcNow < _visitorDataExpiry)
                {
                    _logger.Trace($"Using cached visitor data, expires in {(_visitorDataExpiry - DateTime.UtcNow):hh\\:mm\\:ss}");
                    return _cachedVisitorData;
                }

                _logger.Trace("Generating fresh visitor data from YouTube (only once per session)...");

                using NodeEnvironment tempEnv = new NodeEnvironment();

                YouTubeSessionConfig config = new()
                {
                    JsEnvironment = tempEnv,
                    HttpClient = HttpGet.HttpClient,
                };
                YouTubeSessionCreator tempCreator = new(config);

                _cachedVisitorData = await tempCreator.VisitorDataAsync(token);
                _visitorDataExpiry = DateTime.UtcNow.AddHours(4);

                _logger.Debug($"Successfully generated visitor data, expires at {_visitorDataExpiry}");
                return _cachedVisitorData;
            }
            finally
            {
                _visitorDataSemaphore.Release();
            }
        }

        /// <summary>
        /// Generates tokens using local YouTubeSessionGenerator
        /// </summary>
        private static async Task<SessionTokens> GetTokensFromLocalGeneratorAsync(string? videoId, CancellationToken token)
        {
            NodeEnvironment? nodeEnvironment = null;
            try
            {

                string visitorData = await GetOrCreateVisitorDataAsync(token);
                _logger.Trace("Initializing Node.js environment for token generation");
                nodeEnvironment = new NodeEnvironment();

                YouTubeSessionConfig config = new()
                {
                    JsEnvironment = nodeEnvironment,
                    HttpClient = HttpGet.HttpClient,
                };

                YouTubeSessionCreator creator = new(config);

                BotGuardContentBinding? contentBinding = null;
                string tokenType = "Session";

                // If videoId is provided, create video-bound token
                if (!string.IsNullOrEmpty(videoId))
                {
                    _logger.Trace($"Creating content binding for video: {videoId}");
                    contentBinding = new BotGuardContentBinding
                    {
                        EncryptedVideoId = videoId
                    };
                    tokenType = "Video-Bound";
                }

                _logger.Trace($"Generating {tokenType} proof of origin token...");
                string mintIdentifier = !string.IsNullOrEmpty(videoId) ? videoId : visitorData;
                string poToken = await creator.ProofOfOriginTokenAsync(mintIdentifier, contentBinding, token);

                DateTime expiryTime = DateTime.UtcNow.AddHours(4);
                SessionTokens sessionTokens = new(poToken, visitorData, expiryTime, $"Local Generator ({tokenType})");

                _logger.Debug($"Successfully generated {tokenType} tokens locally. Expiry: {expiryTime}");
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
                    string testUrl = $"{trustedSessionGeneratorUrl.TrimEnd('/')}/get_pot";
                    string jsonBody = JsonSerializer.Serialize(new { bypass_cache = false });

                    HttpRequestMessage request = new(HttpMethod.Post, testUrl)
                    {
                        Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                    };
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
            _cachedVisitorData = null;
            _visitorDataExpiry = DateTime.MinValue;
            _logger.Trace("Token and visitor data cache cleared");
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