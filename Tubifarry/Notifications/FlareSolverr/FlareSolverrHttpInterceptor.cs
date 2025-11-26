using NLog;
using NzbDrone.Common.Http;

namespace Tubifarry.Notifications.FlareSolverr
{
    public class FlareSolverrHttpInterceptor(
        Logger logger,
        Lazy<IFlareSolverrService> flareService,
        Lazy<IHttpClient> httpClient) : IHttpRequestInterceptor
    {
        private readonly Logger _logger = logger;
        private readonly Lazy<IFlareSolverrService> _flareService = flareService;
        private readonly Lazy<IHttpClient> _httpClient = httpClient;

        private IFlareSolverrService? FlareService
        {
            get
            {
                try
                {
                    return _flareService.Value;
                }
                catch
                {
                    return null;
                }
            }
        }

        public HttpRequest PreRequest(HttpRequest request)
        {
            if (FlareService?.IsEnabled != true)
            {
                return request;
            }

            string host = request.Url.Host;

            if (FlareService.HasValidSolution(host))
            {
                ProtectionSolution? solution = FlareService.GetOrSolveChallenge(host, request.Url.ToString());
                if (solution != null)
                    ApplySolution(request, solution);
            }

            return request;
        }

        public HttpResponse PostResponse(HttpResponse response)
        {
            if (FlareService?.IsEnabled != true || !IsProtectionChallengeDetected(response))
            {
                return response;
            }

            string host = response.Request.Url.Host;
            int retryCount = GetRetryCount(response.Request);
            int maxRetries = FlareService.MaxRetries;

            if (retryCount >= maxRetries)
            {
                _logger.Error("Max retries ({0}) reached for {1}", maxRetries, host);
                return response;
            }

            _logger.Warn("Protection challenge detected for {0} (attempt {1}/{2}) - Status: {3}",
                host, retryCount + 1, maxRetries, response.StatusCode);

            _logger.Trace("Current request has {0} cookies", response.Request.Cookies.Count);

            string baseUrl = $"{response.Request.Url.Scheme}://{response.Request.Url.Host}/";
            ProtectionSolution? solution = FlareService.GetOrSolveChallenge(host, baseUrl, forceNew: true);

            HttpRequest retryRequest = CloneRequest(response.Request);
            SetRetryCount(retryRequest, retryCount + 1);

            if (solution == null)
            {
                _logger.Error("Failed to solve challenge for {0}, retrying anyway (attempt {1}/{2})", host, retryCount + 1, maxRetries);
                return _httpClient.Value.Execute(retryRequest);
            }

            _logger.Info("Challenge solved for {0}, retrying with {1} cookies: [{2}]", host, solution.Cookies.Length, string.Join(", ", solution.Cookies.Select(c => c.Name)));

            ApplySolution(retryRequest, solution);

            return _httpClient.Value.Execute(retryRequest);
        }

        private void ApplySolution(HttpRequest request, ProtectionSolution solution)
        {
            if (!string.IsNullOrWhiteSpace(solution.UserAgent))
                request.Headers["User-Agent"] = solution.UserAgent;

            foreach (FlareCookie cookie in solution.Cookies)
            {
                _logger.Trace("Applying cookie: {0} = {1}... (domain: {2}, protection: {3})",
                    cookie.Name,
                    cookie.Value[..Math.Min(20, cookie.Value.Length)],
                    cookie.Domain,
                    FlareDetector.IsProtectionCookie(cookie.Name));
                request.Cookies[cookie.Name] = cookie.Value;
            }
        }

        private static HttpRequest CloneRequest(HttpRequest original)
        {
            HttpRequest clone = new(original.Url.ToString())
            {
                Method = original.Method,
                AllowAutoRedirect = original.AllowAutoRedirect,
                ContentData = original.ContentData,
                LogHttpError = original.LogHttpError,
                LogResponseContent = original.LogResponseContent,
                RateLimit = original.RateLimit,
                RateLimitKey = original.RateLimitKey,
                RequestTimeout = original.RequestTimeout,
                StoreRequestCookie = original.StoreRequestCookie,
                StoreResponseCookie = original.StoreResponseCookie,
                SuppressHttpError = original.SuppressHttpError,
                SuppressHttpErrorStatusCodes = original.SuppressHttpErrorStatusCodes
            };

            foreach (KeyValuePair<string, string> header in original.Headers)
                clone.Headers[header.Key] = header.Value;

            foreach (KeyValuePair<string, string> cookie in original.Cookies)
                clone.Cookies[cookie.Key] = cookie.Value;

            return clone;
        }

        private static bool IsProtectionChallengeDetected(HttpResponse response)
        {
            HttpResponseMessage httpResponse = new(response.StatusCode)
            {
                Content = new StringContent(response.Content ?? string.Empty)
            };

            if (response.Headers != null)
            {
                foreach (KeyValuePair<string, string> header in response.Headers)
                {
                    try
                    {
                        if (header.Key.Equals("Server", StringComparison.OrdinalIgnoreCase))
                        {
                            httpResponse.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }
                    catch { }
                }
            }

            bool isChallenge = FlareDetector.IsChallengePresent(httpResponse);
            httpResponse.Dispose();

            return isChallenge;
        }

        private static int GetRetryCount(HttpRequest request) =>
            request.Cookies.TryGetValue("_flare_retry", out string? value) && int.TryParse(value, out int count)
                ? count
                : 0;

        private static void SetRetryCount(HttpRequest request, int count) =>
            request.Cookies["_flare_retry"] = count.ToString();
    }
}