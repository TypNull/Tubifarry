using DownloadAssistant.Base;
using Tubifarry.Indexers.Lucida;

namespace Tubifarry.Download.Clients.Lucida
{
    /// <summary>
    /// HTTP client wrapper for Lucida operations
    /// Provides standardized HTTP operations with proper headers and error handling
    /// </summary>
    public class LucidaHttpClient
    {
        #region Private Fields

        private readonly HttpClient _httpClient;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the LucidaHttpClient
        /// </summary>
        /// <param name="baseUrl">Base URL for the Lucida instance</param>
        public LucidaHttpClient(string baseUrl)
        {
            BaseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
            _httpClient = HttpGet.HttpClient;

            // Set standard headers for Lucida requests
            _httpClient.DefaultRequestHeaders.Add("User-Agent", LucidaIndexer.UserAgent);
            _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the base URL for this Lucida instance
        /// </summary>
        public string BaseUrl { get; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Performs a GET request and returns the response as a string
        /// </summary>
        /// <param name="url">The URL to request</param>
        /// <returns>Response content as string</returns>
        /// <exception cref="Exception">Thrown when the request fails</exception>
        public async Task<string> GetStringAsync(string url)
        {
            try
            {
                return await _httpClient.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                throw new Exception($"HTTP GET request failed for URL '{url}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Performs a POST request with the provided HTTP request message
        /// </summary>
        /// <param name="request">The HTTP request message to send</param>
        /// <returns>HTTP response message</returns>
        /// <exception cref="Exception">Thrown when the request fails</exception>
        public async Task<HttpResponseMessage> PostAsync(HttpRequestMessage request)
        {
            try
            {
                return await _httpClient.SendAsync(request);
            }
            catch (Exception ex)
            {
                throw new Exception($"HTTP POST request failed for URL '{request.RequestUri}': {ex.Message}", ex);
            }
        }
        #endregion
    }
}
