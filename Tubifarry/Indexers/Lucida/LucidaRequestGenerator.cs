using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace Tubifarry.Indexers.Lucida
{
    public interface ILucidaRequestGenerator : IIndexerRequestGenerator { }

    /// <summary>
    /// Generates Lucida search requests with tiering and service checks
    /// </summary>
    public class LucidaRequestGenerator : ILucidaRequestGenerator
    {
        private readonly LucidaIndexerSettings _settings;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public LucidaRequestGenerator(LucidaIndexerSettings settings, IHttpClient httpClient, Logger logger)
            => (_settings, _httpClient, _logger) = (settings, httpClient, logger);

        public IndexerPageableRequestChain GetRecentRequests() => new();

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
            => Generate(
                query: string.Join(' ', new[] { searchCriteria.AlbumQuery, searchCriteria.ArtistQuery }.Where(s => !string.IsNullOrWhiteSpace(s))),
                isSingle: searchCriteria.Albums?.FirstOrDefault()?.AlbumReleases?.Value?.Min(r => r.TrackCount) == 1
            );

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
            => Generate(searchCriteria.ArtistQuery, false);

        private IndexerPageableRequestChain Generate(string query, bool isSingle)
        {
            IndexerPageableRequestChain chain = new();
            if (string.IsNullOrWhiteSpace(query))
            {
                _logger.Warn("Empty query, skipping search request");
                return chain;
            }

            string baseUrl = _settings.BaseUrl.TrimEnd('/');
            Dictionary<string, List<ServiceCountry>> services = LucidaServiceHelper.GetServicesAsync(baseUrl, _httpClient, _logger)
                             .GetAwaiter().GetResult();
            if (!services.Any())
            {
                _logger.Warn("No services available");
                return chain;
            }

            HashSet<string> userCountries = _settings.CountryCode
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
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
                    .DefaultIfEmpty(countries.First())
                    .Take(2);

                foreach (ServiceCountry? country in picks)
                {
                    string url = $"{baseUrl}/search?query={Uri.EscapeDataString(query)}&service={service}&country={country.Code}";
                    _logger.Debug("Adding tier: {Url}", url);

                    HttpRequest req = new(url)
                    {
                        RequestTimeout = TimeSpan.FromSeconds(_settings.RequestTimeout)
                    };
                    req.Headers["User-Agent"] = LucidaIndexer.UserAgent;
                    req.ContentSummary = new LucidaRequestData(service, _settings.BaseUrl, country.Code, isSingle).ToJson();

                    chain.AddTier(new[] { new IndexerRequest(req) });
                }
            }

            return chain;
        }
    }
}