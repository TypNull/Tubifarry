using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;
using Requests;
using Tubifarry.Indexers.Spotify;

namespace Tubifarry.Indexers.Youtube
{
    internal class YoutubeIndexer : HttpIndexerBase<SpotifyIndexerSettings>
    {
        public override string Name => "Youtube";
        public override string Protocol => nameof(YoutubeDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 50;
        public override TimeSpan RateLimit => new(30);

        private readonly IYoutubeRequestGenerator _indexerRequestGenerator;

        private readonly IYoutubeParser _parseIndexerResponse;

        public override ProviderMessage Message => new(
            "YouTube frequently blocks downloads to prevent unauthorized access. To confirm you're not a bot, you may need to provide additional verification. " +
            "This issue can often be partially resolved by using a `cookies.txt` file containing your login tokens. " +
            "Ensure the file is properly formatted and includes valid session data to bypass restrictions. " +
            "Note: YouTube does not always provide the best metadata for tracks, so you may need to manually verify or update track information.",
            ProviderMessageType.Warning
        );

        public YoutubeIndexer(IYoutubeParser parser, IYoutubeRequestGenerator generator, IHttpClient httpClient, IIndexerStatusService indexerStatusService, IConfigService configService, IParsingService parsingService, Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _parseIndexerResponse = parser;
            _indexerRequestGenerator = generator;

            RequestHandler.MainRequestHandlers[0].MaxParallelism = 1;
        }

        protected override Task Test(List<ValidationFailure> failures)
        {
            _parseIndexerResponse.SetCookies(Settings.CookiePath);
            _indexerRequestGenerator.SetCookies(Settings.CookiePath);
            return Task.CompletedTask;
        }

        public override IIndexerRequestGenerator GetRequestGenerator() => _indexerRequestGenerator;

        public override IParseIndexerResponse GetParser() => _parseIndexerResponse;
    }
}