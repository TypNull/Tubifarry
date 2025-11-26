using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace Tubifarry.Indexers.Lucida
{
    public interface ILucidaRequestGenerator : IIndexerRequestGenerator
    {
        public void SetSetting(LucidaIndexerSettings settings);
    }

    /// <summary>
    /// Generates Lucida search requests with tiering and service checks
    /// </summary>
    public class LucidaRequestGenerator : ILucidaRequestGenerator
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private LucidaIndexerSettings? _settings;

        public LucidaRequestGenerator(IHttpClient httpClient, Logger logger) => (_httpClient, _logger) = (httpClient, logger);

        public IndexerPageableRequestChain GetRecentRequests() => new();

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria) => Generate(
                query: string.Join(' ', new[] { searchCriteria.AlbumQuery, searchCriteria.ArtistQuery }.Where(s => !string.IsNullOrWhiteSpace(s))),
                isSingle: searchCriteria.Albums?.FirstOrDefault()?.AlbumReleases?.Value?.Min(r => r.TrackCount) == 1);

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria) => Generate(searchCriteria.ArtistQuery, false);

        public void SetSetting(LucidaIndexerSettings settings) => _settings = settings;

        private IndexerPageableRequestChain Generate(string query, bool isSingle)
        {
            IndexerPageableRequestChain chain = new();
            if (string.IsNullOrWhiteSpace(query))
            {
                _logger.Warn("Empty query, skipping search request");
                return chain;
            }

            string baseUrl = _settings!.BaseUrl.TrimEnd('/');
            Dictionary<string, List<ServiceCountry>> services = LucidaServiceHelper.GetServicesAsync(baseUrl, _httpClient, _logger)
                             .GetAwaiter().GetResult();
            if (services.Count == 0)
            {
                _logger.Warn("No services available");
                return chain;
            }

            HashSet<string> userCountries = _settings.CountryCode
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim().ToUpperInvariant())
                .ToHashSet();

            Dictionary<string, string> displayToKey = LucidaServiceHelper
                .ServiceQualityMap.Keys.ToDictionary(k => LucidaServiceHelper.GetServiceDisplayName(k), StringComparer.OrdinalIgnoreCase);

            IOrderedEnumerable<(string Service, int Priority)> prioritized = _settings.ServicePriorities
                .Select(kv => (DisplayName: kv.Key, Priority: int.TryParse(kv.Value, out int p) ? p : int.MaxValue))
                .Where(x => displayToKey.TryGetValue(x.DisplayName, out _))
                .Select(x => (Service: displayToKey[x.DisplayName], x.Priority))
                .OrderBy(x => x.Priority);

            foreach ((string service, int _) in prioritized)
            {
                if (!services.TryGetValue(service, out List<ServiceCountry>? countries) || countries.Count == 0) continue;

                IEnumerable<ServiceCountry> picks = countries
                    .Where(c => userCountries.Contains(c.Code))
                    .DefaultIfEmpty(countries[0])
                    .Take(2);

                foreach (ServiceCountry? country in picks)
                {
                    string url = $"{baseUrl}/search?query={Uri.EscapeDataString(query)}&service={service}&country={country.Code}";
                    _logger.Trace("Adding tier: {Url}", url);

                    HttpRequest req = new(url)
                    {
                        RequestTimeout = TimeSpan.FromSeconds(_settings.RequestTimeout),
                        ContentSummary = new LucidaRequestData(service, _settings.BaseUrl, country.Code, isSingle).ToJson()
                    };
                    req.Headers["User-Agent"] = Tubifarry.UserAgent;

                    chain.AddTier([new IndexerRequest(req)]);
                }
            }

            return chain;
        }
    }
}