using DownloadAssistant.Options;
using DownloadAssistant.Requests;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using Requests;
using Requests.Options;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Download.Base;
using Tubifarry.Indexers.DABMusic;

namespace Tubifarry.Download.Clients.DABMusic
{
    /// <summary>
    /// DABMusic download request handling track and album downloads
    /// </summary>
    public class DABMusicDownloadRequest : BaseDownloadRequest<DABMusicDownloadOptions>
    {
        private readonly BaseHttpClient _httpClient;
        private readonly IDABMusicSessionManager _sessionManager;
        private DABMusicAlbum? _currentAlbum;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        };

        public DABMusicDownloadRequest(RemoteAlbum remoteAlbum, IDABMusicSessionManager sessionManager, DABMusicDownloadOptions? options) : base(remoteAlbum, options)
        {
            _httpClient = new BaseHttpClient(Options.BaseUrl, Options.RequestInterceptors, TimeSpan.FromSeconds(Options.RequestTimeout));
            _sessionManager = sessionManager;

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

        private async Task ProcessSingleTrackAsync(string trackId, CancellationToken token)
        {
            _logger.Trace($"Processing single track with ID: {trackId}");

            DABMusicTrack track = await GetTrackAsync(trackId, token);

            string streamUrl = await GetStreamUrlAsync(trackId, token);
            if (string.IsNullOrEmpty(streamUrl))
                throw new Exception("Failed to get stream URL for track");

            _currentAlbum = new DABMusicAlbum(
                Id: track.AlbumId ?? "unknown",
                Title: track.AlbumTitle ?? ReleaseInfo.Album,
                Artist: track.Artist,
                ArtistId: track.ArtistId,
                Cover: track.Cover,
                ReleaseDate: track.ReleaseDate,
                Genre: track.Genre,
                TrackCount: 1,
                Label: track.Label
            );

            await DownloadAlbumCoverAsync(_currentAlbum.Cover, token);

            Track trackMetadata = CreateTrackFromDABData(track, _currentAlbum);
            Album albumMetadata = CreateAlbumFromDABData(_currentAlbum);

            string fileName = BuildTrackFilename(trackMetadata, albumMetadata);
            InitiateDownload(streamUrl, fileName, track, token);
            _requestContainer.Add(_trackContainer);
        }

        private async Task ProcessAlbumAsync(string albumId, CancellationToken token)
        {
            _logger.Trace($"Processing album with ID: {albumId}");

            _currentAlbum = await GetAlbumAsync(albumId, token);
            if ((_currentAlbum.Tracks?.Count ?? 0) == 0)
                throw new Exception("No tracks found in album");

            _expectedTrackCount = _currentAlbum.Tracks!.Count;
            _logger.Trace($"Found {_currentAlbum.Tracks.Count} tracks in album: {_currentAlbum.Title}");

            await DownloadAlbumCoverAsync(_currentAlbum.Cover, token);

            for (int i = 0; i < _currentAlbum.Tracks.Count; i++)
            {
                DABMusicTrack track = _currentAlbum.Tracks[i];
                try
                {
                    string streamUrl = await GetStreamUrlAsync(track.Id, token);
                    if (string.IsNullOrEmpty(streamUrl))
                    {
                        _logger.Warn($"No stream URL available for track: {track.Title}");
                        continue;
                    }

                    Track trackMetadata = CreateTrackFromDABData(track, _currentAlbum);
                    Album albumMetadata = CreateAlbumFromDABData(_currentAlbum);

                    string trackFileName = BuildTrackFilename(trackMetadata, albumMetadata);
                    InitiateDownload(streamUrl, trackFileName, track, token);
                    _logger.Trace($"Track {i + 1}/{_currentAlbum.Tracks.Count} queued: {track.Title}");
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Track {i + 1}/{_currentAlbum.Tracks.Count} failed: {track.Title} - {ex.Message}", LogLevel.Error);
                }
            }
            _requestContainer.Add(_trackContainer);
        }

        private async Task<string> RequestAsync(string url, CancellationToken token)
        {
            using HttpRequestMessage request = _httpClient.CreateRequest(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(Options.Email) && !string.IsNullOrWhiteSpace(Options.Password))
            {
                DABMusicSession? session = _sessionManager.GetOrCreateSession(Options.BaseUrl, Options.Email, Options.Password);
                if (session?.IsValid == true)
                    request.Headers.Add("Cookie", session.SessionCookie);
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(token);
        }


        private async Task<DABMusicTrack> GetTrackAsync(string trackId, CancellationToken token)
        {
            try
            {
                string url = $"/api/track?trackId={Uri.EscapeDataString(trackId)}";
                string response = await RequestAsync(url, token);

                DABMusicTrack? track = JsonSerializer.Deserialize<DABMusicTrack>(response, JsonOptions);
                if (track != null)
                    return track;

                throw new Exception("Failed to parse track response");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get track details: {ex.Message}", ex);
            }
        }

        private async Task<DABMusicAlbum> GetAlbumAsync(string albumId, CancellationToken token)
        {
            try
            {
                string url = $"/api/album?albumId={Uri.EscapeDataString(albumId)}";
                string response = await RequestAsync(url, token);

                try
                {
                    DABMusicAlbumDetailsResponse? result = JsonSerializer.Deserialize<DABMusicAlbumDetailsResponse>(response, JsonOptions);
                    if (result?.Album != null)
                        return result.Album;
                }
                catch (JsonException)
                {
                    DABMusicAlbum? album = JsonSerializer.Deserialize<DABMusicAlbum>(response, JsonOptions);
                    if (album != null)
                        return album;
                }

                throw new Exception("Failed to parse album response");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get album details: {ex.Message}", ex);
            }
        }

        private async Task<string> GetStreamUrlAsync(string trackId, CancellationToken token)
        {
            try
            {
                string url = $"/api/stream?trackId={Uri.EscapeDataString(trackId)}";
                string response = await RequestAsync(url, token);

                DABMusicStreamResponse? result = JsonSerializer.Deserialize<DABMusicStreamResponse>(response, JsonOptions);
                return result?.Url ?? string.Empty;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get stream URL: {ex.Message}", ex);
            }
        }

        private async Task DownloadAlbumCoverAsync(string? coverUrl, CancellationToken token)
        {
            if (string.IsNullOrEmpty(coverUrl))
                return;

            try
            {
                HttpResponseMessage response = await _httpClient.GetAsync(coverUrl, token);
                response.EnsureSuccessStatusCode();

                _albumCover = await response.Content.ReadAsByteArrayAsync(token);
                _logger.Trace($"Downloaded album cover: {_albumCover.Length} bytes");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to download album cover");
                _albumCover = null;
            }
        }

        private void InitiateDownload(string streamUrl, string fileName, DABMusicTrack track, CancellationToken token)
        {
            LoadRequest downloadRequest = new(streamUrl, new LoadRequestOptions()
            {
                CancellationToken = token,
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

            OwnRequest postProcessRequest = new((t) => PostProcessTrackAsync(track, downloadRequest, t), new RequestOptions<VoidStruct, VoidStruct>()
            {
                AutoStart = false,
                Priority = RequestPriority.High,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                Handler = Options.Handler,
                CancellationToken = token,
                RequestFailed = (_, __) =>
                {
                    LogAndAppendMessage($"Post-processing failed: {fileName}", LogLevel.Error);
                    try
                    {
                        if (File.Exists(downloadRequest.Destination))
                            File.Delete(downloadRequest.Destination);
                    }
                    catch { }
                }
            });

            downloadRequest.TrySetSubsequentRequest(postProcessRequest);
            postProcessRequest.TrySetIdle();

            _trackContainer.Add(downloadRequest);
            _requestContainer.Add(postProcessRequest);
        }

        private async Task<bool> PostProcessTrackAsync(DABMusicTrack trackInfo, LoadRequest request, CancellationToken token)
        {
            string trackPath = request.Destination;
            await Task.Delay(100, token);

            if (!File.Exists(trackPath))
                return false;

            try
            {
                AudioMetadataHandler audioData = new(trackPath) { AlbumCover = _albumCover };

                Album album = CreateAlbumFromDABData(_currentAlbum);
                Track track = CreateTrackFromDABData(trackInfo, _currentAlbum);

                if (!audioData.TryEmbedMetadata(album, track))
                {
                    _logger.Warn($"Failed to embed metadata for: {Path.GetFileName(trackPath)}");
                    return false;
                }

                _logger.Trace($"Successfully processed track: {Path.GetFileName(trackPath)}");
                return true;
            }
            catch (Exception ex)
            {
                LogAndAppendMessage($"Post-processing failed for {Path.GetFileName(trackPath)}: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private Album CreateAlbumFromDABData(DABMusicAlbum? albumInfo)
        {
            string albumTitle = albumInfo?.Title ?? ReleaseInfo.Album ?? _remoteAlbum.Albums?.FirstOrDefault()?.Title ?? "Unknown Album";
            string artistName = albumInfo?.Artist ?? ReleaseInfo.Artist ?? _remoteAlbum.Artist?.Name ?? "Unknown Artist";

            DateTime releaseDate = ReleaseInfo.PublishDate;
            if (!string.IsNullOrEmpty(albumInfo?.ReleaseDate) && DateTime.TryParse(albumInfo.ReleaseDate, out DateTime parsedDate))
                releaseDate = parsedDate;

            return new Album
            {
                Title = albumTitle,
                ReleaseDate = releaseDate,
                Artist = new LazyLoaded<Artist>(new Artist
                {
                    Name = artistName,
                }),
                AlbumReleases = new LazyLoaded<List<AlbumRelease>>(new List<AlbumRelease>
                {
                    new() {
                        TrackCount = albumInfo?.TrackCount ?? 0,
                        Title = albumTitle,
                        Label = !string.IsNullOrEmpty(albumInfo?.Label)? new(){ albumInfo.Label } : new(),
                    }
                }),
                Genres = _remoteAlbum.Albums?.FirstOrDefault()?.Genres,
            };
        }

        private Track CreateTrackFromDABData(DABMusicTrack trackInfo, DABMusicAlbum? albumInfo) => new()
        {
            Title = trackInfo.Title,
            TrackNumber = trackInfo.TrackNumber.ToString(),
            AbsoluteTrackNumber = trackInfo.TrackNumber,
            Duration = trackInfo.Duration * 1000,
            Artist = new LazyLoaded<Artist>(new Artist
            {
                Name = trackInfo.Artist ?? albumInfo?.Artist ?? ReleaseInfo.Artist ?? _remoteAlbum.Artist?.Name,
            })
        };

    }
}