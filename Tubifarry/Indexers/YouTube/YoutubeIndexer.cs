using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;
using Tubifarry.Core.Records;
using Tubifarry.Core.Replacements;
using Tubifarry.Core.Utilities;
using Tubifarry.Download.Clients.YouTube;

namespace Tubifarry.Indexers.YouTube
{
    internal class YouTubeIndexer : ExtendedHttpIndexerBase<YouTubeIndexerSettings, LazyIndexerPageableRequest>
    {
        public override string Name => "Youtube";
        public override string Protocol => nameof(YoutubeDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 50;

        public new YouTubeIndexerSettings Settings => base.Settings;

        public override TimeSpan RateLimit => TimeSpan.FromSeconds(2);

        private readonly YouTubeRequestGenerator _requestGenerator;
        private readonly YouTubeParser _parser;

        public override ProviderMessage Message => new(
            "YouTube frequently blocks downloads to prevent unauthorized access. To confirm you're not a bot, you may need to provide additional verification. " +
            "This issue can often be partially resolved by using a `cookies.txt` file containing your login tokens. " +
            "Ensure the file is properly formatted and includes valid session data to bypass restrictions. " +
            "Note: YouTube does not always provide the best metadata for tracks, so you may need to manually verify or update track information.",
            ProviderMessageType.Warning
        );

        public YouTubeIndexer(IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _parser = new(this);
            _requestGenerator = new(this);
        }

        protected override async Task Test(List<ValidationFailure> failures)
        {
            try
            {
                await TrustedSessionHelper.ValidateAuthenticationSettingsAsync(Settings.TrustedSessionGeneratorUrl, Settings.CookiePath);
                SessionTokens session = await TrustedSessionHelper.GetTrustedSessionTokensAsync(Settings.TrustedSessionGeneratorUrl, true);
                if (!session.IsValid && !session.IsEmpty)
                    failures.Add(new ValidationFailure("TrustedSessionGeneratorUrl", "Failed to retrieve valid tokens from the session generator service"));
            }
            catch (Exception ex)
            {
                failures.Add(new ValidationFailure("TrustedSessionGeneratorUrl", $"Failed to valiate session generator service: {ex.Message}"));
            }
        }

        public override IIndexerRequestGenerator<LazyIndexerPageableRequest> GetExtendedRequestGenerator() => _requestGenerator;

        public override IParseIndexerResponse GetParser() => _parser;
    }
}