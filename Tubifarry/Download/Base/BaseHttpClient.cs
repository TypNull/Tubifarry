using DownloadAssistant.Base;

namespace Tubifarry.Download.Base
{
    /// <summary>
    /// HTTP client wrapper for download operations
    /// Provides standardized HTTP operations with proper headers and error handling
    /// Never modifies the shared HttpClient - uses individual requests with proper headers
    /// </summary>
    public class BaseHttpClient
    {
        private readonly HttpClient _httpClient;
        private readonly TimeSpan _timeout;

        /// <summary>
        /// Initializes a new instance of the BaseHttpClient
        /// </summary>
        /// <param name="baseUrl">Base URL for the service instance</param>
        /// <param name="timeout">Request timeout (default: 60 seconds)</param>
        public BaseHttpClient(string baseUrl, TimeSpan? timeout = null)
        {
            BaseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
            _httpClient = HttpGet.HttpClient;
            _timeout = timeout ?? TimeSpan.FromSeconds(60);
        }

        /// <summary>
        /// Gets the base URL for this service instance
        /// </summary>
        public string BaseUrl { get; }

        /// <summary>
        /// Creates a properly configured HttpRequestMessage with standard headers
        /// </summary>
        /// <param name="method">HTTP method</param>
        /// <param name="url">The URL to request (can be relative or absolute)</param>
        /// <returns>Configured HttpRequestMessage</returns>
        private HttpRequestMessage CreateRequest(HttpMethod method, string url)
        {
            string requestUrl = url.StartsWith("http") ? url : new Uri(new Uri(BaseUrl), url).ToString();

            HttpRequestMessage request = new(method, requestUrl);

            request.Headers.Add("User-Agent", Tubifarry.UserAgent);
            request.Headers.Add("Accept", "application/json,text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");

            return request;
        }

        /// <summary>
        /// Performs a GET request and returns the response as a string
        /// </summary>
        /// <param name="url">The URL to request (can be relative or absolute)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Response content as string</returns>
        /// <exception cref="Exception">Thrown when the request fails</exception>
        public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
        {
            try
            {
                using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url);
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_timeout);

                using HttpResponseMessage response = await _httpClient.SendAsync(request, cts.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cts.Token);
            }
            catch (Exception ex)
            {
                throw new Exception($"HTTP GET request failed for URL '{url}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Performs a GET request and returns the HttpResponseMessage
        /// </summary>
        /// <param name="url">The URL to request (can be relative or absolute)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>HTTP response message</returns>
        /// <exception cref="Exception">Thrown when the request fails</exception>
        public async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken = default)
        {
            try
            {
                using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url);
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_timeout);

                return await _httpClient.SendAsync(request, cts.Token);
            }
            catch (Exception ex)
            {
                throw new Exception($"HTTP GET request failed for URL '{url}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Performs a POST request with the provided HTTP request message
        /// Adds standard headers to the request if not already present
        /// </summary>
        /// <param name="request">The HTTP request message to send</param>
        /// <returns>HTTP response message</returns>
        /// <exception cref="Exception">Thrown when the request fails</exception>
        public async Task<HttpResponseMessage> PostAsync(HttpRequestMessage request)
        {
            try
            {
                // Add standard headers if not already present
                if (!request.Headers.Contains("User-Agent"))
                    request.Headers.Add("User-Agent", Tubifarry.UserAgent);

                if (!request.Headers.Contains("Accept"))
                    request.Headers.Add("Accept", "application/json,text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");

                using CancellationTokenSource cts = new(_timeout);
                return await _httpClient.SendAsync(request, cts.Token);
            }
            catch (Exception ex)
            {
                throw new Exception($"HTTP POST request failed for URL '{request.RequestUri}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Performs a POST request with string content
        /// </summary>
        /// <param name="url">The URL to post to (can be relative or absolute)</param>
        /// <param name="content">The content to post</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>HTTP response message</returns>
        /// <exception cref="Exception">Thrown when the request fails</exception>
        public async Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken cancellationToken = default)
        {
            try
            {
                using HttpRequestMessage request = CreateRequest(HttpMethod.Post, url);
                request.Content = content;

                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_timeout);

                return await _httpClient.SendAsync(request, cts.Token);
            }
            catch (Exception ex)
            {
                throw new Exception($"HTTP POST request failed for URL '{url}': {ex.Message}", ex);
            }
        }
    }
}
