using DownloadAssistant.Base;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;
using Requests;
using System.Text.Json;

namespace NzbDrone.Core.Indexers.Spotify
{
    public interface ISpotifyRequestGenerator : IIndexerRequestGenerator
    {
        void StartTokenRequest();
        bool TokenIsExpired();
        bool RequestNewToken();
    }

    public class SpotifyRequestGenerator : ISpotifyRequestGenerator
    {
        private const int MaxPages = 2;
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
            chain.AddTier(GetRequests(searchQuery, "album"));

            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for artist: {searchCriteria.ArtistQuery}");
            IndexerPageableRequestChain chain = new();

            string searchQuery = $"artist:{searchCriteria.ArtistQuery}";
            chain.AddTier(GetRequests(searchQuery, "album"));

            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchQuery, string searchType)
        {
            HandleToken();

            string formattedQuery = Uri.EscapeDataString(searchQuery)
                .Replace("%20", "%2520") // Encode spaces as %2520 for better compatibility
                .Replace(":", "%3A");    // Encode colons as %3A for filters

            for (int page = 0; page < MaxPages; page++)
            {
                int offset = page * PageSize;
                string url = $"https://api.spotify.com/v1/search?q={formattedQuery}&type={searchType}&limit={PageSize}&offset={offset}";

                IndexerRequest req = new(url, HttpAccept.Json);
                req.HttpRequest.Headers.Add("Authorization", $"Bearer {_token}");

                _logger.Trace($"Created search request for query '{searchQuery}' (page {page + 1}): {url}");
                yield return req;
            }
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
            _tokenRequest = new(async (token) =>
            {
                try
                {
                    _logger.Trace("Attempting to create a new Spotify token.");
                    HttpGet getter = new(new(HttpMethod.Get, "https://open.spotify.com/get_access_token?reason=transport&productType=web_player"));
                    _token = await (await getter.LoadResponseAsync()).Content.ReadAsStringAsync(token);
                    JsonElement dynamicObject = JsonSerializer.Deserialize<JsonElement>(_token)!;
                    _token = dynamicObject.GetProperty("accessToken").ToString();
                    if (_token == null)
                        return false;
                    _tokenExpiry = DateTime.Now.AddMinutes(59);
                    _logger.Trace("Successfully created a new Spotify token.");

                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error occurred while creating a Spotify token.");
                    return false;
                }
                return true;
            });
        }
    }
}