using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using System.Net;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Indexers.Soulseek;

namespace Tubifarry.Download.Clients.Soulseek
{
    public class SlskdClient : DownloadClientBase<SlskdProviderSettings>
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
        private readonly IHttpClient _httpClient;
        private readonly IDownloadHistoryService _downloadService;

        private static readonly Dictionary<DownloadKey<int, string>, SlskdDownloadItem> _downloadMappings = [];

        public override string Name => "Slskd";
        public override string Protocol => nameof(SoulseekDownloadProtocol);

        public SlskdClient(IHttpClient httpClient, IDownloadHistoryService downloadService, IConfigService configService, IDiskProvider diskProvider, IRemotePathMappingService remotePathMappingService, ILocalizationService localizationService, Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _httpClient = httpClient;
            _downloadService = downloadService;
        }

        public override async Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
        {
            SlskdDownloadItem item = new(remoteAlbum.Release);
            _logger.Trace($"Download initiated: {remoteAlbum.Release.Title} | Files: {item.FileData.Count}");
            try
            {
                HttpRequest request = BuildHttpRequest(remoteAlbum.Release.DownloadUrl, HttpMethod.Post, remoteAlbum.Release.Source);
                HttpResponse response = await _httpClient.ExecuteAsync(request);

                if (response.StatusCode != HttpStatusCode.Created)
                    throw new DownloadClientException("Failed to create download.");
                item.FileStateChanged += FileStateChanged;
                AddDownloadItem(item);
            }
            catch (Exception)
            {
                try
                {
                    RemoveItemAsync(item).Wait();
                }
                catch { }
                throw;
            }
            return item.ID;
        }

        private void FileStateChanged(object? sender, SlskdFileState fileState)
        {
            fileState.UpdateMaxRetryCount(Settings.RetryAttempts);
            if (fileState.GetStatus() != DownloadItemStatus.Warning)
                return;
            _logger.Trace($"Retry triggered: {Path.GetFileName(fileState.File.Filename)} | State: {fileState.State} | Attempt: {fileState.RetryCount + 1}/{fileState.MaxRetryCount}");
            _ = RetryDownloadAsync(fileState, (SlskdDownloadItem)sender!);
        }

        private async Task RetryDownloadAsync(SlskdFileState fileState, SlskdDownloadItem item)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(item.ReleaseInfo.Source);
                JsonElement root = doc.RootElement;
                JsonElement matchingItem = root.EnumerateArray()
                    .FirstOrDefault(x => x.GetProperty("Filename").GetString() == fileState.File.Filename);

                if (matchingItem.ValueKind == JsonValueKind.Undefined)
                    return;
                string payload = JsonSerializer.Serialize(new[] { matchingItem });

                HttpRequest request = BuildHttpRequest(item.ReleaseInfo.DownloadUrl, HttpMethod.Post, payload);
                HttpResponse response = await _httpClient.ExecuteAsync(request);

                if (response.StatusCode == HttpStatusCode.Created)
                    _logger.Trace($"Successfully retried download for file: {fileState.File.Filename}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to retry download for file: {fileState.File.Filename}");
            }
            fileState.IncrementAttempt();
        }

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            UpdateDownloadItemsAsync().Wait();
            DownloadClientItemClientInfo clientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false);
            foreach (DownloadClientItem? clientItem in GetDownloadItems().Select(x => x.GetDownloadClientItem(GetRemoteToLocal(), Settings.GetTimeout())))
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

            string? itemDirectory = slskdItem.SlskdDownloadDirectory?.Directory;

            _ = RemoveItemAsync(slskdItem);
            RemoveDownloadItem(clientItem.DownloadId);

            if (Settings.CleanStaleDirectories && !string.IsNullOrEmpty(itemDirectory))
                _ = CleanStaleDirectoriesAsync(itemDirectory);
        }

        private async Task CleanStaleDirectoriesAsync(string directoryPath)
        {
            try
            {
                string localPath = _remotePathMappingService.RemapRemoteToLocal(Settings.Host, new OsPath(Path.Combine(Settings.DownloadPath, directoryPath))).FullPath;
                await Task.Delay(1000);
                HttpRequest request = BuildHttpRequest("/api/v0/transfers/downloads/");
                HttpResponse response = await ExecuteAsync(request);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Warn($"Failed to check for remaining downloads. Status Code: {response.StatusCode}");
                    return;
                }

                List<JsonElement>? downloads = JsonSerializer.Deserialize<List<JsonElement>>(response.Content, _jsonOptions);
                bool hasRemainingDownloads = false;
                downloads?.ForEach(user =>
                {
                    user.TryGetProperty("directories", out JsonElement directoriesElement);
                    foreach (SlskdDownloadDirectory dir in SlskdDownloadDirectory.GetDirectories(directoriesElement))
                    {
                        if (dir.Directory.Equals(directoryPath, StringComparison.OrdinalIgnoreCase))
                        {
                            hasRemainingDownloads = true;
                            return;
                        }
                    }
                });

                if (hasRemainingDownloads)
                {
                    _logger.Trace($"Directory {directoryPath} still has active downloads, skipping cleanup");
                    return;
                }
                if (_diskProvider.FolderExists(localPath))
                {
                    _logger.Debug($"Removing stale directory: {localPath}");
                    _diskProvider.DeleteFolder(localPath, true);
                    string? parentPath = Path.GetDirectoryName(localPath);
                    if (!string.IsNullOrEmpty(parentPath) && _diskProvider.FolderExists(parentPath) && _diskProvider.FolderEmpty(parentPath))
                    {
                        _logger.Info($"Removing empty parent directory: {parentPath}");
                        _diskProvider.DeleteFolder(parentPath, true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error cleaning stale directories for path: {directoryPath}");
            }
        }

        private async Task UpdateDownloadItemsAsync()
        {
            HttpRequest request = BuildHttpRequest("/api/v0/transfers/downloads/");
            HttpResponse response = await ExecuteAsync(request);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new DownloadClientException($"Failed to fetch downloads. Status Code: {response.StatusCode}");

            List<JsonElement>? downloads = JsonSerializer.Deserialize<List<JsonElement>>(response.Content, _jsonOptions);
            HashSet<string> currentDownloadIds = [];

            _logger.Debug($"Update Items: Slskd returned {downloads?.Count ?? 0} users | Tracked items: {GetDownloadItems().Count()}");

            downloads?.ForEach(user =>
            {
                user.TryGetProperty("directories", out JsonElement directoriesElement);
                foreach (SlskdDownloadDirectory dir in SlskdDownloadDirectory.GetDirectories(directoriesElement))
                {
                    string hash = SlskdDownloadItem.GetStableMD5Id(dir.Files?.Select(file => file.Filename) ?? []);
                    currentDownloadIds.Add(hash);

                    SlskdDownloadItem? item = GetDownloadItem(hash);
                    if (item == null)
                    {
                        _logger.Trace($"Download item not found, checking history for {hash}");
                        DownloadHistory download = _downloadService.GetLatestGrab(hash);
                        if (download != null)
                            AddDownloadItem(new SlskdDownloadItem(download.Release));
                        else if (Settings.Inclusive)
                            AddDownloadItem(new SlskdDownloadItem(CreateReleaseInfoFromDownloadDirectory(user.GetProperty("username").ToString(), dir)));
                        continue;
                    }
                    item.Username ??= user.GetProperty("username").GetString()!;
                    item.SlskdDownloadDirectory = dir;
                }
            });

            if (Settings.Inclusive)
            {
                foreach (SlskdDownloadItem? item in GetDownloadItems().Where(item => !currentDownloadIds.Contains(item.ID) && item.ReleaseInfo.DownloadProtocol == null))
                {
                    _logger.Trace($"Removing download item {item.ID} as it's no longer found in Slskd downloads");
                    RemoveDownloadItem(item.ID);
                }
            }
        }

        private static ReleaseInfo CreateReleaseInfoFromDownloadDirectory(string username, SlskdDownloadDirectory dir)
        {
            SlskdFolderData folderData = dir.CreateFolderData(username);

            SlskdSearchData searchData = new(null, null, false, false, 1, null);

            IGrouping<string, SlskdFileData> directory = dir.ToSlskdFileDataList().GroupBy(_ => dir.Directory).First();

            AlbumData albumData = SlskdItemsParser.CreateAlbumData(null!, directory, searchData, folderData, null, 0);
            ReleaseInfo release = albumData.ToReleaseInfo();
            release.DownloadProtocol = null;
            return release;
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
            OutputRootFolders = [_remotePathMappingService.RemapRemoteToLocal(Settings.Host, new OsPath(Settings.DownloadPath))]
        };

        private SlskdDownloadItem? GetDownloadItem(string downloadId) => _downloadMappings.TryGetValue(new DownloadKey<int, string>(Definition.Id, downloadId), out SlskdDownloadItem? item) ? item : null;

        private IEnumerable<SlskdDownloadItem> GetDownloadItems() => _downloadMappings.Where(kvp => kvp.Key.OuterKey == Definition.Id).Select(kvp => kvp.Value);

        private void AddDownloadItem(SlskdDownloadItem item) => _downloadMappings[new DownloadKey<int, string>(Definition.Id, item.ID)] = item;

        private bool RemoveDownloadItem(string downloadId) => _downloadMappings.Remove(new DownloadKey<int, string>(Definition.Id, downloadId));

        private OsPath GetRemoteToLocal() => _remotePathMappingService.RemapRemoteToLocal(Settings.Host, new OsPath(Settings.DownloadPath));

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

                Settings.DownloadPath = await FetchDownloadPathAsync() ?? string.Empty;
                if (string.IsNullOrEmpty(Settings.DownloadPath))
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

        private async Task RemoveItemAsync(SlskdDownloadItem downloadItem)
        {
            List<SlskdDownloadFile> files = downloadItem.SlskdDownloadDirectory?.Files ?? [];

            await Task.WhenAll(files.Select(async file =>
            {
                if (!file.State.Contains("Completed"))
                {
                    await ExecuteAsync(BuildHttpRequest($"/api/v0/transfers/downloads/{downloadItem.Username}/{file.Id}", HttpMethod.Delete));
                    await Task.Delay(1000);
                }
                await ExecuteAsync(BuildHttpRequest($"/api/v0/transfers/downloads/{downloadItem.Username}/{file.Id}?remove=true", HttpMethod.Delete));
                _logger.Trace($"Removed download with ID {file.Id}.");
            }));
        }

        private async Task<HttpResponse> ExecuteAsync(HttpRequest request) => await _httpClient.ExecuteAsync(request);
    }
}