using DownloadAssistant.Options;
using DownloadAssistant.Requests;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Download;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using Requests;
using Requests.Options;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tubifarry.Core.Utilities;
using Tubifarry.Indexers.Lucida;

namespace Tubifarry.Download.Clients.Lucida
{
    /// <summary>
    /// Lucida download request handling track and album downloads
    /// </summary>
    internal class LucidaDownloadRequest : Request<LucidaDownloadOptions, string, string>
    {
        #region Private Fields

        private readonly OsPath _destinationPath;
        private readonly StringBuilder _message = new();
        private readonly RequestContainer<IRequest> _requestContainer = new();
        private readonly RequestContainer<LoadRequest> _trackContainer = new();
        private readonly RemoteAlbum _remoteAlbum;
        private readonly Album _albumData;
        private readonly DownloadClientItem _clientItem;
        private readonly ReleaseFormatter _releaseFormatter;
        private readonly Logger _logger;
        private readonly LucidaHttpClient _httpClient;
        private int _expectedTrackCount;

        // Regex pattern for sanitizing filenames
        private static readonly Regex FileNameSanitizer = new(@"[\\/:\*\?""<>\|]", RegexOptions.Compiled);

        // Progress tracking
        private DateTime _lastUpdateTime = DateTime.MinValue;
        private long _lastRemainingSize;

        #endregion

        #region Properties

        private ReleaseInfo ReleaseInfo => _remoteAlbum.Release;
        public override Task Task => _requestContainer.Task;
        public override RequestState State => _requestContainer.State;
        public string ID { get; } = Guid.NewGuid().ToString();

        public DownloadClientItem ClientItem
        {
            get
            {
                _clientItem.RemainingSize = GetRemainingSize();
                _clientItem.Status = GetDownloadItemStatus();
                _clientItem.RemainingTime = GetRemainingTime();
                _clientItem.Message = GetDistinctMessages();
                _clientItem.CanBeRemoved = HasCompleted();
                _clientItem.CanMoveFiles = HasCompleted();
                return _clientItem;
            }
        }

        #endregion

        #region Constructor

        public LucidaDownloadRequest(RemoteAlbum remoteAlbum, LucidaDownloadOptions? options) : base(options)
        {
            _logger = NzbDroneLogger.GetLogger(this);
            _remoteAlbum = remoteAlbum;
            _albumData = remoteAlbum.Albums.FirstOrDefault() ?? new Album();
            _releaseFormatter = new ReleaseFormatter(ReleaseInfo, remoteAlbum.Artist, Options.NamingConfig);
            _requestContainer.Add(_trackContainer);
            _expectedTrackCount = Options.IsTrack ? 1 : remoteAlbum.Albums.FirstOrDefault()?.AlbumReleases.Value?.FirstOrDefault()?.TrackCount ?? 0;

            _httpClient = new LucidaHttpClient(Options.BaseUrl);

            _destinationPath = new OsPath(Path.Combine(Options.DownloadPath, _releaseFormatter.BuildArtistFolderName(null), _releaseFormatter.BuildAlbumFilename("{Album Title}", new Album() { Title = ReleaseInfo.Title })));

            _clientItem = CreateClientItem();
            _logger.Debug($"Processing download - Type from Source: {ReleaseInfo.Source}, IsTrack: {Options.IsTrack}, URL: {Options.ItemUrl}");

            ProcessDownload();
        }

        #endregion

        #region Main Processing Methods

        private void ProcessDownload() => _requestContainer.Add(new OwnRequest(async (token) =>
        {
            try
            {
                _logger.Trace($"Processing {(Options.IsTrack ? "track" : "album")}: {ReleaseInfo.Title}");

                if (Options.IsTrack)
                    await ProcessSingleTrackAsync(Options.ItemUrl, token);
                else
                    await ProcessAlbumAsync(Options.ItemUrl, token);
            }
            catch (Exception ex)
            {
                LogAndAppendMessage($"Error processing download: {ex.Message}", LogLevel.Error);
                throw;
            }
            return true;
        }, new RequestOptions<VoidStruct, VoidStruct>()
        {
            CancellationToken = Token,
            DelayBetweenAttemps = Options.DelayBetweenAttemps,
            NumberOfAttempts = Options.NumberOfAttempts,
            Priority = RequestPriority.Low,
            Handler = Options.Handler
        }));

        private async Task ProcessSingleTrackAsync(string downloadUrl, CancellationToken token)
        {
            LucidaTokens tokens = await LucidaTokenExtractor.ExtractTokensAsync(_httpClient, downloadUrl);

            if (!tokens.IsValid)
                throw new Exception("Failed to extract authentication tokens");

            string fileName = _releaseFormatter.BuildTrackFilename(null, new Track { Title = ReleaseInfo.Title, Artist = _remoteAlbum.Artist }, _albumData) + AudioFormatHelper.GetFileExtensionForCodec(_remoteAlbum.Release.Codec.ToLower());
            InitiateDownload(downloadUrl, tokens.Primary, tokens.Fallback, tokens.Expiry, fileName, token);
            _requestContainer.Add(_trackContainer);
        }

        private async Task ProcessAlbumAsync(string downloadUrl, CancellationToken token)
        {
            LucidaAlbumModel album = await LucidaMetadataExtractor.ExtractAlbumMetadataAsync(_httpClient, downloadUrl);
            _expectedTrackCount = album.Tracks.Count;
            _logger.Trace($"Found {album.Tracks.Count} tracks in album: {album.Title}");

            if (!album.HasValidTokens)
                throw new Exception("Failed to extract authentication tokens from album page");

            for (int i = 0; i < album.Tracks.Count; i++)
            {
                LucidaTrackModel track = album.Tracks[i];
                string trackFileName = $"{track.TrackNumber:D2}. {SanitizeFileName(track.Title)}{AudioFormatHelper.GetFileExtensionForCodec(_remoteAlbum.Release.Codec.ToLower())}";

                try
                {
                    string? trackUrl = !string.IsNullOrEmpty(track.Url) ? track.Url : track.OriginalServiceUrl;
                    if (string.IsNullOrEmpty(trackUrl))
                    {
                        _logger.Warn($"No URL available for track: {track.Title}");
                        continue;
                    }

                    InitiateDownload(trackUrl, album.PrimaryToken!, album.FallbackToken!, album.TokenExpiry, trackFileName, token);
                    _logger.Trace($"Track {i + 1}/{album.Tracks.Count} completed: {track.Title}");
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Track {i + 1}/{album.Tracks.Count} failed: {track.Title} - {ex.Message}", LogLevel.Error);
                }
            }
            _requestContainer.Add(_trackContainer);
        }
        #endregion

        #region Download Workflow Methods

        private void InitiateDownload(string url, string primaryToken, string fallbackToken, long expiry, string fileName, CancellationToken token)
        {
            OwnRequest downloadRequestWrapper = new(async (t) =>
            {
                string handoffId = null!;
                string serverName = null!;
                try
                {
                    (handoffId, serverName) = await InitiateDownloadAsync(url, primaryToken, fallbackToken, expiry, t);
                    _logger.Trace($"Initiation completed - Handoff: {handoffId}, Server: {serverName}");
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Initiation failed: {ex.Message}", LogLevel.Error);
                    return false;
                }
                try
                {
                    if (!await PollForCompletionAsync(handoffId, serverName, t))
                        return false;
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Polling failed: {ex.Message}", LogLevel.Error);
                    return false;
                }
                try
                {
                    string domain = ExtractDomainFromUrl(Options.BaseUrl);
                    string downloadUrl = $"https://{serverName}.{domain}/api/fetch/request/{handoffId}/download";

                    LoadRequest downloadRequest = new(downloadUrl, new LoadRequestOptions()
                    {
                        CancellationToken = t,
                        CreateSpeedReporter = true,
                        SpeedReporterTimeout = 1000,
                        Priority = RequestPriority.Normal,
                        MaxBytesPerSecond = Options.MaxDownloadSpeed,
                        DelayBetweenAttemps = Options.DelayBetweenAttemps,
                        Filename = fileName,
                        AutoStart = true,
                        DestinationPath = _destinationPath.FullPath,
                        Handler = Options.Handler,
                        DeleteFilesOnFailure = true,
                        RequestFailed = (_, __) => LogAndAppendMessage($"Download failed: {fileName}", LogLevel.Error),
                        WriteMode = WriteMode.AppendOrTruncate,
                    });

                    _trackContainer.Add(downloadRequest);
                    return true;
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Download request failed: {ex.Message}", LogLevel.Error);
                    return false;
                }
            }, new RequestOptions<VoidStruct, VoidStruct>()
            {
                AutoStart = true,
                Priority = RequestPriority.High,
                NumberOfAttempts = Options.NumberOfAttempts,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                Handler = Options.Handler,
                CancellationToken = token
            });
            _requestContainer.Add(downloadRequestWrapper);
        }

        private async Task<(string handoffId, string serverName)> InitiateDownloadAsync(string url, string primaryToken, string fallbackToken, long expiry, CancellationToken token)
        {
            try
            {
                LucidaDownloadRequestInfo request = LucidaDownloadRequestInfo.CreateWithTokens(url, primaryToken, fallbackToken, expiry);
                string requestBody = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                const string apiEndpoint = "%2Fapi%2Ffetch%2Fstream%2Fv2";
                string requestUrl = $"{_httpClient.BaseUrl}/api/load?url={apiEndpoint}";
                _logger.Trace($"Initiating track download - URL: {url}, Request URL: {requestUrl}");

                HttpRequestMessage httpRequest = new(HttpMethod.Post, requestUrl);
                httpRequest.Content = new StringContent(requestBody, Encoding.UTF8, "text/plain");
                httpRequest.Headers.Add("Origin", _httpClient.BaseUrl);
                httpRequest.Headers.Add("Referer", $"{_httpClient.BaseUrl}/?url={Uri.EscapeDataString(url)}");
                HttpResponseMessage response = await _httpClient.PostAsync(httpRequest);
                string responseContent = await response.Content.ReadAsStringAsync(token);
                response.EnsureSuccessStatusCode();

                LucidaDownloadResponse? downloadResponse = JsonSerializer.Deserialize<LucidaDownloadResponse>(responseContent, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                if (downloadResponse?.Success == true && !string.IsNullOrEmpty(downloadResponse.Handoff))
                    return (downloadResponse.Handoff, downloadResponse.Server ?? downloadResponse.Name ?? "hund");

                string errorInfo = downloadResponse != null ? $"Success: {downloadResponse.Success}, Handoff: {downloadResponse.Handoff}, Server: {downloadResponse.Server}, Name: {downloadResponse.Name}, Error: {downloadResponse.Error}" : "Failed to deserialize response";
                throw new Exception($"Failed to initiate download - {errorInfo ?? "no handoff ID received"}");
            }
            catch (Exception ex)
            {
                LogAndAppendMessage($"Error initiating download: {ex.Message}", LogLevel.Error);
                throw;
            }
        }

        private async Task<bool> PollForCompletionAsync(string handoffId, string serverName, CancellationToken token)
        {
            const int baseAttempts = 15;
            int delayMs = 3000;
            int serviceUnavailableExtensions = 1;
            const int maxServiceUnavailableExtensions = 20; // Up to 100 minutes total

            await Task.Delay(delayMs * 2, token);

            int totalAttempts = baseAttempts + (serviceUnavailableExtensions * 5);
            for (int attempt = 1; attempt <= totalAttempts; attempt++)
            {
                if (token.IsCancellationRequested)
                    return false;

                try
                {
                    string statusUrl = $"https://{serverName}.{ExtractDomainFromUrl(Options.BaseUrl)}/api/fetch/request/{handoffId}";
                    string responseContent = await _httpClient.GetStringAsync(statusUrl);

                    LucidaStatusResponse? status = JsonSerializer.Deserialize<LucidaStatusResponse>(responseContent, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                    if (status?.Success == true && status.Status == "completed")
                        return true;

                    if (!string.IsNullOrEmpty(status?.Error) && status.Error != "Request not found." && status.Error != "No such request")
                        throw new Exception($"Server error: {status.Error}");
                }
                catch (HttpRequestException httpEx) when (httpEx.StatusCode == System.Net.HttpStatusCode.InternalServerError)
                {
                    _logger.Error($"Polling failed with 500 Internal Server Error - handoff ID may be invalid: {httpEx.Message}");
                    throw new Exception($"Server internal error - handoff ID invalid: {httpEx.Message}");
                }
                catch (HttpRequestException httpEx) when (httpEx.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {

                    if (attempt >= baseAttempts && serviceUnavailableExtensions < maxServiceUnavailableExtensions)
                    {
                        serviceUnavailableExtensions++;
                        totalAttempts = baseAttempts + (serviceUnavailableExtensions * 5);
                        _logger.Warn($"Service unavailable - extending polling with 5-minute intervals (extension {serviceUnavailableExtensions}/{maxServiceUnavailableExtensions})");
                        await Task.Delay(TimeSpan.FromMinutes(5), token);
                        continue;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Trace($"Polling attempt {attempt} failed: {ex.Message}");
                }

                await Task.Delay(delayMs, token);
                delayMs = Math.Min(delayMs * 2, 6000);
            }

            throw new Exception("Download did not complete within expected time");
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Extracts the domain name from a URL (removes protocol)
        /// </summary>
        private static string ExtractDomainFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return "lucida.to";
            return url.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        }

        /// <summary>
        /// Sanitizes a filename by removing invalid characters
        /// </summary>
        private static string SanitizeFileName(string fileName) => string.IsNullOrEmpty(fileName) ? "Unknown" : FileNameSanitizer.Replace(fileName, "_").Trim();

        private void LogAndAppendMessage(string message, LogLevel logLevel)
        {
            _message.AppendLine(message);
            _logger?.Log(logLevel, message);
        }

        private DownloadClientItem CreateClientItem() => new()
        {
            DownloadId = ID,
            Title = ReleaseInfo.Title,
            TotalSize = ReleaseInfo.Size,
            DownloadClientInfo = Options.ClientInfo,
            OutputPath = _destinationPath,
        };

        private TimeSpan? GetRemainingTime()
        {
            long remainingSize = GetRemainingSize();
            if (_lastUpdateTime != DateTime.MinValue && _lastRemainingSize != 0)
            {
                TimeSpan timeElapsed = DateTime.UtcNow - _lastUpdateTime;
                long bytesDownloaded = _lastRemainingSize - remainingSize;

                if (timeElapsed.TotalSeconds > 0 && bytesDownloaded > 0)
                {
                    double bytesPerSecond = bytesDownloaded / timeElapsed.TotalSeconds;
                    double remainingSeconds = remainingSize / bytesPerSecond;
                    return remainingSeconds < 0 ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(remainingSeconds);
                }
            }

            _lastUpdateTime = DateTime.UtcNow;
            _lastRemainingSize = remainingSize;
            return null;
        }

        private string GetDistinctMessages() => string.Join(Environment.NewLine, _message.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Distinct());

        private long GetRemainingSize()
        {
            long totalDownloaded = _trackContainer.Sum(t => t.BytesDownloaded);
            IEnumerable<LoadRequest> knownSizes = _trackContainer.Where(t => t.ContentLength > 0);
            int knownCount = knownSizes.Count();

            return (_expectedTrackCount, knownCount) switch
            {
                (0, _) => ReleaseInfo.Size - totalDownloaded,
                (var expected, var count) when count == expected => knownSizes.Sum(t => t.ContentLength) - totalDownloaded,
                (var expected, var count) when count > 2 => (long)(knownSizes.Average(t => t.ContentLength) * expected) - totalDownloaded,
                (var expected, var count) when count > 0 => Math.Max((long)(knownSizes.Average(t => t.ContentLength) * expected), ReleaseInfo.Size) - totalDownloaded,
                _ => ReleaseInfo.Size - totalDownloaded
            };
        }

        public DownloadItemStatus GetDownloadItemStatus() => State switch
        {
            RequestState.Idle => DownloadItemStatus.Queued,
            RequestState.Paused => DownloadItemStatus.Paused,
            RequestState.Running => DownloadItemStatus.Downloading,
            RequestState.Compleated => DownloadItemStatus.Completed,
            RequestState.Failed => _requestContainer.Count(x => x.State == RequestState.Failed) >= _requestContainer.Count / 2
                                   ? DownloadItemStatus.Failed
                                   : _requestContainer.All(x => x.HasCompleted()) ? DownloadItemStatus.Completed : DownloadItemStatus.Failed,
            _ => DownloadItemStatus.Warning,
        };

        #endregion

        #region Abstract Method Implementations

        protected override Task<RequestReturn> RunRequestAsync() => throw new NotImplementedException();
        public override void Start() => throw new NotImplementedException();
        public override void Pause() => throw new NotImplementedException();

        #endregion
    }
}
