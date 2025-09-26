using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace Tubifarry.Indexers.DABMusic
{
    public interface IDABMusicRequestGenerator : IIndexerRequestGenerator
    {
        public void SetSetting(DABMusicIndexerSettings settings);
    }

    /// <summary>
    /// Generates DABMusic search requests
    /// </summary>
    public class DABMusicRequestGenerator : IDABMusicRequestGenerator
    {
        private readonly Logger _logger;
        private DABMusicIndexerSettings? _settings;

        public DABMusicRequestGenerator(Logger logger) => _logger = logger;

        public IndexerPageableRequestChain GetRecentRequests() => new();

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            string query = string.Join(' ', new[] { searchCriteria.AlbumQuery, searchCriteria.ArtistQuery }.Where(s => !string.IsNullOrWhiteSpace(s)));
            bool isSingle = searchCriteria.Albums?.FirstOrDefault()?.AlbumReleases?.Value?.Min(r => r.TrackCount) == 1;
            return Generate(query, isSingle);
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria) => Generate(searchCriteria.ArtistQuery, false);

        public void SetSetting(DABMusicIndexerSettings settings) => _settings = settings;

        private IndexerPageableRequestChain Generate(string query, bool isSingle)
        {
            IndexerPageableRequestChain chain = new();
            if (string.IsNullOrWhiteSpace(query))
            {
                _logger.Warn("Empty query, skipping search request");
                return chain;
            }

            string baseUrl = _settings!.BaseUrl.TrimEnd('/');

            string url = $"{baseUrl}/api/search?q={Uri.EscapeDataString(query)}&type=album&limit={_settings.SearchLimit}";
            _logger.Trace("Creating DABMusic search request: {Url}", url);
            chain.Add(new[] { CreateRequest(url, baseUrl, "album") });

            if (isSingle)
            {
                string fallbackUrl = $"{baseUrl}/api/search?q={Uri.EscapeDataString(query)}&type=all&limit={_settings.SearchLimit}";
                _logger.Trace("Adding fallback search request: {Url}", fallbackUrl);
                chain.AddTier(new[] { CreateRequest(fallbackUrl, baseUrl, "all") });
            }
            return chain;
        }

        private IndexerRequest CreateRequest(string url, string baseUrl, string searchType)
        {
            HttpRequest req = new(url)
            {
                RequestTimeout = TimeSpan.FromSeconds(_settings!.RequestTimeout),
                ContentSummary = new DABMusicRequestData(baseUrl, searchType, _settings.SearchLimit).ToJson()
            };
            req.Headers["User-Agent"] = Tubifarry.UserAgent;
            return new IndexerRequest(req);
        }
    }
}