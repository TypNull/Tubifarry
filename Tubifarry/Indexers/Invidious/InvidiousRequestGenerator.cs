using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace Tubifarry.Indexers.Invidious
{
    public interface IInvidiousRequestGenerator : IIndexerRequestGenerator
    {
        void SetSetting(InvidiousIndexerSettings settings);
    }

    public class InvidiousRequestGenerator(Logger logger) : IInvidiousRequestGenerator
    {
        private InvidiousIndexerSettings? _settings;

        public IndexerPageableRequestChain GetRecentRequests() => new();

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            return Generate(searchCriteria.ArtistQuery, searchCriteria.AlbumQuery);
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria) => Generate(searchCriteria.ArtistQuery, null);

        public void SetSetting(InvidiousIndexerSettings settings) => _settings = settings;

        private IndexerPageableRequestChain Generate(string? artistQuery, string? albumQuery)
        {
            IndexerPageableRequestChain chain = new();
            string combinedQuery = string.Join(' ', new[] { albumQuery, artistQuery }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(combinedQuery))
            {
                logger.Warn("Empty query, skipping Invidious search request");
                return chain;
            }

            chain.AddTier([CreateSearchRequest(combinedQuery, 1), CreateSearchRequest(combinedQuery, 2)]);
            chain.AddTier([CreateSearchRequest(combinedQuery + " album", 1), CreateSearchRequest(combinedQuery + " album", 2)]);

            if (!string.IsNullOrWhiteSpace(artistQuery) && !string.IsNullOrWhiteSpace(albumQuery))
                chain.AddTier([CreateSearchRequest($"{artistQuery} {albumQuery}", 1), CreateSearchRequest($"{artistQuery} {albumQuery}", 2)]);

            return chain;
        }

        private IndexerRequest CreateSearchRequest(string query, int page)
        {
            string baseUrl = _settings!.BaseUrl.TrimEnd('/');
            string url = $"{baseUrl}/api/v1/search?q={Uri.EscapeDataString(query)}&type=album&sort_by=relevance&features=&page={page}";

            logger.Trace("Creating Invidious search request: {Url}", url);

            HttpRequest req = new(url)
            {
                RequestTimeout = TimeSpan.FromSeconds(_settings.RequestTimeout),
                ContentSummary = new InvidiousRequestData(baseUrl, _settings.ProxyVideos).ToJson()
            };
            req.Headers["User-Agent"] = Tubifarry.UserAgent;

            return new IndexerRequest(req);
        }
    }
}
