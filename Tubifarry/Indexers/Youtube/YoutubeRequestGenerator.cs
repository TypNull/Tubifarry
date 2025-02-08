using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using Tubifarry.Core.Utilities;
using YouTubeMusicAPI.Internal;
using YouTubeMusicAPI.Types;

namespace Tubifarry.Indexers.Youtube
{
    public interface IYoutubeRequestGenerator : IIndexerRequestGenerator
    {
        public void SetCookies(string path);
    }

    internal class YoutubeRequestGenerator : IYoutubeRequestGenerator
    {
        private const int MaxPages = 3;

        private readonly Logger _logger;
        private string? _cookiePath;

        public YoutubeRequestGenerator(Logger logger) => _logger = logger;

        public IndexerPageableRequestChain GetRecentRequests()
        {
            IndexerPageableRequestChain pageableRequests = new();
            //pageableRequests.Add(GetRecentReleaseRequests());
            return pageableRequests;
        }

        private IEnumerable<IndexerRequest> GetRecentReleaseRequests()
        {
            Dictionary<string, object> payload = Payload.WebRemix(
                geographicalLocation: "US", null, null, null,
                items: new (string key, object? value)[]
                {
            ("browseId", "FEmusic_new_releases"),
            ("params", Extensions.ToParams(YouTubeMusicItemKind.Albums))
                }
            );

            string jsonPayload = JsonConvert.SerializeObject(payload, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

            string url = "https://music.youtube.com/youtubei/v1/browse";
            HttpRequest request = new(url, HttpAccept.Json) { Method = HttpMethod.Post };
            request.SetContent(jsonPayload);

            _logger.Trace($"Created request for recent releases: {url}");

            IndexerRequest req = new(request);
            yield return req;
        }

        public void SetCookies(string path)
        {
            if (string.IsNullOrEmpty(path) || path == _cookiePath)
                return;
            _cookiePath = path;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for album: {searchCriteria.AlbumQuery} by artist: {searchCriteria.ArtistQuery}");
            IndexerPageableRequestChain chain = new();

            string searchQuery = $"album:{searchCriteria.AlbumQuery} artist:{searchCriteria.ArtistQuery}";
            for (int page = 0; page < MaxPages; page++)
                chain.AddTier(GetRequests(searchQuery, YouTubeMusicItemKind.Albums));
            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for artist: {searchCriteria.ArtistQuery}");
            IndexerPageableRequestChain chain = new();

            string searchQuery = $"artist:{searchCriteria.ArtistQuery}";
            for (int page = 0; page < MaxPages; page++)
                chain.AddTier(GetRequests(searchQuery, YouTubeMusicItemKind.Albums));
            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchQuery, YouTubeMusicItemKind kind)
        {
            Dictionary<string, object> payload = Payload.WebRemix("US", null, null, null, new (string key, object? value)[] { ("query", searchQuery), ("params", Extensions.ToParams(kind)), ("continuation", null) });

            string jsonPayload = JsonConvert.SerializeObject(payload, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

            string url = $"https://music.youtube.com/youtubei/v1/search";
            HttpRequest s = new(url, HttpAccept.Json);
            if (!string.IsNullOrEmpty(_cookiePath))
            {
                foreach (System.Net.Cookie cookie in CookieManager.ParseCookieFile(_cookiePath))
                {
                    if (s.Cookies.ContainsKey(cookie.Name))
                        s.Cookies[cookie.Name] = cookie.Value;
                    else
                        s.Cookies.Add(cookie.Name, cookie.Value);
                }
            }

            s.Method = HttpMethod.Post;
            s.SetContent(jsonPayload);
            yield return new(s);
        }
    }
}
