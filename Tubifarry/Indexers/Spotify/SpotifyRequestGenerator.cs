using DownloadAssistant.Base;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using Requests;
using System.Text.Json;
using Tubifarry.Core.Replacements;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Spotify
{
    public interface ISpotifyRequestGenerator : IIndexerRequestGenerator<LazyIndexerPageableRequest>
    {
        void StartTokenRequest();

        bool TokenIsExpired();

        bool RequestNewToken();

        void UpdateSettings(SpotifyIndexerSettings settings);
    }

    internal class SpotifyRequestGenerator(Logger logger) : ISpotifyRequestGenerator
    {
        private const int MaxPages = 3;
        private const int DefaultPageSize = 20;
        private const int DefaultNewReleaseLimit = 30;

        private string _token = string.Empty;
        private DateTime _tokenExpiry = DateTime.MinValue;
        private OwnRequest? _tokenRequest;
        private SpotifyIndexerSettings? _settings;

        private readonly Logger _logger = logger;

        public void UpdateSettings(SpotifyIndexerSettings settings) => _settings = settings;

        private int PageSize => Math.Min(_settings?.MaxSearchResults ?? DefaultPageSize, 50);
        private int NewReleaseLimit => Math.Min(_settings?.MaxSearchResults ?? DefaultNewReleaseLimit, 50);

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetRecentRequests()
        {
            LazyIndexerPageableRequestChain chain = new(10);

            try
            {
                chain.AddFactory(() => GetRecentReleaseRequests());
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to generate recent release requests");
            }

            return chain;
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

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for album: '{searchCriteria.AlbumQuery}' by artist: '{searchCriteria.ArtistQuery}'");

            LazyIndexerPageableRequestChain chain = new(3);

            try
            {
                // Primary search: album + artist
                if (!string.IsNullOrEmpty(searchCriteria.AlbumQuery) && !string.IsNullOrEmpty(searchCriteria.ArtistQuery))
                {
                    string primaryQuery = $"album:{searchCriteria.AlbumQuery} artist:{searchCriteria.ArtistQuery}";
                    chain.AddFactory(() => GetAllPagesForQuery(primaryQuery, "album"), 10);
                }

                // Fallback search: album only
                if (!string.IsNullOrEmpty(searchCriteria.AlbumQuery))
                {
                    string albumQuery = $"album:{searchCriteria.AlbumQuery}";
                    chain.AddTierFactory(() => GetAllPagesForQuery(albumQuery, "album"), 5);
                }

                // Last resort: artist only (albums by that artist)
                if (!string.IsNullOrEmpty(searchCriteria.ArtistQuery))
                {
                    string artistQuery = $"artist:{searchCriteria.ArtistQuery}";
                    chain.AddTierFactory(() => GetAllPagesForQuery(artistQuery, "album"), 3);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to generate album search requests");
            }

            return chain;
        }

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for artist: '{searchCriteria.ArtistQuery}'");

            LazyIndexerPageableRequestChain chain = new(3);

            try
            {
                if (!string.IsNullOrEmpty(searchCriteria.ArtistQuery))
                {
                    string artistQuery = $"artist:{searchCriteria.ArtistQuery}";
                    chain.AddFactory(() => GetAllPagesForQuery(artistQuery, "album"));
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to generate artist search requests");
            }

            return chain;
        }

        private IEnumerable<IndexerRequest> GetAllPagesForQuery(string searchQuery, string searchType)
        {
            for (int page = 0; page < MaxPages; page++)
            {
                foreach (IndexerRequest request in GetRequests(searchQuery, searchType, page * PageSize))
                    yield return request;
            }
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
            string clientId = !string.IsNullOrWhiteSpace(_settings?.CustomSpotifyClientId) ? _settings.CustomSpotifyClientId : PluginKeys.SpotifyClientId;
            string clientSecret = !string.IsNullOrWhiteSpace(_settings?.CustomSpotifyClientSecret) ? _settings.CustomSpotifyClientSecret : PluginKeys.SpotifyClientSecret;

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                _logger.Warn("Spotify Client ID or Secret is not configured. Cannot create token.");
                return;
            }

            bool isCustomCredentials = !string.IsNullOrWhiteSpace(_settings?.CustomSpotifyClientId) && !string.IsNullOrWhiteSpace(_settings?.CustomSpotifyClientSecret);

            if (isCustomCredentials)
                _logger.Debug("Using custom Spotify credentials from indexer settings.");
            else
                _logger.Trace("Using default shared Spotify credentials.");

            _tokenRequest = new(async (token) =>
            {
                try
                {
                    _logger.Trace("Attempting to create a new Spotify token using official endpoint.");
                    HttpRequestMessage request = new(HttpMethod.Post, "https://accounts.spotify.com/api/token");
                    string credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
                    request.Headers.Add("Authorization", $"Basic {credentials}");
                    request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { { "grant_type", "client_credentials" } });
                    System.Net.Http.HttpClient httpClient = HttpGet.HttpClient;
                    HttpResponseMessage response = await httpClient.SendAsync(request, token);
                    response.EnsureSuccessStatusCode();
                    string responseContent = await response.Content.ReadAsStringAsync(token);
                    _logger.Debug($"Spotify token created successfully");
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