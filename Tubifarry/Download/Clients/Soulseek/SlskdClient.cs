using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using System.Net;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Records;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Download.Clients.Soulseek
{
    public class SlskdClient : DownloadClientBase<SlskdProviderSettings>
    {
        private readonly IHttpClient _httpClient;

        private static readonly Dictionary<DownloadKey, SlskdDownloadItem> _downloadMappings = new();

        public override string Name => "Slskd";
        public override string Protocol => nameof(SoulseekDownloadProtocol);

        public SlskdClient(IHttpClient httpClient, IConfigService configService, IDiskProvider diskProvider, IRemotePathMappingService remotePathMappingService, Logger logger)
            : base(configService, diskProvider, remotePathMappingService, logger) => _httpClient = httpClient;

        public override async Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
        {
            SlskdDownloadItem item = new(remoteAlbum);
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
                catch (Exception) { }
                return null!;
            }
            return item.ID.ToString();
        }

        private void FileStateChanged(object? sender, SlskdFileState fileState)
        {
            fileState.UpdateMaxRetryCount(Settings.RetryAttempts);
            string filename = fileState.File.Filename;
            string extension = Path.GetExtension(filename);
            AudioFormat format = AudioFormatHelper.GetAudioCodecFromExtension(extension.TrimStart('.'));
            if (fileState.GetStatus() == DownloadItemStatus.Warning)
            {
                _logger.Trace($"Retrying download for file: {filename}. Attempt {fileState.RetryCount} of {fileState.MaxRetryCount}");
                _ = RetryDownloadAsync(fileState, (SlskdDownloadItem)sender!);
                return;
            }

            if (fileState.GetStatus() != DownloadItemStatus.Completed || format == AudioFormat.Unknown)
                return;
            if (Settings.UseLRCLIB)
                PostProcess((SlskdDownloadItem)sender!, fileState.File);
        }

        private async Task RetryDownloadAsync(SlskdFileState fileState, SlskdDownloadItem item)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(item.RemoteAlbum.Release.Source);
                JsonElement root = doc.RootElement;
                JsonElement matchingItem = root.EnumerateArray()
                    .FirstOrDefault(x => x.GetProperty("Filename").GetString() == fileState.File.Filename);

                if (matchingItem.ValueKind == JsonValueKind.Undefined)
                    return;
                string payload = JsonSerializer.Serialize(new[] { matchingItem });

                HttpRequest request = BuildHttpRequest(item.RemoteAlbum.Release.DownloadUrl, HttpMethod.Post, payload);
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

        private void PostProcess(SlskdDownloadItem item, SlskdDownloadFile file) => item.PostProcessTasks.Add(Task.Run(async () =>
        {
            string filename = file.Filename.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() ?? "";
            string filePath = Path.Combine(item.GetFullFolderPath(GetRemoteToLocal()).FullPath, filename);

            _logger.Trace($"Starting post-processing for file: {filePath}");

            if (!File.Exists(filePath))
                return;

            _logger.Trace($"File found for post-processing: {filePath}");

            FileInfoParser parser = new(filename);
            if (parser.Title == null)
                return;

            Lyric? lyric = await Lyric.FetchLyricsFromLRCLIBAsync(Settings.LRCLIBInstance, item.RemoteAlbum.Release, parser.Title);
            AudioMetadataHandler metadataHandler = new(filePath) { Lyric = lyric };

            _logger.Trace($"Lyrics successfully written: {await metadataHandler.TryCreateLrcFileAsync(default)}");
        }));

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
            _ = RemoveItemAsync(slskdItem);
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
                    List<string> sortedFilenames = dir.Files?
                        .Select(file => file.Filename)
                        .OrderBy(filename => filename)
                        .ToList() ?? new List<string>();

                    foreach (string? filename in sortedFilenames)
                        hash.Add(filename);
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
            OutputRootFolders = new List<OsPath> { _remotePathMappingService.RemapRemoteToLocal(Settings.Host, new OsPath(Settings.DownloadPath)) }
        };

        private SlskdDownloadItem? GetDownloadItem(string downloadId) => GetDownloadItem(int.Parse(downloadId));
        private SlskdDownloadItem? GetDownloadItem(int downloadId) => _downloadMappings.TryGetValue(new DownloadKey(Definition.Id, downloadId), out SlskdDownloadItem? item) ? item : null;
        private IEnumerable<SlskdDownloadItem> GetDownloadItems() => _downloadMappings.Where(kvp => kvp.Key.OuterKey == Definition.Id).Select(kvp => kvp.Value);
        private void AddDownloadItem(SlskdDownloadItem item) => _downloadMappings[new DownloadKey(Definition.Id, item.ID)] = item;
        private bool RemoveDownloadItem(string downloadId) => _downloadMappings.Remove(new DownloadKey(Definition.Id, int.Parse(downloadId)));
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
            List<SlskdDownloadFile> files = downloadItem.SlskdDownloadDirectory?.Files ?? new List<SlskdDownloadFile>();

            await Task.WhenAll(files.Select(async file =>
            {
                if (!file.State.Contains("Completed"))
                {
                    await _httpClient.ExecuteAsync(BuildHttpRequest($"/api/v0/transfers/downloads/{downloadItem.Username}/{file.Id}", HttpMethod.Delete));
                    await Task.Delay(1000);
                }
                await _httpClient.ExecuteAsync(BuildHttpRequest($"/api/v0/transfers/downloads/{downloadItem.Username}/{file.Id}?remove=true", HttpMethod.Delete));
                _logger.Trace($"Removed download with ID {file.Id}.");
            }));
        }

        private async Task<HttpResponse> ExecuteAsync(HttpRequest request) => await _httpClient.ExecuteAsync(request);
    }
}