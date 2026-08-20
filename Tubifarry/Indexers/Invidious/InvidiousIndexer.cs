using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;
using System.Text.Json;
using Tubifarry.Download.Base;

namespace Tubifarry.Indexers.Invidious
{
    public class InvidiousIndexer(
        IInvidiousRequestGenerator requestGenerator,
        IInvidiousParser parser,
        IHttpClient httpClient,
        IIndexerStatusService statusService,
        IConfigService configService,
        IParsingService parsingService,
        IEnumerable<IHttpRequestInterceptor> requestInterceptors,
        Logger logger) : HttpIndexerBase<InvidiousIndexerSettings>(httpClient, statusService, configService, parsingService, logger)
    {
        public override string Name => "Invidious";
        public override string Protocol => nameof(YoutubeDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 20;
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(1);

        public override ProviderMessage Message => new("Invidious provides an alternative frontend to YouTube.", ProviderMessageType.Info);

        protected override async Task Test(List<ValidationFailure> failures)
        {
            try
            {
                BaseHttpClient httpClient = new(Settings.BaseUrl.Trim(), requestInterceptors, TimeSpan.FromSeconds(15));
                string response = await httpClient.GetStringAsync("/api/v1/stats");

                if (string.IsNullOrEmpty(response))
                {
                    failures.Add(new ValidationFailure("BaseUrl", "Cannot connect to Invidious instance: Empty response"));
                    return;
                }

                InvidiousStatsResponse? stats = JsonSerializer.Deserialize<InvidiousStatsResponse>(response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (stats?.Software?.Name == null || !stats.Software.Name.Equals("invidious", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(new ValidationFailure("BaseUrl", "The URL does not appear to be an Invidious instance"));
                    return;
                }

                _logger.Debug($"Successfully connected to Invidious {stats.Software.Version}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error connecting to Invidious API");
                failures.Add(new ValidationFailure("BaseUrl", $"Cannot connect to Invidious instance: {ex.Message}"));
            }
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            requestGenerator.SetSetting(Settings);
            return requestGenerator;
        }

        public override IParseIndexerResponse GetParser() => parser;
    }
}
