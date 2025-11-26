using DownloadAssistant.Options;
using DownloadAssistant.Requests;
using NLog;
using NLog.Fluent;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using Requests;
using Requests.Options;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;
using Tubifarry.Download.Base;
using Tubifarry.Indexers.SubSonic;

namespace Tubifarry.Download.Clients.SubSonic
{
    /// <summary>
    /// SubSonic download request handling track and album downloads
    /// </summary>
    public class SubSonicDownloadRequest : BaseDownloadRequest<SubSonicDownloadOptions>
    {
        private readonly BaseHttpClient _httpClient;
        private SubSonicAlbumFull? _currentAlbum;

        public SubSonicDownloadRequest(RemoteAlbum remoteAlbum, SubSonicDownloadOptions? options)
            : base(remoteAlbum, options)
        {
            _httpClient = new BaseHttpClient(
                Options.BaseUrl,
                Options.RequestInterceptors,
                TimeSpan.FromSeconds(Options.RequestTimeout));

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

        private async Task ProcessSingleTrackAsync(string songId, CancellationToken token)
        {
            _logger.Trace($"Processing single track with ID: {songId}");

            SubSonicSearchSong track = await GetSongAsync(songId, token);

            if (!string.IsNullOrEmpty(track.AlbumId))
                _currentAlbum = await TryGetAlbumAsync(track.AlbumId, token);
            await TryDownloadAlbumCoverAsync(track.CoverArt, token);

            QueueTrackDownload(track, token);
            _requestContainer.Add(_trackContainer);
        }

        private async Task ProcessAlbumAsync(string albumId, CancellationToken token)
        {
            _logger.Trace($"Processing album with ID: {albumId}");

            _currentAlbum = await GetAlbumAsync(albumId, token);

            if ((_currentAlbum.Songs?.Count ?? 0) == 0)
                throw new Exception("No tracks found in album");

            _expectedTrackCount = _currentAlbum.Songs!.Count;
            _logger.Trace($"Found {_expectedTrackCount} tracks in album: {_currentAlbum.Name}");

            await TryDownloadAlbumCoverAsync(_currentAlbum.CoverArt, token);

            for (int i = 0; i < _currentAlbum.Songs.Count; i++)
            {
                SubSonicSearchSong track = _currentAlbum.Songs[i];
                try
                {
                    QueueTrackDownload(track, token);
                    _logger.Trace($"Track {i + 1}/{_expectedTrackCount} queued: {track.Title}");
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Track {i + 1}/{_expectedTrackCount} failed: {track.Title} - {ex.Message}", LogLevel.Error);
                }
            }

            _requestContainer.Add(_trackContainer);
        }

        private async Task<SubSonicSearchSong> GetSongAsync(string songId, CancellationToken token)
        {
            try
            {
                string response = await ExecuteApiRequestAsync("getSong.view", songId, token);
                var responseWrapper = JsonSerializer.Deserialize<SubSonicSongResponseWrapper>(response, IndexerParserHelper.StandardJsonOptions);

                ValidateApiResponse(responseWrapper?.SubsonicResponse);

                if (responseWrapper!.SubsonicResponse!.Song == null)
                    throw new Exception("Song data not found in response");

                return responseWrapper.SubsonicResponse.Song;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get song details: {ex.Message}", ex);
            }
        }

        private async Task<SubSonicAlbumFull> GetAlbumAsync(string albumId, CancellationToken token)
        {
            try
            {
                string response = await ExecuteApiRequestAsync("getAlbum.view", albumId, token);
                var responseWrapper = JsonSerializer.Deserialize<SubSonicAlbumResponseWrapper>(response, IndexerParserHelper.StandardJsonOptions);

                ValidateApiResponse(responseWrapper?.SubsonicResponse);

                if (responseWrapper!.SubsonicResponse!.Album == null)
                    throw new Exception("Album data not found in response");

                return responseWrapper.SubsonicResponse.Album;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get album details: {ex.Message}", ex);
            }
        }

        private async Task<SubSonicAlbumFull?> TryGetAlbumAsync(string albumId, CancellationToken token)
        {
            try
            {
                return await GetAlbumAsync(albumId, token);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Failed to get album details for albumId: {albumId}");
                return null;
            }
        }

        private async Task TryDownloadAlbumCoverAsync(string? coverArtId, CancellationToken token)
        {
            if (string.IsNullOrEmpty(coverArtId))
                return;

            try
            {
                string coverUrl = BuildCoverArtUrl(coverArtId);
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

        private async Task<string> ExecuteApiRequestAsync(string endpoint, string id, CancellationToken token)
        {
            string url = BuildApiUrl(endpoint, id);

            using HttpRequestMessage request = _httpClient.CreateRequest(HttpMethod.Get, url);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync(token);
        }

        private static void ValidateApiResponse(SubSonicItemResponse? response)
        {
            if (response?.Status != "ok")
            {
                if (response?.Error != null)
                    throw new Exception($"SubSonic API error: {response.Error.Message ?? "Unknown error"}");
                throw new Exception("SubSonic API returned an error status");
            }
        }

        private string BuildApiUrl(string endpoint, string id)
        {
            var urlBuilder = new StringBuilder();
            urlBuilder.Append($"{Options.BaseUrl.TrimEnd('/')}/rest/{endpoint}");
            urlBuilder.Append($"?id={Uri.EscapeDataString(id)}");
            AppendStandardApiParameters(urlBuilder);
            return urlBuilder.ToString();
        }

        private string BuildStreamUrl(string songId)
        {
            var urlBuilder = new StringBuilder();
            urlBuilder.Append($"{Options.BaseUrl.TrimEnd('/')}/rest/stream.view");
            urlBuilder.Append($"?id={Uri.EscapeDataString(songId)}");
            AppendStandardApiParameters(urlBuilder);

            if (Options.MaxBitRate > 0)
                urlBuilder.Append($"&maxBitRate={Options.MaxBitRate}");

            if (Options.PreferredFormat != PreferredFormatEnum.Raw)
                urlBuilder.Append($"&format={Options.PreferredFormat.ToString().ToLower()}");

            return urlBuilder.ToString();
        }

        private string BuildCoverArtUrl(string coverArtId)
        {
            var urlBuilder = new StringBuilder();
            urlBuilder.Append($"{Options.BaseUrl.TrimEnd('/')}/rest/getCoverArt.view");
            urlBuilder.Append($"?id={Uri.EscapeDataString(coverArtId)}");
            AppendStandardApiParameters(urlBuilder);
            return urlBuilder.ToString();
        }

        private void AppendStandardApiParameters(StringBuilder urlBuilder)
        {
            SubSonicAuthHelper.AppendAuthParameters(urlBuilder, Options.Username, Options.Password, Options.UseTokenAuth);
            urlBuilder.Append("&f=json");
        }

        private void QueueTrackDownload(SubSonicSearchSong track, CancellationToken token)
        {
            string streamUrl = BuildStreamUrl(track.Id);
            Track trackMetadata = CreateTrackFromSubSonicData(track);
            Album albumMetadata = CreateAlbumFromSubSonicData(track, _currentAlbum);
            string fileName = BuildTrackFilename(trackMetadata, albumMetadata);

            LoadRequest downloadRequest = CreateDownloadRequest(streamUrl, fileName, token);
            OwnRequest postProcessRequest = CreatePostProcessRequest(track, downloadRequest, fileName, token);

            downloadRequest.TrySetSubsequentRequest(postProcessRequest);
            postProcessRequest.TrySetIdle();

            _trackContainer.Add(downloadRequest);
            _requestContainer.Add(postProcessRequest);
        }

        private LoadRequest CreateDownloadRequest(string streamUrl, string fileName, CancellationToken token) => new(streamUrl, new LoadRequestOptions()
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

        private OwnRequest CreatePostProcessRequest(SubSonicSearchSong track, LoadRequest downloadRequest, string fileName, CancellationToken token) => new(
            (t) => PostProcessTrackAsync(track, downloadRequest, t),
            new RequestOptions<VoidStruct, VoidStruct>()
            {
                AutoStart = false,
                Priority = RequestPriority.High,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                Handler = Options.Handler,
                CancellationToken = token,
                RequestFailed = (_, __) => LogAndAppendMessage($"Post-processing failed: {fileName}", LogLevel.Error)
            });

        private async Task<bool> PostProcessTrackAsync(SubSonicSearchSong trackInfo, LoadRequest request, CancellationToken token)
        {
            string trackPath = request.Destination;
            await Task.Delay(100, token);

            if (!File.Exists(trackPath))
            {
                _logger.Error($"Track file not found after download: {trackPath}");
                return false;
            }

            try
            {
                AudioMetadataHandler audioData = new(trackPath) { AlbumCover = _albumCover };
                Album album = CreateAlbumFromSubSonicData(trackInfo, _currentAlbum);
                Track track = CreateTrackFromSubSonicData(trackInfo);

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

        private Album CreateAlbumFromSubSonicData(SubSonicSearchSong track, SubSonicAlbumFull? albumInfo)
        {
            string albumTitle = albumInfo?.Name ?? track.DisplayAlbum ?? ReleaseInfo.Album ??
                                _remoteAlbum.Albums?.FirstOrDefault()?.Title ?? "Unknown Album";
            string artistName = albumInfo?.Artist ?? track.Artist ?? ReleaseInfo.Artist ??
                                _remoteAlbum.Artist?.Name ?? "Unknown Artist";

            DateTime releaseDate = DetermineReleaseDate(albumInfo, track);

            return new Album
            {
                Title = albumTitle,
                ReleaseDate = releaseDate,
                Artist = new LazyLoaded<Artist>(new Artist { Name = artistName }),
                AlbumReleases = new LazyLoaded<List<AlbumRelease>>(new List<AlbumRelease>
                {
                    new()
                    {
                        TrackCount = albumInfo?.SongCount ?? 0,
                        Title = albumTitle,
                    }
                }),
                Genres = _remoteAlbum.Albums?.FirstOrDefault()?.Genres,
            };
        }

        private DateTime DetermineReleaseDate(SubSonicAlbumFull? albumInfo, SubSonicSearchSong track)
        {
            int? year = albumInfo?.Year ?? track.Year;
            if (year > 1900 && year.Value <= DateTime.Now.Year)
                return new DateTime(year.Value, 1, 1);
            if (albumInfo?.Created != null)
                return albumInfo.Created.Value;
            return ReleaseInfo.PublishDate;
        }

        private static Track CreateTrackFromSubSonicData(SubSonicSearchSong track) => new()
        {
            Title = track.Title,
            TrackNumber = track.Track?.ToString() ?? "1",
            AbsoluteTrackNumber = track.Track ?? 1,
            Duration = track.Duration * 1000, // Convert seconds to milliseconds
            Artist = new LazyLoaded<Artist>(new Artist { Name = track.Artist })
        };
    }
}
