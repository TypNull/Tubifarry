using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;
using System.Net;

namespace NzbDrone.Core.ImportLists.ArrStack
{
    public class ArrSoundtrackImport : HttpImportListBase<ArrSoundtrackImportSettings>
    {
        public override string Name => "Arr-Soundtracks";

        public override ProviderMessage Message => new(
            "MusicBrainz only supports 1 request per second, so this import is rate-limited and may take some time depending on the size of your library. " +
            "Additionally, MusicBrainz may not process all requests and only handles approximately 75% of them as per their rate-limiting policy. " +
            "For more details, see: https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting. " +
            "Important: Do not use this feature while updating metadata!",
            ProviderMessageType.Warning
        );

        public override ImportListType ListType => ImportListType.Program;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours((Definition?.Settings as ArrSoundtrackImportSettings)?.RefreshInterval ?? 12);

        public override int PageSize => 0;
        private ArrSoundtrackRequestGenerator? _generator;
        private ArrSoundtrackImportParser? _parser;

        public ArrSoundtrackImport(IHttpClient httpClient, IImportListStatusService importListStatusService, IConfigService configService, IParsingService parsingService, Logger logger) : base(httpClient, importListStatusService, configService, parsingService, logger) { }

        public override IImportListRequestGenerator GetRequestGenerator() => _generator ??= new ArrSoundtrackRequestGenerator(Settings);

        public override IParseImportListResponse GetParser() => _parser ??= new ArrSoundtrackImportParser(Settings, _httpClient);

        protected override void Test(List<ValidationFailure> failures)
        {
            failures!.AddIfNotNull(TestConnection());
            failures!.AddIfNotNull(TestWritePermission());
        }

        private new ValidationFailure? TestConnection()
        {
            try
            {
                HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}{Settings.APIStatusEndpoint}")
                    .AddQueryParam("apikey", Settings.ApiKey)
                    .Build();
                request.AllowAutoRedirect = true;
                request.RequestTimeout = TimeSpan.FromSeconds(30);
                HttpResponse response = _httpClient.Get(request);
                if (response.StatusCode == HttpStatusCode.OK)
                    return null;
                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return new ValidationFailure("ApiKey", "Invalid API key");
                else
                    _logger.Warn($"Arr-App returned status code: {response.StatusCode}. Response: {response.Content}");
                return new ValidationFailure("BaseUrl", $"Unable to connect to Arr-App. Status: {response.StatusCode}");
            }
            catch (HttpException ex)
            {
                _logger.Warn(ex, "Unable to connect to Arr-App");
                return new ValidationFailure("BaseUrl", $"Unable to connect to Arr-App: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unexpected error while testing Arr-App connection");
                return new ValidationFailure(string.Empty, $"Unexpected error: {ex.Message}");
            }
        }

        private ValidationFailure? TestWritePermission()
        {
            try
            {
                if (!Directory.Exists(Settings.CacheDirectory))
                    Directory.CreateDirectory(Settings.CacheDirectory);
                string testFilePath = Path.Combine(Settings.CacheDirectory, "test_write_permission.tmp");
                File.WriteAllText(testFilePath, "This is a test file to check write permissions.");
                string content = File.ReadAllText(testFilePath);
                File.Delete(testFilePath);
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warn(ex, "Write permission denied for cache directory");
                return new ValidationFailure("CacheDirectory", $"Write permission denied for cache directory: {ex.Message}");
            }
            catch (IOException ex)
            {
                _logger.Warn(ex, "IO error while testing cache directory write permissions");
                return new ValidationFailure("CacheDirectory", $"IO error while testing cache directory: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unexpected error while testing cache directory write permissions");
                return new ValidationFailure("CacheDirectory", $"Unexpected error: {ex.Message}");
            }
        }

        public override IEnumerable<ProviderDefinition> DefaultDefinitions
        {
            get
            {
                yield return GetDefinition("Radarr", GetSettings("http://localhost:7878", "/api/v3/system/status", "/api/v3/movie"));
                yield return GetDefinition("Sonarr", GetSettings("http://localhost:8989", "/api/v3/system/status", "/api/v3/series"));
            }
        }

        private ImportListDefinition GetDefinition(string name, ArrSoundtrackImportSettings settings) => new()
        {
            EnableAutomaticAdd = false,
            Name = name + " Soundtracks",
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