using System.Text;
using System.Text.RegularExpressions;

namespace Tubifarry.Download.Clients.Lucida
{
    /// <summary>
    /// Helper class for HTTP operations with Lucida
    /// </summary>
    public class LucidaHttpClient : IDisposable
    {
        // Compiled regexes for better performance
        private static readonly Regex HttpsRegex = new(@"^https?://", RegexOptions.Compiled);

        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(30);

        public LucidaHttpClient(string baseUrl)
        {
            _baseUrl = baseUrl;
            _httpClient = new HttpClient
            {
                Timeout = _defaultTimeout
            };

            // Common headers for all requests
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        }

        /// <summary>
        /// Gets the base URL
        /// </summary>
        public string BaseUrl => _baseUrl;

        /// <summary>
        /// Performs a GET request and returns the response as a string
        /// </summary>
        public async Task<string> GetStringAsync(string url)
        {
            try
            {
                return await _httpClient.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error in GetStringAsync: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Performs a GET request and returns the response
        /// </summary>
        public async Task<HttpResponseMessage> GetAsync(string url)
        {
            try
            {
                return await _httpClient.GetAsync(url);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error in GetAsync: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Performs a POST request with JSON content
        /// </summary>
        public async Task<HttpResponseMessage> PostJsonAsync(string url, string jsonContent)
        {
            try
            {
                HttpRequestMessage request = new(HttpMethod.Post, url);
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "text/plain");

                // Set content type header explicitly
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
                request.Content.Headers.ContentType.CharSet = "UTF-8";

                // Add required headers
                request.Headers.Add("Origin", _baseUrl);

                return await _httpClient.SendAsync(request);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error in PostJsonAsync: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Performs a POST request with a request object
        /// </summary>
        public async Task<HttpResponseMessage> PostAsync(HttpRequestMessage request)
        {
            try
            {
                return await _httpClient.SendAsync(request);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error in PostAsync: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Creates an absolute URL from a relative path
        /// </summary>
        public string CreateAbsoluteUrl(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return null;

            if (HttpsRegex.IsMatch(relativePath))
                return relativePath;

            return relativePath.StartsWith("/")
                ? $"{_baseUrl}{relativePath}"
                : $"{_baseUrl}/{relativePath}";
        }

        /// <summary>
        /// Sets a custom timeout for requests
        /// </summary>
        public void SetTimeout(TimeSpan timeout)
        {
            _httpClient.Timeout = timeout;
        }

        /// <summary>
        /// Resets timeout to default value
        /// </summary>
        public void ResetTimeout()
        {
            _httpClient.Timeout = _defaultTimeout;
        }

        /// <summary>
        /// Adds a custom header to the client
        /// </summary>
        public void AddHeader(string name, string value)
        {
            if (_httpClient.DefaultRequestHeaders.Contains(name))
                _httpClient.DefaultRequestHeaders.Remove(name);

            _httpClient.DefaultRequestHeaders.Add(name, value);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}