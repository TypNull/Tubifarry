using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;
using System.Net;

namespace NzbDrone.Core.Indexers.Soulseek
{
    public class SlskdIndexer : HttpIndexerBase<SlskdSettings>
    {
        public override string Name => "Slsdk";
        public override string Protocol => nameof(SoulseekDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 50;
        public override TimeSpan RateLimit => new(3);

        private readonly IIndexerRequestGenerator _indexerRequestGenerator;

        private readonly IParseIndexerResponse _parseIndexerResponse;

        internal new SlskdSettings Settings => base.Settings;


        public SlskdIndexer(IHttpClient httpClient, IIndexerStatusService indexerStatusService, IConfigService configService, IParsingService parsingService, Logger logger)
          : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _parseIndexerResponse = new SlskdParser();
            _indexerRequestGenerator = new SlskdRequestGenerator(this, httpClient);
        }

        protected override async Task Test(List<ValidationFailure> failures) => failures.AddIfNotNull(await TestConnection());

        public override IIndexerRequestGenerator GetRequestGenerator() => _indexerRequestGenerator;

        public override IParseIndexerResponse GetParser() => _parseIndexerResponse;

        protected override async Task<ValidationFailure> TestConnection()
        {
            try
            {
                HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/application")
                    .SetHeader("X-API-KEY", Settings.ApiKey)
                    .Build();

                request.AllowAutoRedirect = true;
                request.RequestTimeout = TimeSpan.FromSeconds(30);
                HttpResponse response = await _httpClient.GetAsync(request);
                _logger.Info(response.Content.ToString());
                if (response.StatusCode == HttpStatusCode.OK)
                    return null!;

                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return new ValidationFailure("ApiKey", "Invalid API key");
                else

                    return new ValidationFailure("BaseUrl", $"Unable to connect to Slskd. Status: {response.StatusCode}");

            }
            catch (HttpException ex)
            {
                _logger.Warn(ex, "Unable to connect to Slskd");
                return new ValidationFailure("BaseUrl", $"Unable to connect to Slskd: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unexpected error while testing Slskd connection");
                return new ValidationFailure(string.Empty, $"Unexpected error: {ex.Message}");
            }
        }
    }
}
