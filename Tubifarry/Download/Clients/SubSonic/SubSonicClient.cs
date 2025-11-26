using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using System.Text.Json;
using Tubifarry.Core.Utilities;
using Tubifarry.Download.Base;
using Tubifarry.Indexers.SubSonic;

namespace Tubifarry.Download.Clients.SubSonic
{
    /// <summary>
    /// SubSonic download client for downloading music from SubSonic servers
    /// Integrates with SubSonic API to download tracks and albums
    /// </summary>
    public class SubSonicClient : DownloadClientBase<SubSonicProviderSettings>
    {
        private readonly ISubSonicDownloadManager _downloadManager;
        private readonly INamingConfigService _namingService;
        private readonly IEnumerable<IHttpRequestInterceptor> _requestInterceptors;

        public SubSonicClient(
            ISubSonicDownloadManager downloadManager,
            IConfigService configService,
            IDiskProvider diskProvider,
            INamingConfigService namingConfigService,
            IRemotePathMappingService remotePathMappingService,
            ILocalizationService localizationService,
            IEnumerable<IHttpRequestInterceptor> requestInterceptors,
            Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _downloadManager = downloadManager;
            _requestInterceptors = requestInterceptors;
            _namingService = namingConfigService;
        }

        public override string Name => "SubSonic";
        public override string Protocol => nameof(SubSonicDownloadProtocol);
        public new SubSonicProviderSettings Settings => base.Settings;

        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
            => _downloadManager.Download(remoteAlbum, indexer, _namingService.GetConfig(), this);

        public override IEnumerable<DownloadClientItem> GetItems()
            => _downloadManager.GetItems();

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (deleteData)
                DeleteItemData(item);
            _downloadManager.RemoveItem(item);
        }

        public override DownloadClientInfo GetStatus() => new()
        {
            IsLocalhost = false,
            OutputRootFolders = [new OsPath(Settings.DownloadPath)]
        };

        protected override void Test(List<ValidationFailure> failures)
        {
            // Test download path
            if (!_diskProvider.FolderExists(Settings.DownloadPath))
            {
                failures.Add(new ValidationFailure("DownloadPath", "Download path does not exist"));
                return;
            }

            if (!_diskProvider.FolderWritable(Settings.DownloadPath))
            {
                failures.Add(new ValidationFailure("DownloadPath", "Download path is not writable"));
            }

            // Test SubSonic connection
            try
            {
                string baseUrl = Settings.ServerUrl.TrimEnd('/');
                var urlBuilder = new System.Text.StringBuilder($"{baseUrl}/rest/ping.view");
                SubSonicAuthHelper.AppendAuthParameters(urlBuilder, Settings.Username, Settings.Password, Settings.UseTokenAuth);
                urlBuilder.Append("&f=json");
                string testUrl = urlBuilder.ToString();

                BaseHttpClient httpClient = new(Settings.ServerUrl, _requestInterceptors, TimeSpan.FromSeconds(Settings.RequestTimeout));
                using HttpRequestMessage request = httpClient.CreateRequest(HttpMethod.Get, testUrl);

                _logger.Trace("Testing SubSonic connection to: {BaseUrl}", Settings.ServerUrl);

                var response = httpClient.SendAsync(request, CancellationToken.None).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();

                string responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                var responseWrapper = JsonSerializer.Deserialize<SubSonicPingResponse>(
                    responseContent, IndexerParserHelper.StandardJsonOptions);

                if (responseWrapper?.SubsonicResponse != null)
                {
                    var pingResponse = responseWrapper.SubsonicResponse;

                    if (pingResponse.Status == "ok")
                    {
                        _logger.Debug($"Successfully connected to SubSonic server as {Settings.Username} (API version: {pingResponse.Version})");
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
                            failures.Add(new ValidationFailure("ServerUrl",
                                $"SubSonic API error: {errorMsg}"));
                        }
                        return;
                    }
                }

                failures.Add(new ValidationFailure("ServerUrl",
                    "Failed to connect to SubSonic server. Check the server URL and credentials."));
            }
            catch (HttpRequestException ex)
            {
                _logger.Error(ex, "HTTP error testing SubSonic connection");
                failures.Add(new ValidationFailure("ServerUrl",
                    $"Cannot connect to SubSonic server: {ex.Message}. Check the server URL."));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error testing SubSonic connection");
                failures.Add(new ValidationFailure("ServerUrl",
                    $"Error connecting to SubSonic: {ex.Message}"));
            }
        }
    }
}
