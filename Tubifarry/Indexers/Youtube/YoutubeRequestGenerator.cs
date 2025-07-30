using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using System.Net;
using Tubifarry.Core.Utilities;
using YouTubeMusicAPI.Internal;
using YouTubeMusicAPI.Models.Search;

namespace Tubifarry.Indexers.YouTube
{
    public interface IYouTubeRequestGenerator : IIndexerRequestGenerator<ExtendedIndexerPageableRequest>
    {
        void SetCookies(string path);
        void SetTrustedSessionData(string poToken, string visitorData);
    }

    internal class YouTubeRequestGenerator : IYouTubeRequestGenerator
    {
        private const int MaxPages = 3;

        private readonly Logger _logger;
        private string? _cookiePath;
        private string? _poToken;
        private string? _visitorData;

        public YouTubeRequestGenerator(Logger logger) => _logger = logger;

        public IndexerPageableRequestChain<ExtendedIndexerPageableRequest> GetRecentRequests()
        {
            // YouTube doesn't support RSS/recent releases functionality in a traditional sense
            return new ExtendedIndexerPageableRequestChain();
        }

        public IndexerPageableRequestChain<ExtendedIndexerPageableRequest> GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for album: '{searchCriteria.AlbumQuery}' by artist: '{searchCriteria.ArtistQuery}'");

            ExtendedIndexerPageableRequestChain chain = new(5);

            // Primary search: album + artist
            if (!string.IsNullOrEmpty(searchCriteria.AlbumQuery) && !string.IsNullOrEmpty(searchCriteria.ArtistQuery))
            {
                string primaryQuery = $"{searchCriteria.AlbumQuery} {searchCriteria.ArtistQuery}";
                chain.Add(GetRequests(primaryQuery, SearchCategory.Albums));
            }

            // Fallback search: album only
            if (!string.IsNullOrEmpty(searchCriteria.AlbumQuery))
            {
                chain.AddTier(GetRequests(searchCriteria.AlbumQuery, SearchCategory.Albums));
            }

            // Last resort: artist only (still search for albums)
            if (!string.IsNullOrEmpty(searchCriteria.ArtistQuery))
            {
                chain.AddTier(GetRequests(searchCriteria.ArtistQuery, SearchCategory.Albums));
            }

            return chain;
        }

        public IndexerPageableRequestChain<ExtendedIndexerPageableRequest> GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for artist: '{searchCriteria.ArtistQuery}'");

            ExtendedIndexerPageableRequestChain chain = new(5);
            if (!string.IsNullOrEmpty(searchCriteria.ArtistQuery))
                chain.Add(GetRequests(searchCriteria.ArtistQuery, SearchCategory.Albums));


            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchQuery, SearchCategory category)
        {
            for (int page = 0; page < MaxPages; page++)
            {
                Dictionary<string, object> payload = Payload.WebRemix(
                    geographicalLocation: "US",
                    visitorData: _visitorData,
                    poToken: _poToken,
                    signatureTimestamp: null,
                    items: new (string key, object? value)[]
                    {
                        ("query", searchQuery),
                        ("params", ToParams(category)),
                        ("continuation", null)
                    }
                );

                string jsonPayload = JsonConvert.SerializeObject(payload, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

                HttpRequest request = new($"https://music.youtube.com/youtubei/v1/search?key={PluginKeys.YouTubeSecret}", HttpAccept.Json) { Method = HttpMethod.Post };
                if (!string.IsNullOrEmpty(_cookiePath))
                {
                    try
                    {
                        foreach (Cookie cookie in CookieManager.ParseCookieFile(_cookiePath))
                            request.Cookies[cookie.Name] = cookie.Value;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, $"Failed to load cookies from {_cookiePath}");
                    }
                }
                request.SetContent(jsonPayload);
                _logger.Trace($"Created YouTube Music API request for query: '{searchQuery}', category: {category}");

                yield return new IndexerRequest(request);
            }
        }

        public void SetCookies(string path)
        {
            if (string.IsNullOrEmpty(path) || path == _cookiePath)
                return;

            _cookiePath = path;
            _logger.Debug($"Cookie path set: {!string.IsNullOrEmpty(path)}");
        }

        public void SetTrustedSessionData(string poToken, string visitorData)
        {
            _poToken = poToken;
            _visitorData = visitorData;
            _logger.Debug($"Trusted session data set: poToken={!string.IsNullOrEmpty(poToken)}, visitorData={!string.IsNullOrEmpty(visitorData)}");
        }

        public static string? ToParams(SearchCategory? value) =>
           value switch
           {
               SearchCategory.Songs => "EgWKAQIIAWoQEAMQChAJEAQQBRAREBAQFQ%3D%3D",
               SearchCategory.Videos => "EgWKAQIQAWoQEAMQBBAJEAoQBRAREBAQFQ%3D%3D",
               SearchCategory.Albums => "EgWKAQIYAWoQEAMQChAJEAQQBRAREBAQFQ%3D%3D",
               SearchCategory.CommunityPlaylists => "EgeKAQQoAEABahAQAxAKEAkQBBAFEBEQEBAV",
               SearchCategory.Artists => "EgWKAQIgAWoQEAMQChAJEAQQBRAREBAQFQ%3D%3D",
               SearchCategory.Podcasts => "EgWKAQJQAWoQEAMQChAJEAQQBRAREBAQFQ%3D%3D",
               SearchCategory.Episodes => "EgWKAQJIAWoQEAMQChAJEAQQBRAREBAQFQ%3D%3D",
               SearchCategory.Profiles => "EgWKAQJYAWoQEAMQChAJEAQQBRAREBAQFQ%3D%3D",
               _ => null
           };
    }
}