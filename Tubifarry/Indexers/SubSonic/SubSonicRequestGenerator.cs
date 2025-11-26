using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using System.Text;

namespace Tubifarry.Indexers.SubSonic
{
    public interface ISubSonicRequestGenerator : IIndexerRequestGenerator
    {
        void SetSetting(SubSonicIndexerSettings settings);
    }

    public class SubSonicRequestGenerator(Logger logger) : ISubSonicRequestGenerator
    {
        private readonly Logger _logger = logger;
        private SubSonicIndexerSettings? _settings;

        public IndexerPageableRequestChain GetRecentRequests() => new();

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            string query = string.Join(' ', new[] { searchCriteria.AlbumQuery, searchCriteria.ArtistQuery }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            bool isSingle = searchCriteria.Albums?.FirstOrDefault()?.AlbumReleases?.Value?.Min(r => r.TrackCount) == 1;
            return Generate(query, isSingle);
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            return Generate(searchCriteria.ArtistQuery, false);
        }

        public void SetSetting(SubSonicIndexerSettings settings) => _settings = settings;

        private IndexerPageableRequestChain Generate(string query, bool isSingle)
        {
            IndexerPageableRequestChain chain = new();

            if (string.IsNullOrWhiteSpace(query))
            {
                _logger.Warn("Empty query, skipping search request");
                return chain;
            }

            if (_settings == null)
            {
                _logger.Error("Settings not initialized");
                return chain;
            }

            string baseUrl = _settings.BaseUrl.TrimEnd('/');

            try
            {
                string searchUrl = BuildSearch3Url(baseUrl, query, isSingle);
                _logger.Trace($"Searching SubSonic: {searchUrl}");
                IndexerRequest searchRequest = CreateRequest(searchUrl, isSingle ? "search3_with_songs" : "search3");
                chain.Add([searchRequest]);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error generating SubSonic requests");
            }

            return chain;
        }

        private string BuildSearch3Url(string baseUrl, string query, bool isSingle)
        {
            StringBuilder urlBuilder = new($"{baseUrl}/rest/search3.view");
            urlBuilder.Append($"?query={Uri.EscapeDataString(query)}");
            SubSonicAuthHelper.AppendAuthParameters(urlBuilder, _settings!.Username, _settings.Password, _settings.UseTokenAuth);
            urlBuilder.Append($"&artistCount=0");
            urlBuilder.Append($"&albumCount={_settings!.SearchLimit}");
            urlBuilder.Append($"&songCount={(isSingle ? _settings.SearchLimit : 0)}");
            urlBuilder.Append("&f=json");
            return urlBuilder.ToString();
        }

        private IndexerRequest CreateRequest(string url, string contentType)
        {
            HttpRequest req = new(url)
            {
                RequestTimeout = TimeSpan.FromSeconds(_settings!.RequestTimeout),
                ContentSummary = contentType,
                SuppressHttpError = false,
                LogHttpError = true
            };

            req.Headers["User-Agent"] = Tubifarry.UserAgent;
            return new IndexerRequest(req);
        }
    }
}