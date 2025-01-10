using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.History;
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
        private readonly IHistoryService _historyService;

        private static readonly Dictionary<DownloadKey, SlskdDownloadItem> _downloadMappings = new();

        public override string Name => "Slskd";
        public override string Protocol => nameof(SoulseekDownloadProtocol);

        public SlskdClient(IHttpClient httpClient, IConfigService configService, IDiskProvider diskProvider,IHistoryService history, IRemotePathMappingService remotePathMappingService, Logger logger)
            : base(configService, diskProvider, remotePathMappingService, logger) { _httpClient = httpClient; _historyService = history; }


        public override async Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
        {
            SlskdDownloadItem item = new(remoteAlbum);
            HttpRequest request = BuildHttpRequest(remoteAlbum.Release.DownloadUrl, HttpMethod.Post, remoteAlbum.Release.Source);
            HttpResponse response = await _httpClient.ExecuteAsync(request);

            if (response.StatusCode != HttpStatusCode.Created)
                throw new DownloadClientException("Failed to create download.");
            AddDownloadItem(item);
            return item.ID.ToString();
        }


        public override IEnumerable<DownloadClientItem> GetItems()
        {
            UpdateDownloadItemsAsync().Wait();
            DownloadClientItemClientInfo clientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false);
            foreach (DownloadClientItem? clientItem in GetDownloadItems().Select(x => x.GetDownloadClientItem(Settings.DownloadPath)))
            {
                clientItem.DownloadClientInfo = clientInfo;
                yield return clientItem;
            }
        }

        public override void RemoveItem(DownloadClientItem clientItem, bool deleteData)
        {
            if (!deleteData) return;
            SlskdDownloadItem? slskdItem = GetDownloadItem(clientItem.DownloadId);
            if (slskdItem == null) return;
            RemoveItemAsync(slskdItem).Wait();
            RemoveDownloadItem(clientItem.DownloadId);
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
                    HashCode hash = new();
                    foreach (SlskdDownloadFile file in dir.Files ?? new List<SlskdDownloadFile>())
                        hash.Add(file.Filename);
                    SlskdDownloadItem? item = GetDownloadItem(hash.ToHashCode());
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

        public override DownloadClientInfo GetStatus() => new()
        {
            IsLocalhost = Settings.IsLocalhost,
            OutputRootFolders = new List<OsPath> { Settings.IsRemotePath ? _remotePathMappingService.RemapRemoteToLocal(Settings.BaseUrl, new OsPath(Settings.DownloadPath)) : new OsPath(Settings.DownloadPath) }
        };

        private SlskdDownloadItem? GetDownloadItem(string downloadId) => GetDownloadItem(int.Parse(downloadId));
        private SlskdDownloadItem? GetDownloadItem(int downloadId) => _downloadMappings.TryGetValue(new DownloadKey(Definition.Id, downloadId), out SlskdDownloadItem? item) ? item : null;
        private IEnumerable<SlskdDownloadItem> GetDownloadItems() => _downloadMappings.Where(kvp => kvp.Key.OuterKey == Definition.Id).Select(kvp => kvp.Value);
        private void AddDownloadItem(SlskdDownloadItem item) => _downloadMappings[new DownloadKey(Definition.Id, item.ID)] = item;
        private bool RemoveDownloadItem(string downloadId) => _downloadMappings.Remove(new DownloadKey(Definition.Id, int.Parse(downloadId)));


        protected override void Test(List<ValidationFailure> failures) => failures.AddIfNotNull(TestConnection().Result);

        protected async Task<ValidationFailure> TestConnection()
        {
            try
            {
                Uri uri = new(Settings.BaseUrl);
                if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    IPAddress.TryParse(uri.Host, out IPAddress? ipAddress) && IPAddress.IsLoopback(ipAddress))
                    Settings.IsLocalhost = true;
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

                if (string.IsNullOrEmpty(Settings.DownloadPath))
                {
                    Settings.DownloadPath = await FetchDownloadPathAsync() ?? string.Empty;
                    if (string.IsNullOrEmpty(Settings.DownloadPath))
                        return new ValidationFailure("DownloadPath", "DownloadPath could not be found or is invalid.");
                }
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
                _logger.Trace($"Successfully removed download with ID {item.ID}.");
        }

        private async Task<HttpResponse> ExecuteAsync(HttpRequest request) => await _httpClient.ExecuteAsync(request);
    }
}