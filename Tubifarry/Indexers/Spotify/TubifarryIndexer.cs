using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;
using Tubifarry.Core.Records;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Spotify
{
    internal class TubifarryIndexer : ExtendedHttpIndexerBase<SpotifyIndexerSettings, ExtendedIndexerPageableRequest>
    {
        public override string Name => "Tubifarry";
        public override string Protocol => nameof(YoutubeDownloadProtocol);
        public override bool SupportsRss => true;
        public override bool SupportsSearch => true;
        public override int PageSize => 20;
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(2);

        private readonly ISpotifyRequestGenerator _requestGenerator;
        private readonly ISpotifyParser _parser;

        public override ProviderMessage Message => new(
            "Spotify is used to discover music releases, but actual downloads are provided through YouTube Music. " +
            "This indexer searches Spotify for album information and enriches it with YouTube Music streaming data. " +
            "Ensure you have valid authentication for both services for the best results.",
            ProviderMessageType.Info
        );

        public TubifarryIndexer(
            ISpotifyParser parser,
            ISpotifyRequestGenerator generator,
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
            UpdateComponentSettings();

            if (_requestGenerator.TokenIsExpired())
                _requestGenerator.StartTokenRequest();

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
            UpdateComponentSettings();
        }

        private void UpdateComponentSettings()
        {
            _requestGenerator.UpdateSettings(Settings);
            _parser.UpdateSettings(Settings);
        }

        public override IIndexerRequestGenerator<ExtendedIndexerPageableRequest> GetExtendedRequestGenerator()
        {
            UpdateComponentSettings();
            return _requestGenerator;
        }

        public override IParseIndexerResponse GetParser()
        {
            UpdateComponentSettings();
            return _parser;
        }
    }
}
