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
    public class DABMusicRequestGenerator(Logger logger, IDABMusicSessionManager sessionManager) : IDABMusicRequestGenerator
    {
        private readonly Logger _logger = logger;
        private readonly IDABMusicSessionManager _sessionManager = sessionManager;
        private DABMusicIndexerSettings? _settings;

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
            chain.Add([CreateRequest(url, baseUrl, "album")]);

            if (isSingle)
            {
                string fallbackUrl = $"{baseUrl}/api/search?q={Uri.EscapeDataString(query)}&type=all&limit={_settings.SearchLimit}";
                _logger.Trace("Adding fallback search request: {Url}", fallbackUrl);
                chain.AddTier([CreateRequest(fallbackUrl, baseUrl, "all")]);
            }
            return chain;
        }

        private IndexerRequest CreateRequest(string url, string baseUrl, string searchType)
        {
            HttpRequest req = new(url)
            {
                RequestTimeout = TimeSpan.FromSeconds(_settings!.RequestTimeout),
                ContentSummary = new DABMusicRequestData(baseUrl, searchType, _settings.SearchLimit).ToJson(),
                // FlareSolverr interceptor will handle protection challenges
                SuppressHttpError = false,
                LogHttpError = true
            };
            req.Headers["User-Agent"] = Tubifarry.UserAgent;

            DABMusicSession? session = _sessionManager.GetOrCreateSession(baseUrl, _settings.Email, _settings.Password);

            if (session?.IsValid == true)
            {
                req.Headers["Cookie"] = session.SessionCookie;
                _logger.Trace($"Added session cookie to request for {session.Email}");
            }
            else
            {
                _logger.Warn("No valid session available for request");
            }

            return new IndexerRequest(req);
        }
    }
}
