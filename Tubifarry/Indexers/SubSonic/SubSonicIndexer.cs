using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;
using System.Text.Json;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.SubSonic
{
    public class SubSonicIndexer : HttpIndexerBase<SubSonicIndexerSettings>
    {
        private readonly ISubSonicRequestGenerator _requestGenerator;
        private readonly ISubSonicParser _parser;

        public override string Name => "SubSonic";
        public override string Protocol => nameof(SubSonicDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 50;
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(1);

        public override ProviderMessage Message => new(
            "SubSonic provides access to your personal music server supporting Subsonic API.",
            ProviderMessageType.Info);

        public SubSonicIndexer(
            ISubSonicRequestGenerator requestGenerator,
            ISubSonicParser parser,
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
            try
            {
                // Test connection using ping endpoint
                string baseUrl = Settings.BaseUrl.TrimEnd('/');
                var urlBuilder = new System.Text.StringBuilder($"{baseUrl}/rest/ping.view");

                // Add authentication parameters
                urlBuilder.Append($"?u={Uri.EscapeDataString(Settings.Username)}");
                urlBuilder.Append($"&v={Uri.EscapeDataString(SubSonicAuthHelper.ApiVersion)}");
                urlBuilder.Append($"&c={Uri.EscapeDataString(SubSonicAuthHelper.ClientName)}");
                urlBuilder.Append("&f=json");

                if (Settings.UseTokenAuth)
                {
                    (string salt, string token) = SubSonicAuthHelper.GenerateToken(Settings.Password);
                    urlBuilder.Append($"&t={token}");
                    urlBuilder.Append($"&s={salt}");
                }
                else
                {
                    urlBuilder.Append($"&p={Uri.EscapeDataString(Settings.Password)}");
                }

                string testUrl = urlBuilder.ToString();

                var request = new HttpRequest(testUrl)
                {
                    RequestTimeout = TimeSpan.FromSeconds(Settings.RequestTimeout)
                };
                request.Headers["User-Agent"] = Tubifarry.UserAgent;

                _logger.Trace("Testing SubSonic connection to: {BaseUrl}", Settings.BaseUrl);

                var response = await _httpClient.ExecuteAsync(request);

                if (!response.HasHttpError)
                {
                    var responseWrapper = JsonSerializer.Deserialize<SubSonicPingResponse>(
                        response.Content,
                        IndexerParserHelper.StandardJsonOptions);

                    if (responseWrapper?.SubsonicResponse != null)
                    {
                        var pingResponse = responseWrapper.SubsonicResponse;

                        if (pingResponse.Status == "ok")
                        {
                            _logger.Debug("Successfully connected to SubSonic server as {Username}", Settings.Username);
                            return;
                        }
                        else if (pingResponse.Error != null)
                        {
                            int errorCode = pingResponse.Error.Code;
                            string errorMsg = pingResponse.Error.Message;

                            if (errorCode == 40 || errorCode == 41) // Authentication errors
                            {
                                failures.Add(new ValidationFailure("Username",
                                    $"Authentication failed: {errorMsg}. Check your username and password."));
                            }
                            else
                            {
                                failures.Add(new ValidationFailure("BaseUrl",
                                    $"SubSonic API error: {errorMsg}"));
                            }
                            return;
                        }
                    }
                }

                failures.Add(new ValidationFailure("BaseUrl",
                    $"Failed to connect to SubSonic server. Check the server URL and credentials."));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error testing SubSonic connection");
                failures.Add(new ValidationFailure("BaseUrl",
                    $"Error connecting to SubSonic: {ex.Message}"));
            }
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            _requestGenerator.SetSetting(Settings);
            return _requestGenerator;
        }

        public override IParseIndexerResponse GetParser()
        {
            _parser.SetSettings(Settings);
            return _parser;
        }
    }
}
