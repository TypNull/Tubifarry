using DownloadAssistant.Base;
using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Notifications.FlareSolverr
{
    public class FlareSolverrService : IFlareSolverrService, IHandle<ApplicationStartedEvent>
    {
        private readonly INotificationFactory _notificationFactory;
        private readonly INotificationStatusService _notificationStatusService;
        private readonly Logger _logger;
        private readonly CacheService _cache;

        private string? _apiUrl;
        private int _maxRetries = 3;
        private int _maxTimeout = 60000;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public FlareSolverrService(
            INotificationFactory notificationFactory,
            INotificationStatusService notificationStatusService,
            Logger logger)
        {
            _notificationFactory = notificationFactory;
            _notificationStatusService = notificationStatusService;
            _logger = logger;

            _cache = new() { CacheType = CacheType.Memory };
        }

        public bool IsEnabled => !string.IsNullOrEmpty(_apiUrl);
        public int MaxRetries => _maxRetries;

        public void Handle(ApplicationStartedEvent message)
        {
            try
            {
                ConfigureFlareSolverr();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to configure FlareSolverr on application startup");
            }
        }

        public ProtectionSolution? GetOrSolveChallenge(string host, string url, bool forceNew = false)
        {
            if (!IsEnabled)
            {
                _logger.Error("FlareSolverr is not configured");
                return null;
            }

            string cacheKey = $"flare_solution_{host}";

            if (!forceNew)
            {
                ProtectionSolution? cached = _cache.GetAsync<ProtectionSolution>(cacheKey).GetAwaiter().GetResult();
                if (cached?.IsValid == true)
                {
                    _logger.Trace("Using cached solution for {0}, expires in {1:0.0} minutes",
                        host, cached.TimeUntilExpiry.TotalMinutes);
                    return cached;
                }
            }

            _logger.Info("Solving challenge for {0} using URL: {1}", host, url);
            return SolveChallenge(host, url, cacheKey);
        }

        public void InvalidateSolution(string host)
        {
            string cacheKey = $"flare_solution_{host}";
            _cache.SetAsync(cacheKey, (ProtectionSolution?)null).GetAwaiter().GetResult();
            _logger.Debug("Invalidated solution for {0}", host);
        }

        public bool HasValidSolution(string host)
        {
            string cacheKey = $"flare_solution_{host}";
            ProtectionSolution? solution = _cache.GetAsync<ProtectionSolution>(cacheKey).GetAwaiter().GetResult();
            return solution?.IsValid == true;
        }

        private ProtectionSolution? SolveChallenge(string host, string url, string cacheKey)
        {
            try
            {
                FlareResponse? result = SolveAsync(url).GetAwaiter().GetResult();

                if (result?.IsSuccess != true || result.Solution?.HasValidCookies != true)
                {
                    _logger.Warn("Challenge solve unsuccessful for {0}. Status: {1}, Cookies: {2}",
                        host, result?.Status ?? "null", result?.Solution?.Cookies?.Length ?? 0);
                    return null;
                }

                DateTime expiry = DateTime.UtcNow.Add(_cache.CacheDuration);
                ProtectionSolution solution = new(
                    result.Solution.Cookies,
                    result.Solution.UserAgent,
                    expiry,
                    host);

                _cache.SetAsync(cacheKey, solution).GetAwaiter().GetResult();

                _logger.Info("Successfully solved challenge for {0}, cached until {1:yyyy-MM-dd HH:mm:ss} UTC. Cookies: {2}",
                    host, expiry, solution.Cookies.Length);

                return solution;
            }
            catch (FlareException ex)
            {
                _logger.Error(ex, "Exception while solving challenge for {0}: {1}", host, ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unexpected exception while solving challenge for {0}", host);
                return null;
            }
        }

        private async Task<FlareResponse> SolveAsync(string url)
        {
            if (string.IsNullOrEmpty(_apiUrl))
                throw new InvalidOperationException("FlareSolverr API URL is not configured");

            Uri apiEndpoint = new($"{_apiUrl.TrimEnd('/')}/v1");

            FlareRequest request = new(
                Command: "request.get",
                Url: url,
                MaxTimeout: _maxTimeout
            );

            try
            {
                string json = JsonSerializer.Serialize(request, JsonOptions);
                _logger.Trace("Sending request to {0}", apiEndpoint);

                StringContent content = new(json, System.Text.Encoding.UTF8, "application/json");
                HttpResponseMessage response = await HttpGet.HttpClient.PostAsync(apiEndpoint, content);

                _logger.Trace("Received response with status code: {0}", response.StatusCode);

                if (response.StatusCode != HttpStatusCode.OK &&
                    response.StatusCode != HttpStatusCode.InternalServerError)
                {
                    string errorBody = await response.Content.ReadAsStringAsync();
                    _logger.Error("Unexpected HTTP status {0}", response.StatusCode);
                    throw new FlareException($"Unexpected status code from FlareSolverr: {response.StatusCode}");
                }

                string responseText = await response.Content.ReadAsStringAsync();
                FlareResponse? flareResponse = JsonSerializer.Deserialize<FlareResponse>(responseText, JsonOptions) ?? throw new FlareException("FlareSolverr returned null response");

                if (!flareResponse.IsSuccess)
                {
                    string errorMessage = flareResponse.Status.ToLowerInvariant() switch
                    {
                        "warning" => $"Captcha detected: {flareResponse.Message}",
                        "error" => $"FlareSolverr error: {flareResponse.Message}",
                        _ => $"Unknown status '{flareResponse.Status}': {flareResponse.Message}"
                    };
                    _logger.Warn("Request failed - {0}", errorMessage);
                    throw new FlareException(errorMessage);
                }

                if (flareResponse.Solution != null)
                {
                    _logger.Trace("Received response. Duration: {0}ms, Cookies: {1}",
                        flareResponse.DurationMs, flareResponse.Solution.Cookies?.Length ?? 0);

                    if (flareResponse.Solution.Cookies?.Length > 0)
                    {
                        _logger.Trace("Received cookies: {0}",
                            string.Join(", ", flareResponse.Solution.Cookies.Select(c => c.Name)));
                    }
                }

                return flareResponse;
            }
            catch (HttpRequestException ex)
            {
                _logger.Error(ex, "Failed to connect to FlareSolverr API at {0}", apiEndpoint);
                throw new FlareException($"Failed to connect to FlareSolverr: {ex.Message}", ex);
            }
            catch (JsonException ex)
            {
                _logger.Error(ex, "Failed to parse JSON response");
                throw new FlareException($"Failed to parse FlareSolverr response: {ex.Message}", ex);
            }
        }

        private void ConfigureFlareSolverr()
        {
            foreach (INotification? notification in (List<INotification>)_notificationFactory.GetAvailableProviders())
            {
                if (notification is not FlareSolverrNotification)
                    continue;

                if (notification.Definition is not NotificationDefinition definition || !definition.Enable)
                    continue;

                FlareSolverrSettings settings = (FlareSolverrSettings)notification.Definition.Settings;

                try
                {
                    _apiUrl = settings.ApiUrl;
                    _maxRetries = settings.MaxRetries;
                    _maxTimeout = settings.MaxTimeout;
                    _cache.CacheDuration = TimeSpan.FromMinutes(settings.CacheDurationMinutes);

                    HttpGet.HttpClient.Timeout = TimeSpan.FromMilliseconds(_maxTimeout + 5000);

                    TestConnection();

                    _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    _logger.Trace("Successfully configured FlareSolverr: {0}", settings.ApiUrl);
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Error(ex, "Failed to configure FlareSolverr with settings");
                }
            }
        }

        private void TestConnection()
        {
            if (string.IsNullOrEmpty(_apiUrl))
                return;

            try
            {
                Uri indexEndpoint = new(_apiUrl.TrimEnd('/') + "/");
                HttpResponseMessage response = HttpGet.HttpClient.GetAsync(indexEndpoint).GetAwaiter().GetResult();

                if (response.IsSuccessStatusCode)
                {
                    string content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    FlareIndexResponse? indexResponse = JsonSerializer.Deserialize<FlareIndexResponse>(content, JsonOptions);

                    if (indexResponse != null)
                    {
                        _logger.Debug("FlareSolverr connection successful. Version: {0}", indexResponse.Version ?? "Unknown");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Could not verify FlareSolverr connection, but will proceed with configuration");
            }
        }
    }

    public class FlareException : Exception
    {
        public FlareException(string message) : base(message)
        { }

        public FlareException(string message, Exception innerException) : base(message, innerException)
        { }

        public FlareException()
        { }
    }
}