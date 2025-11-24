using DownloadAssistant.Base;
using NzbDrone.Common.Http;

namespace Tubifarry.Download.Base
{
    /// <summary>
    /// HTTP client wrapper for download operations
    /// Provides standardized HTTP operations with proper headers and error handling
    /// Never modifies the shared HttpClient uses individual requests with proper headers
    /// </summary>
    public class BaseHttpClient
    {
        private readonly System.Net.Http.HttpClient _httpClient = HttpGet.HttpClient;
        private readonly TimeSpan _timeout;
        private readonly List<IHttpRequestInterceptor> _requestInterceptors;

        /// <summary>
        /// Initializes a new instance of the BaseHttpClient
        /// </summary>
        /// <param name="baseUrl">Base URL for the service instance</param>
        /// <param name="requestInterceptors">Optional list of request interceptors</param>
        /// <param name="timeout">Request timeout (default: 60 seconds)</param>
        public BaseHttpClient(string baseUrl, IEnumerable<IHttpRequestInterceptor> requestInterceptors, TimeSpan? timeout = null)
        {
            BaseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
            _timeout = timeout ?? TimeSpan.FromSeconds(60);
            _requestInterceptors = requestInterceptors?.ToList() ?? [];
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
        public HttpRequestMessage CreateRequest(HttpMethod method, string url)
        {
            string requestUrl = url.StartsWith("http") ? url : new Uri(new Uri(BaseUrl), url).ToString();

            HttpRequestMessage request = new(method, requestUrl);

            request.Headers.Add("User-Agent", Tubifarry.UserAgent);
            request.Headers.Add("Accept", "application/json,text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");

            return request;
        }

        /// <summary>
        /// Sends an HTTP request with timeout handling and interceptor support
        /// </summary>
        /// <param name="request">The HTTP request to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>HTTP response message</returns>
        /// <exception cref="Exception">Thrown when the request fails</exception>
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            try
            {
                HttpRequest nzbRequest = ConvertToNzbRequest(request);

                foreach (IHttpRequestInterceptor interceptor in _requestInterceptors)
                {
                    nzbRequest = interceptor.PreRequest(nzbRequest);
                }

                ApplyNzbRequestToHttpRequestMessage(nzbRequest, request);

                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_timeout);

                HttpResponseMessage response = await _httpClient.SendAsync(request, cts.Token);

                HttpResponse nzbResponse = await ConvertToNzbResponse(nzbRequest, response);

                foreach (IHttpRequestInterceptor interceptor in _requestInterceptors)
                {
                    nzbResponse = interceptor.PostResponse(nzbResponse);
                }

                return ConvertToHttpResponseMessage(nzbResponse);
            }
            catch (Exception ex)
            {
                throw new Exception($"HTTP request failed for URL '{request.RequestUri}': {ex.Message}", ex);
            }
        }

        private static HttpRequest ConvertToNzbRequest(HttpRequestMessage httpRequest)
        {
            HttpRequest nzbRequest = new(httpRequest.RequestUri?.ToString())
            {
                Method = httpRequest.Method
            };

            foreach (KeyValuePair<string, IEnumerable<string>> header in httpRequest.Headers)
            {
                nzbRequest.Headers[header.Key] = string.Join(", ", header.Value);
            }

            if (httpRequest.Content != null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in httpRequest.Content.Headers)
                {
                    nzbRequest.Headers[header.Key] = string.Join(", ", header.Value);
                }

                byte[] contentBytes = httpRequest.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (contentBytes.Length > 0)
                {
                    nzbRequest.SetContent(contentBytes);
                }
            }

            return nzbRequest;
        }

        private static void ApplyNzbRequestToHttpRequestMessage(HttpRequest nzbRequest, HttpRequestMessage httpRequest)
        {
            // Apply modified headers back to HttpRequestMessage
            foreach (KeyValuePair<string, string> header in nzbRequest.Headers)
            {
                if (httpRequest.Headers.Contains(header.Key))
                {
                    httpRequest.Headers.Remove(header.Key);
                }
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // Apply cookies
            if (nzbRequest.Cookies.Count > 0)
            {
                string cookieHeader = string.Join("; ", nzbRequest.Cookies.Select(c => $"{c.Key}={c.Value}"));
                if (httpRequest.Headers.Contains("Cookie"))
                {
                    httpRequest.Headers.Remove("Cookie");
                }
                httpRequest.Headers.Add("Cookie", cookieHeader);
            }
        }

        private static async Task<HttpResponse> ConvertToNzbResponse(HttpRequest nzbRequest, HttpResponseMessage httpResponse)
        {
            HttpHeader headers = [];

            foreach (KeyValuePair<string, IEnumerable<string>> header in httpResponse.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            if (httpResponse.Content != null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in httpResponse.Content.Headers)
                {
                    headers[header.Key] = string.Join(", ", header.Value);
                }
            }

            byte[] responseData = [];
            if (httpResponse.Content != null)
            {
                responseData = await httpResponse.Content.ReadAsByteArrayAsync();
            }

            return new HttpResponse(nzbRequest, headers, responseData, httpResponse.StatusCode, httpResponse.Version);
        }

        private static HttpResponseMessage ConvertToHttpResponseMessage(HttpResponse nzbResponse)
        {
            HttpResponseMessage httpResponse = new(nzbResponse.StatusCode)
            {
                Version = nzbResponse.Version,
                Content = new ByteArrayContent(nzbResponse.ResponseData ?? [])
            };

            foreach (KeyValuePair<string, string> header in nzbResponse.Headers)
            {
                if (!httpResponse.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    httpResponse.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return httpResponse;
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
                using HttpResponseMessage response = await SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_timeout);

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
                return await SendAsync(request, cancellationToken);
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
                if (!request.Headers.Contains("User-Agent"))
                    request.Headers.Add("User-Agent", Tubifarry.UserAgent);

                if (!request.Headers.Contains("Accept"))
                    request.Headers.Add("Accept", "application/json,text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");

                return await SendAsync(request);
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

                return await SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new Exception($"HTTP POST request failed for URL '{url}': {ex.Message}", ex);
            }
        }
    }
}
