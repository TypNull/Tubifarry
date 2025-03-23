using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using Requests;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Spotify
{
    internal class TubifarryIndexer : HttpIndexerBase<SpotifyIndexerSettings>
    {
        public override string Name => "Tubifarry";
        public override string Protocol => nameof(YoutubeDownloadProtocol);
        public override bool SupportsRss => true;
        public override bool SupportsSearch => true;
        public override int PageSize => 50;
        public override TimeSpan RateLimit => new(3);

        private readonly ISpotifyRequestGenerator _indexerRequestGenerator;
        private readonly ISpotifyToYoutubeParser _parseIndexerResponse;

        public TubifarryIndexer(ISpotifyToYoutubeParser parser, ISpotifyRequestGenerator generator, IHttpClient httpClient, IIndexerStatusService indexerStatusService, IConfigService configService, IParsingService parsingService, Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _parseIndexerResponse = parser;
            _indexerRequestGenerator = generator;
            if (generator.TokenIsExpired())
                generator.StartTokenRequest();

            RequestHandler.MainRequestHandlers[0].MaxParallelism = 1;
        }

        protected override async Task Test(List<ValidationFailure> failures)
        {
            if (!string.IsNullOrEmpty(Settings.TrustedSessionGeneratorUrl))
            {
                try
                {
                    TrustedSessionHelper.ValidateAuthenticationSettingsAsync(Settings.TrustedSessionGeneratorUrl, Settings.PoToken, Settings.VisitorData, Settings.CookiePath).Wait();

                    (string? poToken, string? visitorData) = await TrustedSessionHelper.GetTrustedSessionTokensAsync(Settings.TrustedSessionGeneratorUrl, forceRefresh: true);

                    if (!string.IsNullOrEmpty(poToken) && !string.IsNullOrEmpty(visitorData))
                    {
                        Settings.PoToken = poToken;
                        Settings.VisitorData = visitorData;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(new ValidationFailure("TrustedSessionGeneratorUrl", $"Error connecting to the trusted session generator: {ex.Message}"));
                }
            }

            _parseIndexerResponse.SetAuth(Settings);
            failures.AddIfNotNull(await TestConnection());
        }

        public override IIndexerRequestGenerator GetRequestGenerator() => _indexerRequestGenerator;

        public override IParseIndexerResponse GetParser() => _parseIndexerResponse;
    }
}