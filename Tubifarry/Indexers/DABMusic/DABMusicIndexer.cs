using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;

namespace Tubifarry.Indexers.DABMusic
{
    public class DABMusicIndexer : HttpIndexerBase<DABMusicIndexerSettings>
    {
        private readonly IDABMusicRequestGenerator _requestGenerator;
        private readonly IDABMusicParser _parser;

        public override string Name => "DABMusic";
        public override string Protocol => "QobuzDownloadProtocol";
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 50;
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(1);

        public override ProviderMessage Message => new("DABMusic provides high-quality music downloads from qobuz.", ProviderMessageType.Info);

        public DABMusicIndexer(
            IDABMusicRequestGenerator requestGenerator,
            IDABMusicParser parser,
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
                    failures.Add(new ValidationFailure("BaseUrl", $"Cannot connect to DABMusic: {(int)response.StatusCode}"));
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error connecting to DABMusic API");
                failures.Add(new ValidationFailure("BaseUrl", ex.Message));
                return;
            }
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            _requestGenerator.SetSetting(Settings);
            return _requestGenerator;
        }

        public override IParseIndexerResponse GetParser() => _parser;
    }
}