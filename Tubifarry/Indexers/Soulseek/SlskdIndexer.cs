using FluentValidation.Results;
using Newtonsoft.Json;
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
            _parseIndexerResponse = new SlskdParser(this, httpClient);
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
                    .SetHeader("X-API-KEY", Settings.ApiKey).Build();
                request.AllowAutoRedirect = true;
                request.RequestTimeout = TimeSpan.FromSeconds(30);

                HttpResponse response = await _httpClient.ExecuteAsync(request);
                _logger.Debug($"TestConnection Response: {response.Content}");

                if (response.StatusCode != HttpStatusCode.OK)
                    return new ValidationFailure("BaseUrl", $"Unable to connect to Slskd. Status: {response.StatusCode}");

                dynamic? jsonResponse = JsonConvert.DeserializeObject<dynamic>(response.Content);
                if (jsonResponse == null)
                    return new ValidationFailure("BaseUrl", "Failed to parse Slskd response.");

                string? serverState = jsonResponse?.server?.state?.ToString();
                if (string.IsNullOrEmpty(serverState) || !serverState.Contains("Connected"))
                    return new ValidationFailure("BaseUrl", $"Slskd server is not connected. State: {serverState}");

                return null!;
            }
            catch (HttpException ex)
            {
                _logger.Warn(ex, "Unable to connect to Slskd.");
                return new ValidationFailure("BaseUrl", $"Unable to connect to Slskd: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unexpected error while testing Slskd connection.");
                return new ValidationFailure(string.Empty, $"Unexpected error: {ex.Message}");
            }
        }
    }
}
