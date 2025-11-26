using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;
using System.Net;
using Tubifarry.Core.Utilities;

namespace Tubifarry.ImportLists.ArrStack
{
    /// <summary>
    /// Import list that discovers soundtracks from Arr applications (Radarr/Sonarr) using MusicBrainz.
    /// </summary>
    public class ArrSoundtrackImport : HttpImportListBase<ArrSoundtrackImportSettings>
    {
        public override string Name => "Arr-Soundtracks";

        public override ProviderMessage Message => new(
            "MusicBrainz enforces strict rate limiting (1 request/second). " +
            "Large libraries may take considerable time to process. " +
            "Approximately 75% of requests succeed due to MusicBrainz's rate limiting policy. " +
            "See: https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting",
            ProviderMessageType.Warning
        );

        public override ImportListType ListType => ImportListType.Program;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours((Definition?.Settings as ArrSoundtrackImportSettings)?.RefreshInterval ?? 12);

        public override int PageSize => 0;
        private ArrSoundtrackRequestGenerator? _generator;
        private ArrSoundtrackImportParser? _parser;

        public ArrSoundtrackImport(IHttpClient httpClient, IImportListStatusService importListStatusService,
            IConfigService configService, IParsingService parsingService, Logger logger)
            : base(httpClient, importListStatusService, configService, parsingService, logger) { }

        public override IImportListRequestGenerator GetRequestGenerator() => _generator ??= new ArrSoundtrackRequestGenerator(Settings);

        public override IParseImportListResponse GetParser() => _parser ??= new ArrSoundtrackImportParser(Settings, _httpClient);

        protected override void Test(List<ValidationFailure> failures)
        {
            ValidationFailure? connectionFailure = TestArrConnection();
            failures!.AddIfNotNull(connectionFailure);
            ValidationFailure? cacheFailure = TestCacheDirectory();
            failures!.AddIfNotNull(cacheFailure);
        }

        private ValidationFailure? TestArrConnection()
        {
            try
            {
                _logger.Trace("Testing connection to Arr application at: {0}", Settings.BaseUrl);
                HttpRequest request = new HttpRequestBuilder(Settings.BaseUrl + Settings.APIStatusEndpoint)
                    .AddQueryParam("apikey", Settings.ApiKey)
                    .Build();

                request.AllowAutoRedirect = true;
                request.RequestTimeout = TimeSpan.FromSeconds(30);

                HttpResponse response = _httpClient.Get(request);

                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        return null;

                    case HttpStatusCode.Unauthorized:
                        return new ValidationFailure("ApiKey", "Invalid API key");

                    case HttpStatusCode.NotFound:
                        return new ValidationFailure("BaseUrl", "Endpoint not found. Verify URL and API paths");

                    default:
                        _logger.Warn("Arr application returned unexpected status: {0}. Response: {1}",
                            response.StatusCode, response.Content[..200]);
                        return new ValidationFailure("BaseUrl", $"Connection failed with status: {response.StatusCode}");
                }
            }
            catch (HttpException ex)
            {
                return new ValidationFailure("BaseUrl", $"Connection failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unexpected error during connection test");
                return new ValidationFailure(string.Empty, $"Unexpected error: {ex.Message}");
            }
        }

        private ValidationFailure? TestCacheDirectory()
        {
            try
            {
                _logger.Trace("Testing cache directory: {0}", Settings.CacheDirectory);
                ValidationFailure? existenceFailure = PermissionTester.TestExistance(Settings.CacheDirectory, _logger);
                return existenceFailure ?? PermissionTester.TestReadWritePermissions(Settings.CacheDirectory, _logger);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error testing cache directory");
                return new ValidationFailure("CacheDirectory", $"Cache directory test failed: {ex.Message}");
            }
        }

        public override IEnumerable<ProviderDefinition> DefaultDefinitions
        {
            get
            {
                yield return GetDefinition("Radarr", GetSettings(
                    "http://localhost:7878",
                    "/api/v3/system/status",
                    "/api/v3/movie"));

                yield return GetDefinition("Sonarr", GetSettings(
                    "http://localhost:8989",
                    "/api/v3/system/status",
                    "/api/v3/series"));
            }
        }

        private ImportListDefinition GetDefinition(string name, ArrSoundtrackImportSettings settings) => new()
        {
            EnableAutomaticAdd = false,
            Name = $"{name} Soundtracks",
            Implementation = GetType().Name,
            Settings = settings
        };

        private static ArrSoundtrackImportSettings GetSettings(string baseUrl, string apiStatusEndpoint, string apiItemEndpoint) => new()
        {
            BaseUrl = baseUrl,
            APIItemEndpoint = apiItemEndpoint,
            APIStatusEndpoint = apiStatusEndpoint
        };
    }
}