using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace Tubifarry.Indexers.TripleTriple
{
    public interface ITripleTripleRequestGenerator : IIndexerRequestGenerator
    {
        void SetSetting(TripleTripleIndexerSettings settings);
    }

    public class TripleTripleRequestGenerator : ITripleTripleRequestGenerator
    {
        private readonly Logger _logger;
        private TripleTripleIndexerSettings? _settings;

        public TripleTripleRequestGenerator(Logger logger) => _logger = logger;

        public IndexerPageableRequestChain GetRecentRequests() => new();

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            bool isSingle = searchCriteria.Albums?.FirstOrDefault()?.AlbumReleases?.Value?.Min(r => r.TrackCount) == 1;
            return Generate(searchCriteria.ArtistQuery, searchCriteria.AlbumQuery, isSingle);
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria) => Generate(searchCriteria.ArtistQuery, null, false);

        public void SetSetting(TripleTripleIndexerSettings settings) => _settings = settings;

        private IndexerPageableRequestChain Generate(string? artistQuery, string? albumQuery, bool isSingle)
        {
            IndexerPageableRequestChain chain = new();
            string combinedQuery = string.Join(' ', new[] { albumQuery, artistQuery }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(combinedQuery))
            {
                _logger.Warn("Empty query, skipping search request");
                return chain;
            }

            string type = isSingle ? "track" : "album";

            chain.AddTier([CreateRequest(combinedQuery, type, isSingle)]);

            string? fallbackQuery = !string.IsNullOrWhiteSpace(artistQuery) ? artistQuery : albumQuery;
            if (!string.IsNullOrWhiteSpace(fallbackQuery) && !string.Equals(fallbackQuery, combinedQuery, StringComparison.OrdinalIgnoreCase))
                chain.AddTier([CreateRequest(fallbackQuery, type, isSingle)]);

            if (isSingle)
                chain.AddTier([CreateRequest(combinedQuery, "album", true)]);

            return chain;
        }

        private IndexerRequest CreateRequest(string query, string type, bool isSingle)
        {
            string baseUrl = _settings!.BaseUrl.TrimEnd('/');
            string country = ((TripleTripleCountry)_settings.CountryCode).ToString();
            string codec = ((TripleTripleCodec)_settings.Codec).ToString().ToLowerInvariant();

            string url = $"{baseUrl}/api/amazon-music/search?query={Uri.EscapeDataString(query)}&type={type}&country={country}";
            _logger.Trace("Creating TripleTriple search request: {Url}", url);

            HttpRequest req = new(url)
            {
                RequestTimeout = TimeSpan.FromSeconds(_settings.RequestTimeout),
                ContentSummary = new TripleTripleRequestData(baseUrl, country, codec, isSingle).ToJson()
            };
            req.Headers["User-Agent"] = Tubifarry.UserAgent;
            req.Headers["Referer"] = $"{baseUrl}/search/{Uri.EscapeDataString(query)}";

            return new IndexerRequest(req);
        }
    }
}
