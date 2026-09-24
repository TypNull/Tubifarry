using DownloadAssistant.Options;
using DownloadAssistant.Requests;
using NLog;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using Requests;
using Requests.Options;
using System.Text;
using System.Text.Json;
using Tubifarry.Core.Utilities;
using Tubifarry.Download.Base;
using Tubifarry.Indexers.Lucida;

namespace Tubifarry.Download.Clients.Lucida
{
    /// <summary>
    /// Lucida download request handling track and album downloads
    /// </summary>
    public class LucidaDownloadRequest : BaseDownloadRequest<LucidaDownloadOptions>
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly BaseHttpClient _httpClient;
        private readonly ILucidaRateLimiter? _rateLimiter;

        public LucidaDownloadRequest(RemoteAlbum remoteAlbum, LucidaDownloadOptions? options) : base(remoteAlbum, options)
        {
            _httpClient = new BaseHttpClient(Options.BaseUrl, Options.RequestInterceptors, TimeSpan.FromSeconds(Options.RequestTimeout));
            _rateLimiter = options?.RateLimiter;

            _requestContainer.Add(new OwnRequest(async (token) =>
            {
                try
                {
                    await ProcessDownloadAsync(token);
                    return true;
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Error processing download: {ex.Message}", LogLevel.Error);
                    throw;
                }
            }, new RequestOptions<VoidStruct, VoidStruct>()
            {
                CancellationToken = Token,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                NumberOfAttempts = Options.NumberOfAttempts,
                Priority = RequestPriority.Low,
                Handler = Options.Handler
            }));
        }

        protected override async Task ProcessDownloadAsync(CancellationToken token)
        {
            _logger.Trace($"Processing {(Options.IsTrack ? "track" : "album")}: {ReleaseInfo.Title}");

            if (Options.IsTrack)
                await ProcessSingleTrackAsync(Options.ItemId, token);
            else
                await ProcessAlbumAsync(Options.ItemId, token);
        }

        private async Task ProcessSingleTrackAsync(string downloadUrl, CancellationToken token)
        {
            LucidaTokens tokens = await LucidaTokenExtractor.ExtractTokensAsync(_httpClient, downloadUrl);

            if (!tokens.IsValid)
                throw new Exception("Failed to extract authentication tokens");

            Track trackMetadata = CreateTrackFromLucidaData(new LucidaTrackModel
            {
                Title = ReleaseInfo.Title,
                TrackNumber = 1,
                Artist = _remoteAlbum.Artist?.Name ?? "Unknown Artist",
                DurationMs = 0
            }, new LucidaAlbumModel
            {
                Title = ReleaseInfo.Album ?? ReleaseInfo.Title,
                Artist = _remoteAlbum.Artist?.Name ?? "Unknown Artist",
                ReleaseDate = ReleaseInfo.PublishDate.ToString("yyyy-MM-dd")
            });

            Album albumMetadata = CreateAlbumFromLucidaData(new LucidaAlbumModel
            {
                Title = ReleaseInfo.Album ?? ReleaseInfo.Title,
                Artist = _remoteAlbum.Artist?.Name ?? "Unknown Artist",
                ReleaseDate = ReleaseInfo.PublishDate.ToString("yyyy-MM-dd"),
                TrackCount = 1
            });

            string fileName = BuildTrackFilename(trackMetadata, albumMetadata, AudioFormatHelper.GetFileExtensionForCodec(_remoteAlbum.Release.Codec.ToLower()));
            InitiateDownload(downloadUrl, tokens.Primary, tokens.Fallback, tokens.Expiry, fileName, token);
            _requestContainer.Add(_trackContainer);
        }

        private async Task ProcessAlbumAsync(string downloadUrl, CancellationToken token)
        {
            LucidaAlbumModel album = await LucidaMetadataExtractor.ExtractAlbumMetadataAsync(_httpClient, downloadUrl);
            if (album.Tracks.Count == 0)
                throw new Exception($"No tracks found for album: {downloadUrl}");

            _expectedTrackCount = album.Tracks.Count;
            _logger.Trace($"Found {album.Tracks.Count} tracks in album: {album.Title}");

            for (int i = 0; i < album.Tracks.Count; i++)
            {
                LucidaTrackModel track = album.Tracks[i];

                Track trackMetadata = CreateTrackFromLucidaData(track, album);
                Album albumMetadata = CreateAlbumFromLucidaData(album);

                string trackFileName = BuildTrackFilename(trackMetadata, albumMetadata, AudioFormatHelper.GetFileExtensionForCodec(_remoteAlbum.Release.Codec.ToLower()));

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

        private void InitiateDownload(string url, string primaryToken, string fallbackToken, long expiry, string fileName, CancellationToken token)
        {
            OwnRequest downloadRequestWrapper = new(async (t) =>
            {
                LucidaInitiationResult initiation;
                try
                {
                    initiation = await InitiateDownloadWithRetryAsync(
                        url, primaryToken, fallbackToken, expiry, t);
                    _logger.Trace($"Initiation completed. Handoff: {initiation.HandoffId}, Server: {initiation.ServerName}");
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Initiation failed after retries: {ex.Message}", LogLevel.Error);
                    return false;
                }

                try
                {
                    if (!await PollForCompletionAsync(initiation.HandoffId, initiation.ServerName, t))
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
                    string downloadUrl = $"https://{initiation.ServerName}.{domain}/api/fetch/request/{initiation.HandoffId}/download";

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

        private async Task<LucidaInitiationResult> InitiateDownloadWithRetryAsync(string url, string primaryToken, string fallbackToken, long expiry, CancellationToken token)
        {
            if (_rateLimiter == null)
            {
                _logger.Trace("No rate limiter configured, using direct request");
                return await InitiateDownloadRequestAsync(url, primaryToken, fallbackToken, expiry, null, token);
            }

            int totalAttempts = 0;
            string? lastWorker = null;

            // Try different workers
            for (int workerRetry = 0; workerRetry < ILucidaRateLimiter.MAX_WORKER_RETRIES; workerRetry++)
            {
                token.ThrowIfCancellationRequested();

                string worker = await _rateLimiter.WaitForAvailableWorkerAsync(token);
                totalAttempts++;
                lastWorker = worker;

                _logger.Trace($"Attempt {totalAttempts}: Using worker '{worker}' (retry {workerRetry + 1}/{ILucidaRateLimiter.MAX_WORKER_RETRIES})");

                try
                {
                    LucidaInitiationResult result = await InitiateDownloadRequestAsync(url, primaryToken, fallbackToken, expiry, worker, token);
                    _rateLimiter.ReleaseWorker(worker);
                    _logger.Debug($"Download initiated successfully on worker '{worker}'");
                    return result;
                }
                catch (LucidaRateLimitException)
                {
                    _rateLimiter.ReleaseWorker(worker);
                    _rateLimiter.MarkWorkerRateLimited(worker);
                    _logger.Debug($"Worker '{worker}' rate limited, trying different worker");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _rateLimiter.ReleaseWorker(worker);
                    throw;
                }
            }

            // All workers rate limited wait
            _logger.Debug($"All workers rate limited, waiting {ILucidaRateLimiter.WAIT_ON_ALL_BLOCKED_MS}ms before retry");
            await Task.Delay(ILucidaRateLimiter.WAIT_ON_ALL_BLOCKED_MS, token);

            //Retry after waiting
            for (int waitRetry = 0; waitRetry < ILucidaRateLimiter.MAX_WAIT_RETRIES; waitRetry++)
            {
                token.ThrowIfCancellationRequested();

                string worker = await _rateLimiter.WaitForAvailableWorkerAsync(token);
                totalAttempts++;
                lastWorker = worker;

                _logger.Trace($"Attempt {totalAttempts}: After wait, using worker '{worker}' (wait retry {waitRetry + 1}/{ILucidaRateLimiter.MAX_WAIT_RETRIES})");

                try
                {
                    LucidaInitiationResult result = await InitiateDownloadRequestAsync(url, primaryToken, fallbackToken, expiry, worker, token);
                    _rateLimiter.ReleaseWorker(worker);
                    _logger.Debug($"Download initiated successfully on worker '{worker}' after wait");
                    return result;
                }
                catch (LucidaRateLimitException)
                {
                    _rateLimiter.ReleaseWorker(worker);
                    _rateLimiter.MarkWorkerRateLimited(worker);
                    _logger.Debug($"Worker '{worker}' still rate limited after wait");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _rateLimiter.ReleaseWorker(worker);
                    throw;
                }
            }

            throw new Exception($"Failed to initiate download after {totalAttempts} attempts across all workers (last worker: {lastWorker})");
        }

        private async Task<LucidaInitiationResult> InitiateDownloadRequestAsync(
            string url, string primaryToken, string fallbackToken, long expiry,
            string? forcedWorker, CancellationToken token)
        {
            try
            {
                LucidaDownloadRequestInfo request = LucidaDownloadRequestInfo.CreateWithTokens(url, primaryToken, fallbackToken, expiry);
                string requestBody = JsonSerializer.Serialize(request, _jsonOptions);

                string apiEndpoint = "%2Fapi%2Ffetch%2Fstream%2Fv2";
                if (!string.IsNullOrEmpty(forcedWorker))
                    apiEndpoint += $"&force={forcedWorker}";

                string requestUrl = $"{_httpClient.BaseUrl}/api/load?url={apiEndpoint}";
                _logger.Trace($"Initiating download from URL: {url}, Forced worker: {forcedWorker ?? "auto"}");

                HttpRequestMessage httpRequest = new(HttpMethod.Post, requestUrl);
                httpRequest.Headers.Add("Origin", _httpClient.BaseUrl);
                httpRequest.Headers.Add("Referer", $"{_httpClient.BaseUrl}/?url={Uri.EscapeDataString(url)}");
                httpRequest.Content = new StringContent(requestBody, Encoding.UTF8, "text/plain");

                HttpResponseMessage response = await _httpClient.PostAsync(httpRequest);
                string responseContent = await response.Content.ReadAsStringAsync(token);
                response.EnsureSuccessStatusCode();

                if (_rateLimiter?.IsRateLimitedResponse(responseContent) == true)
                    throw new LucidaRateLimitException($"Rate limited on worker '{forcedWorker}'", forcedWorker);

                LucidaDownloadResponse? downloadResponse = JsonSerializer.Deserialize<LucidaDownloadResponse>(responseContent, _jsonOptions);

                if (downloadResponse?.Success == true && !string.IsNullOrEmpty(downloadResponse.Handoff))
                {
                    string serverName = downloadResponse.Server ?? downloadResponse.Name ?? forcedWorker ?? "hund";

                    _rateLimiter?.EnsureWorkerRegistered(serverName);

                    return new LucidaInitiationResult
                    {
                        HandoffId = downloadResponse.Handoff,
                        ServerName = serverName
                    };
                }

                if (downloadResponse?.Error?.Contains("Service account is unavailable or disabled", StringComparison.OrdinalIgnoreCase) == true)
                    throw new LucidaRateLimitException($"Account unavailable on worker '{forcedWorker}': {downloadResponse.Error}", forcedWorker);

                string errorInfo = downloadResponse != null
                    ? $"Success: {downloadResponse.Success}, Handoff: {downloadResponse.Handoff}, Server: {downloadResponse.Server}, Name: {downloadResponse.Name}, Error: {downloadResponse.Error}"
                    : "Failed to deserialize response";
                throw new Exception($"Failed to initiate download: {errorInfo}");
            }
            catch (LucidaRateLimitException)
            {
                throw;
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
            int delayMs = 5000;
            int serviceUnavailableExtensions = 1;
            const int maxServiceUnavailableExtensions = 20;

            await Task.Delay(delayMs * 2, token);

            int totalAttempts = baseAttempts + (serviceUnavailableExtensions * 5);

            for (int attempt = 1; attempt <= totalAttempts; attempt++)
            {
                if (token.IsCancellationRequested)
                    return false;

                bool acquiredSlot = false;
                string? serverError = null;
                try
                {
                    if (_rateLimiter is not null)
                    {
                        await _rateLimiter.AcquirePollingSlotAsync(serverName, token);
                        acquiredSlot = true;
                    }

                    string statusUrl = $"https://{serverName}.{ExtractDomainFromUrl(Options.BaseUrl)}/api/fetch/request/{handoffId}";
                    string responseContent = await _httpClient.GetStringAsync(statusUrl, token);

                    if (_rateLimiter?.IsRateLimitedResponse(responseContent) == true)
                    {
                        _logger.Debug($"Polling rate limited on worker '{serverName}', treating as transient");
                    }
                    else
                    {
                        LucidaStatusResponse? status = JsonSerializer.Deserialize<LucidaStatusResponse>(responseContent, _jsonOptions);

                        if (status?.Success == true && status.Status == "completed")
                            return true;

                        if (!string.IsNullOrEmpty(status?.Error) && status.Error != "Request not found." && status.Error != "No such request")
                            serverError = status.Error;
                        else if (status?.Success == true && status.Status == "error")
                            serverError = status.Message ?? "unknown error";

                        if (!string.IsNullOrEmpty(status?.Status))
                            _logger.Trace($"Poll {attempt}: status={status.Status}");
                    }
                }
                catch (HttpRequestException httpEx) when (httpEx.StatusCode == System.Net.HttpStatusCode.InternalServerError)
                {
                    _logger.Error($"Polling failed with 500 Internal Server Error. Handoff ID may be invalid: {httpEx.Message}");
                    throw new Exception($"Server internal error. Handoff ID invalid: {httpEx.Message}");
                }
                catch (HttpRequestException httpEx) when (httpEx.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    if (attempt >= baseAttempts && serviceUnavailableExtensions < maxServiceUnavailableExtensions)
                    {
                        serviceUnavailableExtensions++;
                        totalAttempts = baseAttempts + (serviceUnavailableExtensions * 5);
                        _logger.Warn($"Service unavailable. Extending polling with 5-minute intervals (extension {serviceUnavailableExtensions}/{maxServiceUnavailableExtensions})");
                        await Task.Delay(TimeSpan.FromMinutes(5), token);
                        continue;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Trace($"Polling attempt {attempt} failed: {ex.Message}");
                }
                finally
                {
                    if (acquiredSlot)
                        _rateLimiter?.ReleasePollingSlot(serverName);
                }

                if (serverError != null)
                    throw new Exception($"Server error: {serverError}");

                await Task.Delay(delayMs, token);
                delayMs = Math.Min(delayMs * 2, 6000);
            }

            throw new Exception("Download did not complete within expected time");
        }

        private static string ExtractDomainFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return "lucida.to";
            return url.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        }

        private Album CreateAlbumFromLucidaData(LucidaAlbumModel? albumInfo)
        {
            DateTime releaseDate = ReleaseInfo.PublishDate;
            if (!string.IsNullOrEmpty(albumInfo?.ReleaseDate) && DateTime.TryParse(albumInfo.ReleaseDate, out DateTime parsedDate))
                releaseDate = parsedDate;

            return new Album
            {
                Title = albumInfo?.Title ?? ReleaseInfo.Album,
                ReleaseDate = releaseDate,
                Artist = new NzbDrone.Core.Datastore.LazyLoaded<NzbDrone.Core.Music.Artist>(new Artist
                {
                    Name = albumInfo?.Artist ?? ReleaseInfo.Artist,
                }),
                AlbumReleases = new NzbDrone.Core.Datastore.LazyLoaded<List<AlbumRelease>>(
                [
                    new() {
                        TrackCount = albumInfo?.TrackCount ?? 0,
                        Title = albumInfo?.Title ?? ReleaseInfo.Album,
                        Duration = (int)(albumInfo?.GetTotalDurationMs() ?? 0)
                    }
                ]),
                Genres = _remoteAlbum.Albums?.FirstOrDefault()?.Genres,
            };
        }

        private Track CreateTrackFromLucidaData(LucidaTrackModel trackInfo, LucidaAlbumModel? albumInfo) => new()
        {
            Title = trackInfo.Title,
            TrackNumber = trackInfo.TrackNumber.ToString(),
            AbsoluteTrackNumber = trackInfo.TrackNumber,
            Duration = (int)trackInfo.DurationMs,
            Explicit = trackInfo.IsExplicit,
            Artist = new NzbDrone.Core.Datastore.LazyLoaded<Artist>(new Artist
            {
                Name = trackInfo.Artist ?? albumInfo?.Artist ?? ReleaseInfo.Artist ?? _remoteAlbum.Artist?.Name,
            }),
            MediumNumber = trackInfo.DiscNumber
        };
    }
}
