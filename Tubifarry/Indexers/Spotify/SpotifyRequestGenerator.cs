using DownloadAssistant.Base;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using Requests;
using System.Text.Json;

namespace Tubifarry.Indexers.Spotify
{
    public interface ISpotifyRequestGenerator : IIndexerRequestGenerator
    {
        void StartTokenRequest();
        bool TokenIsExpired();
        bool RequestNewToken();
    }

    public class SpotifyRequestGenerator : ISpotifyRequestGenerator
    {
        private const int MaxPages = 3;
        private const int PageSize = 20;
        private const int NewReleaseLimit = 20;

        private string _token = string.Empty;
        private DateTime _tokenExpiry = DateTime.MinValue;
        private OwnRequest? _tokenRequest;

        private readonly Logger _logger;

        public SpotifyRequestGenerator(Logger logger) => _logger = logger;

        public IndexerPageableRequestChain GetRecentRequests()
        {
            IndexerPageableRequestChain pageableRequests = new();
            pageableRequests.Add(GetRecentReleaseRequests());
            return pageableRequests;
        }

        private IEnumerable<IndexerRequest> GetRecentReleaseRequests()
        {
            HandleToken();

            string url = $"https://api.spotify.com/v1/browse/new-releases?limit={NewReleaseLimit}";

            IndexerRequest req = new(url, HttpAccept.Json);
            req.HttpRequest.Headers.Add("Authorization", $"Bearer {_token}");

            _logger.Trace($"Created request for recent releases: {url}");
            yield return req;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for album: {searchCriteria.AlbumQuery} by artist: {searchCriteria.ArtistQuery}");
            IndexerPageableRequestChain chain = new();

            string searchQuery = $"album:{searchCriteria.AlbumQuery} artist:{searchCriteria.ArtistQuery}";
            for (int page = 0; page < MaxPages; page++)
                chain.AddTier(GetRequests(searchQuery, "album", page * PageSize));
            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for artist: {searchCriteria.ArtistQuery}");
            IndexerPageableRequestChain chain = new();

            string searchQuery = $"artist:{searchCriteria.ArtistQuery}";
            for (int page = 0; page < MaxPages; page++)
                chain.AddTier(GetRequests(searchQuery, "album", page * PageSize));
            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchQuery, string searchType, int offset = 0)
        {
            HandleToken();

            string formattedQuery = Uri.EscapeDataString(searchQuery).Replace(":", "%3A");
            string url = $"https://api.spotify.com/v1/search?q={formattedQuery}&type={searchType}&limit={PageSize}&offset={offset}";

            IndexerRequest req = new(url, HttpAccept.Json);
            req.HttpRequest.Headers.Add("Authorization", $"Bearer {_token}");
            _logger.Trace($"Created search request for query '{searchQuery}' (offset {offset}): {url}");
            yield return req;
        }

        private void HandleToken()
        {
            if (RequestNewToken())
                StartTokenRequest();
            if (TokenIsExpired())
                _tokenRequest?.Wait();
        }

        public bool TokenIsExpired() => DateTime.Now >= _tokenExpiry;
        public bool RequestNewToken() => DateTime.Now >= _tokenExpiry.AddMinutes(10);

        public void StartTokenRequest()
        {
            if (string.IsNullOrEmpty(PluginKeys.SpotifyClientId) || string.IsNullOrEmpty(PluginKeys.SpotifyClientSecret))
                return;
            _tokenRequest = new(async (token) =>
            {
                try
                {
                    _logger.Trace("Attempting to create a new Spotify token using official endpoint.");
                    HttpRequestMessage request = new(HttpMethod.Post, "https://accounts.spotify.com/api/token");
                    string credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{PluginKeys.SpotifyClientId}:{PluginKeys.SpotifyClientSecret}"));
                    request.Headers.Add("Authorization", $"Basic {credentials}");
                    request.Content = (FormUrlEncodedContent)new(new Dictionary<string, string> { { "grant_type", "client_credentials" } });
                    System.Net.Http.HttpClient httpClient = HttpGet.HttpClient;
                    HttpResponseMessage response = await httpClient.SendAsync(request, token);
                    response.EnsureSuccessStatusCode();
                    string responseContent = await response.Content.ReadAsStringAsync(token);
                    _logger.Info($"Spotify token response: {responseContent}");
                    JsonElement dynamicObject = JsonSerializer.Deserialize<JsonElement>(responseContent)!;
                    _token = dynamicObject.GetProperty("access_token").GetString() ?? "";
                    if (string.IsNullOrEmpty(_token))
                        return false;
                    int expiresIn = 3600;
                    if (dynamicObject.TryGetProperty("expires_in", out JsonElement expiresElement))
                        expiresIn = expiresElement.GetInt32();
                    _tokenExpiry = DateTime.Now.AddSeconds(expiresIn - 60);
                    _logger.Trace($"Successfully created a new Spotify token. Expires at {_tokenExpiry}");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error occurred while creating a Spotify token.");
                    return false;
                }
                return true;
            }, new() { NumberOfAttempts = 1 });
        }
    }
}