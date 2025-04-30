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
using Tubifarry.Download.Clients.Lucida.Models;

namespace Tubifarry.Download.Clients.Lucida
{
    /// <summary>
    /// Lucida download request handling track and album downloads
    /// Follows the same pattern as YouTubeAlbumRequest for consistency
    /// </summary>
    internal class LucidaDownloadRequest : Request<LucidaDownloadOptions, string, string>
    {
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

        // Static regex patterns for performance
        private static readonly Regex LucidaUrlRegex = new(@"^https?://lucida\.to", RegexOptions.Compiled);
        private static readonly Regex FileNameSanitizer = new(@"[\\/:\*\?""<>\|]", RegexOptions.Compiled);
        private static readonly Regex CsrfExtractor = new(@"""csrf""\s*:\s*""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex CsrfFallbackExtractor = new(@"""csrfFallback""\s*:\s*""([^""]+)""", RegexOptions.Compiled);

        // Progress tracking
        private DateTime _lastUpdateTime = DateTime.MinValue;
        private long _lastRemainingSize;

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

        public LucidaDownloadRequest(RemoteAlbum remoteAlbum, LucidaDownloadOptions? options) : base(options)
        {
            _logger = NzbDroneLogger.GetLogger(this);
            _remoteAlbum = remoteAlbum;
            _albumData = remoteAlbum.Albums.FirstOrDefault() ?? new Album();
            _releaseFormatter = new ReleaseFormatter(ReleaseInfo, remoteAlbum.Artist, Options.NamingConfig);
            _requestContainer.Add(_trackContainer);

            _httpClient = new LucidaHttpClient(Options.BaseUrl);

            _destinationPath = new OsPath(Path.Combine(
                Options.DownloadPath,
                _releaseFormatter.BuildArtistFolderName(null),
                _releaseFormatter.BuildAlbumFilename("{Album Title}", new Album() { Title = ReleaseInfo.Title })));

            _clientItem = CreateClientItem();
            
            // Log the type information for debugging
            _logger.Debug($"Processing download - Type from Source: {ReleaseInfo.Source}, IsTrack: {Options.IsTrack}, URL: {Options.ActualUrl}");
            
            ProcessDownload();
        }

        private void ProcessDownload()
        {
            _requestContainer.Add(new OwnRequest(async (token) =>
            {
                await ProcessDownloadAsync(token);
                return true;
            }, new RequestOptions<VoidStruct, VoidStruct>()
            {
                CancellationToken = Token,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                NumberOfAttempts = Options.NumberOfAttempts,
                Priority = RequestPriority.Low,
                Handler = Options.Handler
            }));
        }

        private async Task ProcessDownloadAsync(CancellationToken token)
        {
            try
            {
                string downloadUrl = NormalizeUrl(Options.ActualUrl);
                LogAndAppendMessage($"Processing {(Options.IsTrack ? "track" : "album")}: {ReleaseInfo.Title}", LogLevel.Info);

                Directory.CreateDirectory(_destinationPath.FullPath);

                if (Options.IsTrack)
                {
                    await ProcessSingleTrackAsync(downloadUrl, token);
                }
                else
                {
                    await ProcessAlbumAsync(downloadUrl, token);
                }
            }
            catch (Exception ex)
            {
                LogAndAppendMessage($"Error processing download: {ex.Message}", LogLevel.Error);
                throw;
            }
        }

        private async Task ProcessSingleTrackAsync(string downloadUrl, CancellationToken token)
        {
            (string primaryToken, string fallbackToken, long expiry) = await ExtractTokensAsync(downloadUrl, token);

            if (string.IsNullOrEmpty(primaryToken) || string.IsNullOrEmpty(fallbackToken))
            {
                throw new Exception("Failed to extract authentication tokens");
            }

            string fileName = SanitizeFileName(_releaseFormatter.BuildTrackFilename(null,
                new Track { Title = ReleaseInfo.Title, Artist = _remoteAlbum.Artist }, _albumData) + ".flac");

            await InitiateAndDownloadAsync(downloadUrl, primaryToken, fallbackToken, expiry, fileName, token);
        }

        private async Task ProcessAlbumAsync(string downloadUrl, CancellationToken token)
        {
            // For albums, we need to fetch the album page and download individual tracks
            // This matches how the test project works
            LogAndAppendMessage("Fetching album page to get track list...", LogLevel.Info);
            
            string albumPageUrl = $"{_httpClient.BaseUrl}/?url={Uri.EscapeDataString(downloadUrl)}";
            string albumHtml = await _httpClient.GetStringAsync(albumPageUrl);
            
            List<TrackInfo> tracks = ExtractTracksFromAlbumPage(albumHtml, downloadUrl);
            LogAndAppendMessage($"Found {tracks.Count} tracks in album", LogLevel.Info);
            
            if (tracks.Count == 0)
            {
                throw new Exception("No tracks found in album");
            }

            // Download each track individually (like the test project does)
            for (int i = 0; i < tracks.Count; i++)
            {
                TrackInfo track = tracks[i];
                string trackFileName = SanitizeFileName($"{i + 1:D2}. {track.Title}.flac");
                
                try
                {
                    await ProcessIndividualTrackAsync(track.Url, trackFileName, token);
                    LogAndAppendMessage($"Track {i + 1}/{tracks.Count} completed: {track.Title}", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Track {i + 1}/{tracks.Count} failed: {track.Title} - {ex.Message}", LogLevel.Error);
                    // Continue with other tracks instead of failing the entire album
                }
            }
        }

        private async Task ProcessIndividualTrackAsync(string trackUrl, string fileName, CancellationToken token)
        {
            (string primaryToken, string fallbackToken, long expiry) = await ExtractTokensAsync(trackUrl, token);

            if (string.IsNullOrEmpty(primaryToken) || string.IsNullOrEmpty(fallbackToken))
            {
                throw new Exception("Failed to extract authentication tokens for track");
            }

            await InitiateAndDownloadAsync(trackUrl, primaryToken, fallbackToken, expiry, fileName, token);
        }

        private List<TrackInfo> ExtractTracksFromAlbumPage(string html, string albumUrl)
        {
            List<TrackInfo> tracks = new();
            
            try
            {
                // Extract track URLs from the album page HTML
                // Look for patterns like: href="/track/205683336" or similar
                var trackMatches = Regex.Matches(html, @"/track/([^""']+)");
                
                foreach (Match match in trackMatches)
                {
                    string trackId = match.Groups[1].Value;
                    string baseUrl = albumUrl.Contains("tidal.com") ? "http://www.tidal.com" : "";
                    
                    if (!string.IsNullOrEmpty(baseUrl))
                    {
                        string trackUrl = $"{baseUrl}/track/{trackId}";
                        tracks.Add(new TrackInfo
                        {
                            Title = $"Track {tracks.Count + 1}", // Will be updated with real title if available
                            Url = trackUrl,
                            TrackNumber = tracks.Count + 1
                        });
                    }
                }
                
                // Try to extract track titles if available
                // This is a simplified approach - the test project likely has more sophisticated parsing
                var titleMatches = Regex.Matches(html, @"""title""\s*:\s*""([^""]+)""");
                for (int i = 0; i < Math.Min(tracks.Count, titleMatches.Count); i++)
                {
                    tracks[i] = tracks[i] with { Title = titleMatches[i].Groups[1].Value };
                }
            }
            catch (Exception ex)
            {
                LogAndAppendMessage($"Error extracting tracks from album page: {ex.Message}", LogLevel.Error);
            }
            
            return tracks;
        }

        private async Task InitiateAndDownloadAsync(string url, string primaryToken, string fallbackToken, long expiry, string fileName, CancellationToken token)
        {
            string handoffId = null;
            string serverName = null;

            // Step 1: Initiate download
            OwnRequest initRequest = new(async (t) =>
            {
                try
                {
                    (handoffId, serverName) = await InitiateDownloadAsync(url, primaryToken, fallbackToken, expiry, t);
                    LogAndAppendMessage($"Initiation completed - Handoff: {handoffId}, Server: {serverName}", LogLevel.Debug);
                    return !string.IsNullOrEmpty(handoffId);
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Initiation failed: {ex.Message}", LogLevel.Error);
                    return false;
                }
            }, new RequestOptions<VoidStruct, VoidStruct>()
            {
                CancellationToken = token,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                NumberOfAttempts = Options.NumberOfAttempts,
                Priority = RequestPriority.High,
                Handler = Options.Handler
            });

            // Step 2: Poll for completion
            OwnRequest pollRequest = new(async (t) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(handoffId) || string.IsNullOrEmpty(serverName))
                    {
                        LogAndAppendMessage("Cannot poll - missing handoff ID or server name", LogLevel.Error);
                        return false;
                    }
                    return await PollForCompletionAsync(handoffId, serverName, t);
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Polling failed: {ex.Message}", LogLevel.Error);
                    return false;
                }
            }, new RequestOptions<VoidStruct, VoidStruct>()
            {
                AutoStart = false,
                CancellationToken = token,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                NumberOfAttempts = Options.NumberOfAttempts,
                Priority = RequestPriority.High,
                Handler = Options.Handler
            });

            // Step 3: Download file - created dynamically after polling
            OwnRequest downloadRequestWrapper = new(async (t) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(handoffId) || string.IsNullOrEmpty(serverName))
                    {
                        LogAndAppendMessage("Cannot download - missing handoff ID or server name", LogLevel.Error);
                        return false;
                    }

                    string downloadUrl = $"https://{serverName}.lucida.to/api/fetch/request/{handoffId}/download";
                    LogAndAppendMessage($"Creating download request for: {downloadUrl}", LogLevel.Debug);

                    LoadRequest downloadRequest = new(downloadUrl, new LoadRequestOptions()
                    {
                        CancellationToken = t,
                        CreateSpeedReporter = true,
                        SpeedReporterTimeout = 1,
                        Priority = RequestPriority.Normal,
                        MaxBytesPerSecond = Options.MaxDownloadSpeed,
                        DelayBetweenAttemps = Options.DelayBetweenAttemps,
                        Filename = fileName,
                        DestinationPath = _destinationPath.FullPath,
                        Handler = Options.Handler,
                        DeleteFilesOnFailure = true,
                        RequestFailed = (_, __) => LogAndAppendMessage($"Download failed: {fileName}", LogLevel.Error),
                        WriteMode = WriteMode.AppendOrTruncate,
                    });

                    _trackContainer.Add(downloadRequest);
                    downloadRequest.Start();
                    await downloadRequest.Task;
                    
                    return downloadRequest.State == RequestState.Compleated;
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Download request failed: {ex.Message}", LogLevel.Error);
                    return false;
                }
            }, new RequestOptions<VoidStruct, VoidStruct>()
            {
                AutoStart = false,
                Priority = RequestPriority.High,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                Handler = Options.Handler,
                CancellationToken = token
            });

            // Step 4: Post-process
            OwnRequest postProcessRequest = new(async (t) =>
            {
                try
                {
                    return await PostProcessAsync(fileName, t);
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Post-processing failed: {ex.Message}", LogLevel.Error);
                    return false;
                }
            }, new RequestOptions<VoidStruct, VoidStruct>()
            {
                AutoStart = false,
                Priority = RequestPriority.High,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                Handler = Options.Handler,
                CancellationToken = token
            });

            // Chain requests
            initRequest.TrySetSubsequentRequest(pollRequest);
            pollRequest.TrySetSubsequentRequest(downloadRequestWrapper);
            downloadRequestWrapper.TrySetSubsequentRequest(postProcessRequest);

            // Add to containers
            _requestContainer.Add(initRequest);
            _requestContainer.Add(pollRequest);
            _requestContainer.Add(downloadRequestWrapper);
            _requestContainer.Add(postProcessRequest);
        }

        private LoadRequest CreateDownloadRequest(string handoffId, string serverName, string fileName)
        {
            string downloadUrl = $"https://{serverName}.lucida.to/api/fetch/request/{handoffId}/download";

            return new LoadRequest(downloadUrl, new LoadRequestOptions()
            {
                CancellationToken = Token,
                CreateSpeedReporter = true,
                SpeedReporterTimeout = 1,
                Priority = RequestPriority.Normal,
                MaxBytesPerSecond = Options.MaxDownloadSpeed,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                Filename = fileName,
                DestinationPath = _destinationPath.FullPath,
                Handler = Options.Handler,
                DeleteFilesOnFailure = true,
                RequestFailed = (_, __) => LogAndAppendMessage($"Download failed: {fileName}", LogLevel.Error),
                WriteMode = WriteMode.AppendOrTruncate,
            });
        }

        private async Task<(string handoffId, string serverName)> InitiateDownloadAsync(string url, string primaryToken, string fallbackToken, long expiry, CancellationToken token)
        {
            try
            {
                DownloadRequest request = DownloadRequest.CreateWithTokens(url, primaryToken, fallbackToken, expiry);
                string requestBody = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                // Use stream endpoint for both individual tracks and album tracks
                string apiEndpoint = "%2Fapi%2Ffetch%2Fstream%2Fv2";
                string requestUrl = $"{_httpClient.BaseUrl}/api/load?url={apiEndpoint}";
                LogAndAppendMessage($"Initiating track download - URL: {url}, Request URL: {requestUrl}", LogLevel.Debug);

                HttpRequestMessage httpRequest = new(HttpMethod.Post, requestUrl);
                httpRequest.Content = new StringContent(requestBody, Encoding.UTF8, "text/plain");
                httpRequest.Headers.Add("Origin", _httpClient.BaseUrl);
                httpRequest.Headers.Add("Referer", $"{_httpClient.BaseUrl}/?url={Uri.EscapeDataString(url)}");

                HttpResponseMessage response = await _httpClient.PostAsync(httpRequest);
                string responseContent = await response.Content.ReadAsStringAsync();

                LogAndAppendMessage($"Response status: {response.StatusCode}, Content: {responseContent.Substring(0, Math.Min(200, responseContent.Length))}...", LogLevel.Debug);

                response.EnsureSuccessStatusCode();

                DownloadResponse? downloadResponse = JsonSerializer.Deserialize<DownloadResponse>(responseContent, 
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                if (downloadResponse?.Success == true && !string.IsNullOrEmpty(downloadResponse.Handoff))
                {
                    string serverName = downloadResponse.Server ?? downloadResponse.Name ?? "hund";
                    LogAndAppendMessage($"Download initiated successfully - Handoff: {downloadResponse.Handoff}, Server: {serverName}", LogLevel.Debug);
                    return (downloadResponse.Handoff, serverName);
                }
                else
                {
                    string errorInfo = downloadResponse != null ? 
                        $"Success: {downloadResponse.Success}, Handoff: {downloadResponse.Handoff}, Server: {downloadResponse.Server}, Name: {downloadResponse.Name}, Error: {downloadResponse.Error}" :
                        "Failed to deserialize response";
                    LogAndAppendMessage($"Download initiation failed - {errorInfo}", LogLevel.Error);
                }

                throw new Exception($"Failed to initiate download - {downloadResponse?.Error ?? "no handoff ID received"}");
            }
            catch (Exception ex)
            {
                LogAndAppendMessage($"Error initiating download: {ex.Message}", LogLevel.Error);
                throw;
            }
        }

        private async Task<bool> PollForCompletionAsync(string handoffId, string serverName, CancellationToken token)
        {
            int maxAttempts = 30;
            int delayMs = 1000;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (token.IsCancellationRequested) return false;

                try
                {
                    string statusUrl = $"https://{serverName}.lucida.to/api/fetch/request/{handoffId}";

                    using HttpClient client = new();
                    HttpResponseMessage response = await client.GetAsync(statusUrl, token);
                    string responseContent = await response.Content.ReadAsStringAsync();

                    StatusResponse? status = JsonSerializer.Deserialize<StatusResponse>(responseContent,
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                    if (status?.Success == true)
                    {
                        if (status.Status == "completed")
                        {
                            LogAndAppendMessage("Download ready for retrieval", LogLevel.Debug);
                            return true;
                        }

                        LogAndAppendMessage($"Status: {status.Status}", LogLevel.Debug);
                    }
                    else if (!string.IsNullOrEmpty(status?.Error) && 
                             status.Error != "Request not found." && 
                             status.Error != "No such request")
                    {
                        throw new Exception($"Server error: {status.Error}");
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    LogAndAppendMessage($"Polling attempt {attempt} failed: {ex.Message}", LogLevel.Debug);
                }

                await Task.Delay(delayMs, token);
                delayMs = Math.Min(delayMs * 2, 5000);
            }

            throw new Exception("Download did not complete within expected time");
        }

        private async Task<bool> PostProcessAsync(string fileName, CancellationToken token)
        {
            try
            {
                string filePath = Path.Combine(_destinationPath.FullPath, fileName);
                
                if (!File.Exists(filePath))
                {
                    LogAndAppendMessage($"Downloaded file not found: {filePath}", LogLevel.Error);
                    return false;
                }

                LogAndAppendMessage($"Download completed: {fileName}", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                LogAndAppendMessage($"Post-processing failed: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private async Task<(string primaryToken, string fallbackToken, long expiry)> ExtractTokensAsync(string url, CancellationToken token)
        {
            try
            {
                string lucidaUrl = $"{_httpClient.BaseUrl}/?url={Uri.EscapeDataString(url)}";
                string html = await _httpClient.GetStringAsync(lucidaUrl);

                return ExtractTokensFromHtml(html);
            }
            catch (Exception ex)
            {
                LogAndAppendMessage($"Error extracting tokens: {ex.Message}", LogLevel.Error);
                throw;
            }
        }

        private (string primaryToken, string fallbackToken, long expiry) ExtractTokensFromHtml(string html)
        {
            string primaryToken = null;
            string fallbackToken = null;
            long expiry = 0;

            // Method 1: Try data attributes first (most reliable)
            Match csrfDataMatch = Regex.Match(html, @"data-csrf=""([^""]+)""");
            if (csrfDataMatch.Success)
            {
                primaryToken = csrfDataMatch.Groups[1].Value;
            }

            Match fallbackDataMatch = Regex.Match(html, @"data-csrffallback=""([^""]+)""");
            if (fallbackDataMatch.Success)
            {
                fallbackToken = fallbackDataMatch.Groups[1].Value;
            }

            // Method 2: Try JavaScript object notation if data attributes failed
            if (string.IsNullOrEmpty(primaryToken) || string.IsNullOrEmpty(fallbackToken))
            {
                Match csrfJsMatch = Regex.Match(html, @"""csrf""\s*:\s*""([^""]+)""");
                if (csrfJsMatch.Success)
                {
                    primaryToken = csrfJsMatch.Groups[1].Value;
                }

                Match fallbackJsMatch = Regex.Match(html, @"""csrfFallback""\s*:\s*""([^""]+)""");
                if (fallbackJsMatch.Success)
                {
                    fallbackToken = fallbackJsMatch.Groups[1].Value;
                }
            }

            // Method 3: Try generic "token" field if csrf fields not found
            if (string.IsNullOrEmpty(primaryToken))
            {
                Match tokenMatch = Regex.Match(html, @"""token""\s*:\s*""([^""]+)""");
                if (tokenMatch.Success)
                {
                    primaryToken = tokenMatch.Groups[1].Value;
                }
            }

            // Method 4: Try embedded JSON data extraction
            if (string.IsNullOrEmpty(primaryToken) || string.IsNullOrEmpty(fallbackToken))
            {
                try
                {
                    (string jsonPrimary, string jsonFallback, long jsonExpiry) = ExtractTokensFromEmbeddedJson(html);
                    if (!string.IsNullOrEmpty(jsonPrimary))
                    {
                        primaryToken = jsonPrimary;
                        fallbackToken = jsonFallback;
                        expiry = jsonExpiry;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "JSON token extraction failed, using regex results");
                }
            }

            // Use primary token as fallback if no explicit fallback found
            if (!string.IsNullOrEmpty(primaryToken) && string.IsNullOrEmpty(fallbackToken))
            {
                fallbackToken = primaryToken;
            }

            // Set default expiry if not found
            if (expiry == 0)
            {
                expiry = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            }

            if (!string.IsNullOrEmpty(primaryToken) && !string.IsNullOrEmpty(fallbackToken))
            {
                LogAndAppendMessage($"Successfully extracted tokens: Primary={primaryToken.Substring(0, Math.Min(8, primaryToken.Length))}...", LogLevel.Debug);
                return (primaryToken, fallbackToken, expiry);
            }

            // Log the first 500 characters of HTML for debugging
            string htmlPreview = html.Length > 500 ? html.Substring(0, 500) + "..." : html;
            LogAndAppendMessage($"Token extraction failed. HTML preview: {htmlPreview}", LogLevel.Debug);
            
            throw new Exception("Failed to extract valid authentication tokens from page");
        }

        private string NormalizeUrl(string url)
        {
            if (LucidaUrlRegex.IsMatch(url))
            {
                Uri uri = new(url);
                var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
                if (queryParams["url"] != null)
                {
                    url = queryParams["url"];
                }
            }
            return url;
        }

        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "Unknown";

            fileName = FileNameSanitizer.Replace(fileName, "_");
            fileName = Regex.Replace(fileName, @"_+", "_").Trim();

            if (fileName.Length > 100)
            {
                fileName = fileName.Substring(0, 97) + "...";
            }

            return fileName;
        }

        private (string primaryToken, string fallbackToken, long expiry) ExtractTokensFromEmbeddedJson(string html)
        {
            try
            {
                // Simple method - just return null to fall back to regex patterns
                return (null, null, 0);
            }
            catch
            {
                return (null, null, 0);
            }
        }

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

        private string GetDistinctMessages()
        {
            return string.Join(Environment.NewLine, _message.ToString()
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Distinct());
        }

        private long GetRemainingSize() => 
            Math.Max(_trackContainer.Sum(x => x.ContentLength), ReleaseInfo.Size) - _trackContainer.Sum(x => x.BytesDownloaded);

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

        protected override Task<RequestReturn> RunRequestAsync() => throw new NotImplementedException();
        public override void Start() => throw new NotImplementedException();
        public override void Pause() => throw new NotImplementedException();
    }
}
