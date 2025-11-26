using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using System.Net;
using System.Text.Json;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Deezer
{
    /// <summary>
    /// Handles authentication with Deezer via OAuth.
    /// </summary>
    public class DeezerAuthService
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public DeezerAuthService(IHttpClient httpClient)
        {
            _httpClient = httpClient;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        /// <summary>
        /// Exchanges an OAuth code for an access token.
        /// </summary>
        public async Task<string?> AuthenticateAsync(string appId, string appSecret, string redirectUri, string code)
        {
            string requestUrl = $"https://connect.deezer.com/oauth/access_token.php?app_id={appId}&secret={appSecret}&code={code}&output=json";
            HttpRequest request = new HttpRequestBuilder(requestUrl).Build();
            HttpResponse response = await _httpClient.GetAsync(request);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                using JsonDocument jsonDoc = JsonDocument.Parse(response.Content);
                if (jsonDoc.RootElement.TryGetProperty("access_token", out JsonElement tokenElement))
                {
                    string? token = tokenElement.GetString();
                    _logger.Debug("Successfully authenticated with Deezer.");
                    return token;
                }
            }
            _logger.Warn("Failed to authenticate with Deezer.");
            return null;
        }
    }
}