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
        private readonly IDABMusicSessionManager _sessionManager;

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
            IDABMusicSessionManager sessionManager,
            IHttpClient httpClient,
            IIndexerStatusService statusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, statusService, configService, parsingService, logger)
        {
            _requestGenerator = requestGenerator;
            _parser = parser;
            _sessionManager = sessionManager;
        }

        protected override async Task Test(List<ValidationFailure> failures)
        {
            try
            {
                DABMusicSession? session = _sessionManager.GetOrCreateSession(Settings.BaseUrl.Trim(), Settings.Email, Settings.Password, true);

                if (session == null)
                {
                    failures.Add(new ValidationFailure("Email", "Failed to authenticate with DABMusic. Check your email and password."));
                    return;
                }

                _logger.Debug($"Successfully authenticated with DABMusic as {Settings.Email}");
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