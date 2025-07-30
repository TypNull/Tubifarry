using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.YouTube
{
    internal class YouTubeIndexer : ExtendedHttpIndexerBase<YouTubeIndexerSettings, ExtendedIndexerPageableRequest>
    {
        public override string Name => "Youtube";
        public override string Protocol => nameof(YoutubeDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 50;
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(2);

        private readonly IYouTubeRequestGenerator _requestGenerator;
        private readonly IYouTubeParser _parser;

        public override ProviderMessage Message => new(
            "YouTube frequently blocks downloads to prevent unauthorized access. To confirm you're not a bot, you may need to provide additional verification. " +
            "This issue can often be partially resolved by using a `cookies.txt` file containing your login tokens. " +
            "Ensure the file is properly formatted and includes valid session data to bypass restrictions. " +
            "Note: YouTube does not always provide the best metadata for tracks, so you may need to manually verify or update track information.",
            ProviderMessageType.Warning
        );

        public YouTubeIndexer(
            IYouTubeParser parser,
            IYouTubeRequestGenerator generator,
            IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _parser = parser;
            _requestGenerator = generator;
        }

        protected override async Task Test(List<ValidationFailure> failures)
        {
            if (!string.IsNullOrEmpty(Settings.TrustedSessionGeneratorUrl))
            {
                try
                {
                    (string? poToken, string? visitorData) = await TrustedSessionHelper.GetTrustedSessionTokensAsync(
                        Settings.TrustedSessionGeneratorUrl, forceRefresh: true);

                    if (!string.IsNullOrEmpty(poToken) && !string.IsNullOrEmpty(visitorData))
                    {
                        _requestGenerator.SetTrustedSessionData(poToken, visitorData);
                        Settings.PoToken = poToken;
                        Settings.VisitorData = visitorData;
                    }
                    else
                        failures.Add(new ValidationFailure("TrustedSessionGeneratorUrl", "Failed to retrieve valid tokens from the session generator service"));
                }
                catch (Exception ex)
                {
                    failures.Add(new ValidationFailure("TrustedSessionGeneratorUrl", $"Failed to contact session generator service: {ex.Message}"));
                }
            }
            else if (!string.IsNullOrEmpty(Settings.PoToken) && !string.IsNullOrEmpty(Settings.VisitorData))
            {
                _requestGenerator.SetTrustedSessionData(Settings.PoToken, Settings.VisitorData);
                _logger.Debug("Using manually provided tokens");
            }

            _requestGenerator.SetCookies(Settings.CookiePath);
            _parser.SetAuth(Settings);
        }

        public override IIndexerRequestGenerator<ExtendedIndexerPageableRequest> GetExtendedRequestGenerator() => _requestGenerator;

        public override IParseIndexerResponse GetParser() => _parser;
    }
}