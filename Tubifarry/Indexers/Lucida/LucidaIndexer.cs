using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;
using System.Text.RegularExpressions;

namespace Tubifarry.Indexers.Lucida
{
    public partial class LucidaIndexer : HttpIndexerBase<LucidaIndexerSettings>
    {
        private readonly ILucidaRequestGenerator _requestGenerator;
        private readonly ILucidaParser _parser;

        public override string Name => "Lucida";
        public override string Protocol => nameof(LucidaDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 100;
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(2);

        public override ProviderMessage Message => new(
            "Lucida is an interface for searching music across streaming services. " +
            "Configure your service priorities and countries in settings.",
            ProviderMessageType.Info);

        public LucidaIndexer(
            ILucidaRequestGenerator requestGenerator,
            ILucidaParser parser,
            IHttpClient httpClient,
            IIndexerStatusService statusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, statusService, configService, parsingService, logger)
        {
            _requestGenerator = requestGenerator;
            _parser = parser;
        }

        protected override async Task Test(List<ValidationFailure> failures)
        {
            string? baseUrl = Settings.BaseUrl?.Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                failures.Add(new ValidationFailure("BaseUrl", "Base URL is required"));
                return;
            }

            try
            {
                HttpRequest req = new(baseUrl);
                req.Headers["User-Agent"] = Tubifarry.UserAgent;
                HttpResponse response = await _httpClient.ExecuteAsync(req);
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    failures.Add(new ValidationFailure("BaseUrl", $"Cannot connect to Lucida instance: HTTP {(int)response.StatusCode}"));
                    return;
                }

                if (!response.Content.Contains("Lucida") && !LucidaHeaderRegex().IsMatch(response.Content))
                {
                    failures.Add(new ValidationFailure("BaseUrl", "The provided URL does not appear to be a Lucida instance"));
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error connecting to Lucida instance");
                failures.Add(new ValidationFailure("BaseUrl", ex.Message));
                return;
            }

            Dictionary<string, List<ServiceCountry>> services;
            try
            {
                if (LucidaServiceHelper.IsFetchCompleted(baseUrl) && !LucidaServiceHelper.HasAvailableServices(baseUrl))
                    LucidaServiceHelper.ClearCache(baseUrl);
                services = await LucidaServiceHelper.GetServicesAsync(baseUrl, _httpClient, _logger);
                if (services.Count == 0)
                {
                    failures.Add(new ValidationFailure("BaseUrl", "No services available from Lucida instance"));
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error fetching services");
                failures.Add(new ValidationFailure("BaseUrl", ex.Message));
                return;
            }

            List<string> codes = Settings.CountryCode?
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim().ToUpperInvariant())
                .ToList() ?? [];

            if (codes.Count == 0)
            {
                failures.Add(new ValidationFailure("CountryCode", "At least one country code is required"));
            }
            else
            {
                foreach (string? code in codes)
                {
                    if (!services.Values.Where(x => x != null).SelectMany(x => x).Any(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)))
                        failures.Add(new ValidationFailure("CountryCode", $"Country code '{code}' is not valid for any service"));
                }
            }
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            _requestGenerator.SetSetting(Settings);
            return _requestGenerator;
        }

        public override IParseIndexerResponse GetParser() => _parser;

        protected override async Task<IndexerResponse> FetchIndexerResponse(IndexerRequest request)
        {
            IndexerResponse response = await base.FetchIndexerResponse(request);
            for (int attempt = 1; attempt < LucidaRetryPolicy.MaxAttempts && InvalidCountryRegex().IsMatch(response.Content); attempt++)
            {
                _logger.Debug("Lucida server rejected the country, retrying ({0}/{1})", attempt, LucidaRetryPolicy.MaxAttempts - 1);
                await LucidaRetryPolicy.DelayAsync(attempt);
                response = await base.FetchIndexerResponse(request);
            }
            return response;
        }

        [GeneratedRegex(@"success:\s*false,\s*error:\s*""Invalid country")]
        private static partial Regex InvalidCountryRegex();

        [GeneratedRegex("<title>.*?(Lucida|Music).*?</title>", RegexOptions.IgnoreCase, "de-DE")]
        private static partial Regex LucidaHeaderRegex();
    }
}