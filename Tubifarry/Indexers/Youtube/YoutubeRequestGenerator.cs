using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using System.Net;
using Tubifarry.Core.Records;
using Tubifarry.Core.Replacements;
using Tubifarry.Core.Utilities;
using Tubifarry.Download.Clients.YouTube;
using YouTubeMusicAPI.Internal;
using YouTubeMusicAPI.Models.Search;

namespace Tubifarry.Indexers.YouTube
{

    internal class YouTubeRequestGenerator : IIndexerRequestGenerator<LazyIndexerPageableRequest>
    {
        private const int MaxPages = 3;

        private readonly Logger _logger;

        private readonly YouTubeIndexer _youTubeIndexer;
        private SessionTokens? _sessionToken;

        public YouTubeRequestGenerator(YouTubeIndexer indexer)
        {
            _youTubeIndexer = indexer;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetRecentRequests()
        {
            // YouTube doesn't support RSS/recent releases functionality in a traditional sense
            return new LazyIndexerPageableRequestChain();
        }

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for album: '{searchCriteria.AlbumQuery}' by artist: '{searchCriteria.ArtistQuery}'");

            LazyIndexerPageableRequestChain chain = new(5);

            // Primary search: album + artist
            if (!string.IsNullOrEmpty(searchCriteria.AlbumQuery) && !string.IsNullOrEmpty(searchCriteria.ArtistQuery))
            {
                string primaryQuery = $"{searchCriteria.AlbumQuery} {searchCriteria.ArtistQuery}";
                chain.AddFactory(() => GetRequests(primaryQuery, SearchCategory.Albums));
            }

            // Fallback search: album only
            if (!string.IsNullOrEmpty(searchCriteria.AlbumQuery))
            {
                chain.AddTierFactory(() => GetRequests(searchCriteria.AlbumQuery, SearchCategory.Albums));
            }

            // Last resort: artist only (still search for albums)
            if (!string.IsNullOrEmpty(searchCriteria.ArtistQuery))
            {
                chain.AddTierFactory(() => GetRequests(searchCriteria.ArtistQuery, SearchCategory.Albums));
            }

            return chain;
        }

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for artist: '{searchCriteria.ArtistQuery}'");

            LazyIndexerPageableRequestChain chain = new(5);
            if (!string.IsNullOrEmpty(searchCriteria.ArtistQuery))
                chain.AddFactory(() => GetRequests(searchCriteria.ArtistQuery, SearchCategory.Albums));

            return chain;
        }

        private void UpdateTokens()
        {
            if (_sessionToken?.IsValid == true)
                return;
            _sessionToken = TrustedSessionHelper.GetTrustedSessionTokensAsync(_youTubeIndexer.Settings.TrustedSessionGeneratorUrl).Result;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchQuery, SearchCategory category)
        {
            UpdateTokens();

            for (int page = 0; page < MaxPages; page++)
            {
                Dictionary<string, object> payload = Payload.WebRemix(
                    geographicalLocation: "US",
                    visitorData: _sessionToken!.VisitorData,
                    poToken: _sessionToken!.PoToken,
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
                if (!string.IsNullOrEmpty(_youTubeIndexer.Settings.CookiePath))
                {
                    try
                    {
                        foreach (Cookie cookie in CookieManager.ParseCookieFile(_youTubeIndexer.Settings.CookiePath))
                            request.Cookies[cookie.Name] = cookie.Value;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, $"Failed to load cookies from {_youTubeIndexer.Settings.CookiePath}");
                    }
                }
                request.SetContent(jsonPayload);
                _logger.Trace($"Created YouTube Music API request for query: '{searchQuery}', category: {category}");

                yield return new IndexerRequest(request);
            }
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