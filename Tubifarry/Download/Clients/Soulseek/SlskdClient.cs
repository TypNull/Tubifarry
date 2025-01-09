using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using System.Net;
using System.Text.Json;

namespace NzbDrone.Core.Download.Clients.Soulseek
{
    public class SlskdClient : DownloadClientBase<SlskdProviderSettings>
    {
        private readonly IHttpClient _httpClient;
        private Dictionary<string, SlskdDownloadItem> _downloadMapping;
        private string _downloadPath = string.Empty;
        private bool _isLocalhost = false;

        public SlskdClient(IHttpClient httpClient, IConfigService configService, IDiskProvider diskProvider, IRemotePathMappingService remotePathMappingService, Logger logger)
            : base(configService, diskProvider, remotePathMappingService, logger)
        {
            _httpClient = httpClient;
            _downloadMapping = new Dictionary<string, SlskdDownloadItem>();
        }

        public override string Name => "Slskd";

        public override string Protocol => nameof(SoulseekDownloadProtocol);

        public override async Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
        {
            SlskdDownloadItem item = new(Guid.NewGuid().ToString(), remoteAlbum);
            HttpRequest request = BuildHttpRequest(remoteAlbum.Release.DownloadUrl, HttpMethod.Post, remoteAlbum.Release.Source);
            HttpResponse response = await _httpClient.ExecuteAsync(request);

            if (response.StatusCode != HttpStatusCode.Created)
                throw new DownloadClientException("Failed to create download.");

            _downloadMapping[item.ID] = item;
            return item.ID;
        }

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            UpdateDownloadItemsAsync().Wait();
            DownloadClientItemClientInfo clientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false);
            foreach (KeyValuePair<string, SlskdDownloadItem> kpv in _downloadMapping)
            {
                DownloadClientItem clientItem = kpv.Value.GetDownloadClientItem(_downloadPath);
                clientItem.DownloadClientInfo = clientInfo;
                yield return clientItem;
            }
        }

        public override void RemoveItem(DownloadClientItem clientItem, bool deleteData)
        {
            if (!deleteData) return;
            _downloadMapping.TryGetValue(clientItem.DownloadId, out SlskdDownloadItem? slskdItem);
            if (slskdItem == null) return;
            RemoveItemAsync(slskdItem).Wait();
            _downloadMapping.Remove(clientItem.DownloadId);
        }

        private async Task UpdateDownloadItemsAsync()
        {
            HttpRequest request = BuildHttpRequest("/api/v0/transfers/downloads/");
            HttpResponse response = await _httpClient.ExecuteAsync(request);

            if (response.StatusCode != HttpStatusCode.OK)
                throw new DownloadClientException($"Failed to fetch downloads. Status Code: {response.StatusCode}");
            List<JsonElement>? downloads = JsonSerializer.Deserialize<List<JsonElement>>(response.Content);
            downloads?.ForEach(user =>
            {
                user.TryGetProperty("directories", out JsonElement directoriesElement);
                IEnumerable<SlskdDownloadDirectory> data = SlskdDownloadDirectory.GetDirectories(directoriesElement);
                foreach (SlskdDownloadDirectory dir in data)
                {
                    SlskdDownloadItem? item = _downloadMapping.Values.FirstOrDefault(x => x.FileData.Any(y => y.Filename?.Contains(dir.Directory!) ?? false));
                    if (item == null)
                        continue;
                    item.Username ??= user.GetProperty("username").GetString()!;
                    item.SlskdDownloadDirectory = dir;
                }
            });
        }

        private async Task<string?> FetchDownloadPathAsync()
        {
            try
            {
                HttpResponse response = await _httpClient.ExecuteAsync(BuildHttpRequest("/api/v0/options"));

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Warn($"Failed to fetch options. Status Code: {response.StatusCode}");
                    return null;
                }

                using JsonDocument doc = JsonDocument.Parse(response.Content);
                if (doc.RootElement.TryGetProperty("directories", out JsonElement directories) &&
                    directories.TryGetProperty("downloads", out JsonElement downloads)) return downloads.GetString();

                _logger.Warn("Download path not found in the options.");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to fetch download path from Slskd.");
            }

            return null;
        }

        public override DownloadClientInfo GetStatus()
        {
            if (string.IsNullOrEmpty(_downloadPath))
                _downloadPath = string.IsNullOrEmpty(Settings.DownloadPath) ? FetchDownloadPathAsync().Result ?? Settings.DownloadPath : Settings.DownloadPath;
            return new DownloadClientInfo
            {
                IsLocalhost = _isLocalhost,
                OutputRootFolders = new List<OsPath> { _remotePathMappingService.RemapRemoteToLocal(Settings.BaseUrl, new OsPath(_downloadPath)) }
            };
        }

        protected override void Test(List<ValidationFailure> failures) => failures.AddIfNotNull(TestConnection().Result);

        protected async Task<ValidationFailure> TestConnection()
        {
            try
            {
                Uri uri = new(Settings.BaseUrl);
                if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    IPAddress.TryParse(uri.Host, out IPAddress? ipAddress) && IPAddress.IsLoopback(ipAddress))
                    _isLocalhost = true;
            }
            catch (UriFormatException ex)
            {
                _logger.Warn($"Invalid BaseUrl format: {Settings.BaseUrl}");
                return new ValidationFailure("BaseUrl", $"Invalid BaseUrl format: {ex.Message}");
            }

            try
            {
                HttpRequest request = BuildHttpRequest("/api/v0/application");
                request.AllowAutoRedirect = true;
                request.RequestTimeout = TimeSpan.FromSeconds(30);
                HttpResponse response = await _httpClient.ExecuteAsync(request);

                if (response.StatusCode != HttpStatusCode.OK)
                    return new ValidationFailure("BaseUrl", $"Unable to connect to Slskd. Status: {response.StatusCode}");

                using JsonDocument jsonDocument = JsonDocument.Parse(response.Content);
                JsonElement jsonResponse = jsonDocument.RootElement;

                if (!jsonResponse.TryGetProperty("server", out JsonElement serverElement) ||
                    !serverElement.TryGetProperty("state", out JsonElement stateElement))
                    return new ValidationFailure("BaseUrl", "Failed to parse Slskd response: missing 'server' or 'state'.");


                string? serverState = stateElement.GetString();
                if (string.IsNullOrEmpty(serverState) || !serverState.Contains("Connected"))
                    return new ValidationFailure("BaseUrl", $"Slskd server is not connected. State: {serverState}");


                _downloadPath = string.IsNullOrEmpty(Settings.DownloadPath) ? await FetchDownloadPathAsync() ?? Settings.DownloadPath : Settings.DownloadPath;
                if (string.IsNullOrEmpty(_downloadPath))
                    return new ValidationFailure("DownloadPath", "DownloadPath could not be found or is invalid.");
                return null!;
            }
            catch (HttpException ex)
            {
                return new ValidationFailure("BaseUrl", $"Unable to connect to Slskd: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unexpected error while testing Slskd connection.");
                return new ValidationFailure(string.Empty, $"Unexpected error: {ex.Message}");
            }
        }

        private HttpRequest BuildHttpRequest(string endpoint, HttpMethod? method = null, string? content = null)
        {
            HttpRequestBuilder requestBuilder = new HttpRequestBuilder($"{Settings.BaseUrl}{endpoint}")
                .SetHeader("X-API-KEY", Settings.ApiKey)
                .SetHeader("Accept", "application/json");

            if (method != null)
                requestBuilder.Method = method;

            bool hasContent = !string.IsNullOrEmpty(content);
            if (hasContent)
                requestBuilder.SetHeader("Content-Type", "application/json");

            HttpRequest request = requestBuilder.Build();
            if (hasContent)
                request.SetContent(content);
            return request;
        }

        private async Task RemoveItemAsync(SlskdDownloadItem item)
        {
            HttpResponse response = await _httpClient.ExecuteAsync(BuildHttpRequest($"/api/v0/transfers/downloads/{item.Username}/{item.ID}", HttpMethod.Delete));

            if (response.StatusCode != HttpStatusCode.NoContent)
                _logger.Warn($"Failed to remove download with ID {item.ID}. Status Code: {response.StatusCode}");
            else
                _logger.Info($"Successfully removed download with ID {item.ID}.");
        }

        private async Task<HttpResponse> ExecuteAsync(HttpRequest request) => await _httpClient.ExecuteAsync(request);
    }
}