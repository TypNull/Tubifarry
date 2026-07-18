using DownloadAssistant.Options;
using DownloadAssistant.Requests;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using Requests;
using Requests.Options;
using System.Text.Json;
using Tubifarry.Core.FFmpeg;
using Tubifarry.Core.Model;
using Tubifarry.Core.Records;
using Tubifarry.Core.Utilities;
using Tubifarry.Download.Base;
using Tubifarry.Indexers.TripleTriple;

namespace Tubifarry.Download.Clients.TripleTriple
{
    public class TripleTripleDownloadRequest : BaseDownloadRequest<TripleTripleDownloadOptions>
    {
        private readonly BaseHttpClient _httpClient;
        private TripleTripleAlbumInfo? _currentAlbum;
        private List<TripleTripleMediaResponse>? _mediaResponses;

        public TripleTripleDownloadRequest(RemoteAlbum remoteAlbum, TripleTripleDownloadOptions? options) : base(remoteAlbum, options)
        {
            _httpClient = new BaseHttpClient(Options.BaseUrl, Options.RequestInterceptors, TimeSpan.FromSeconds(Options.RequestTimeout));

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

            string asin = Options.ItemId.Split('/').Last();

            if (Options.IsTrack)
                await ProcessSingleTrackAsync(asin, token);
            else
                await ProcessAlbumAsync(asin, token);
        }

        private async Task ProcessSingleTrackAsync(string asin, CancellationToken token)
        {
            _logger.Trace($"Processing single track with ASIN: {asin}");

            TripleTripleMediaResponse? media = await GetMediaAsync(asin, token);

            if (media != null && media.Streamable && media.StreamInfo == null)
            {
                string? fallback = GetFallbackCodec(Options.Codec.ToString().ToLowerInvariant());
                if (fallback != null)
                {
                    _logger.Warn($"Codec '{Options.Codec}' unavailable for track '{asin}', trying '{fallback}'");
                    List<TripleTripleMediaResponse>? fallbackList = await GetAlbumMediaWithCodecAsync(asin, fallback, token);
                    media = fallbackList?.FirstOrDefault() ?? media;
                }
            }

            if (media == null || !media.Streamable || media.StreamInfo == null)
            {
                _logger.Warn($"No streams available for track '{asin}'. Requested codec not available.");
                _requestContainer.Cancel();
                return;
            }

            _mediaResponses = [media];

            string coverUrl = BuildCoverUrl(media.TemplateCoverUrl);
            await DownloadAlbumCoverAsync(coverUrl, token);

            Track trackMetadata = CreateTrackFromMedia(media);
            Album albumMetadata = CreateAlbumFromMedia(media);

            string extension = AudioFormatHelper.GetFileExtensionForFormat(AudioFormatHelper.GetAudioFormatFromCodec(media.StreamInfo.Codec));
            string fileName = BuildTrackFilename(trackMetadata, albumMetadata, extension);
            InitiateDownload(media, fileName, token);
        }

        private async Task ProcessAlbumAsync(string asin, CancellationToken token)
        {
            _logger.Trace($"Processing album with ASIN: {asin}");

            _currentAlbum = await GetAlbumMetadataAsync(asin, token);
            if (_currentAlbum == null || (_currentAlbum.Tracks?.Count ?? 0) == 0)
                throw new Exception("No tracks found in album");

            _expectedTrackCount = _currentAlbum.Tracks!.Count;
            _logger.Trace($"Found {_currentAlbum.Tracks.Count} tracks in album: {_currentAlbum.Title}");

            string coverUrl = BuildCoverUrl(_currentAlbum.Image);
            await DownloadAlbumCoverAsync(coverUrl, token);

            _mediaResponses = await GetAlbumMediaAsync(asin, token);
            if (_mediaResponses == null || _mediaResponses.Count == 0)
                throw new Exception("Failed to get media info for album");

            if (!_mediaResponses.Any(m => m.Streamable && m.StreamInfo != null))
            {
                string? fallback = GetFallbackCodec(Options.Codec.ToString().ToLowerInvariant());
                if (fallback != null)
                {
                    _logger.Warn($"Codec '{Options.Codec}' unavailable for '{_currentAlbum.Title}', trying '{fallback}'");
                    _mediaResponses = await GetAlbumMediaWithCodecAsync(asin, fallback, token) ?? _mediaResponses;
                }
            }

            foreach (TripleTripleTrackInfo track in _currentAlbum.Tracks)
            {
                try
                {
                    TripleTripleMediaResponse? media = _mediaResponses.FirstOrDefault(m => m.Asin == track.Asin);
                    if (media == null || !media.Streamable || media.StreamInfo == null)
                    {
                        string reason = media == null ? "not found in media response" :
                                       !media.Streamable ? "not streamable in your region" :
                                       "missing stream info";
                        _logger.Debug($"Skipping track '{track.Title}': {reason}");
                        continue;
                    }

                    Track trackMetadata = CreateTrackFromAlbum(track, _currentAlbum);
                    Album albumMetadata = CreateAlbumFromMetadata(_currentAlbum);

                    string extension = AudioFormatHelper.GetFileExtensionForFormat(AudioFormatHelper.GetAudioFormatFromCodec(media.StreamInfo.Codec));
                    string trackFileName = BuildTrackFilename(trackMetadata, albumMetadata, extension);
                    InitiateDownload(media, trackFileName, token);
                    _logger.Trace($"Track queued: {track.Title}");
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Track failed: {track.Title} - {ex.Message}", LogLevel.Error);
                }
            }

            if (_trackContainer.Count == 0)
            {
                _logger.Warn($"No downloadable tracks for album '{_currentAlbum.Title}'. Requested codec not available.");
                _requestContainer.Cancel();
            }
        }

        private async Task<TripleTripleMediaResponse?> GetMediaAsync(string asin, CancellationToken token)
        {
            try
            {
                string codec = Options.Codec.ToString().ToLowerInvariant();
                string url = $"/api/amazon-music/media-from-asin?asin={asin}&country={Options.CountryCode}&codec={codec}";
                string response = await RequestAsync(url, token);

                List<TripleTripleMediaResponse>? mediaList = JsonSerializer.Deserialize<List<TripleTripleMediaResponse>>(response, IndexerParserHelper.StandardJsonOptions);
                return mediaList?.FirstOrDefault();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get media info: {ex.Message}", ex);
            }
        }

        private async Task<TripleTripleAlbumInfo?> GetAlbumMetadataAsync(string asin, CancellationToken token)
        {
            try
            {
                string url = $"/api/amazon-music/metadata?asin={asin}&country={Options.CountryCode}";
                string response = await RequestAsync(url, token);

                TripleTripleAlbumMetadata? metadata = JsonSerializer.Deserialize<TripleTripleAlbumMetadata>(response, IndexerParserHelper.StandardJsonOptions);
                return metadata?.AlbumList?.FirstOrDefault();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get album metadata: {ex.Message}", ex);
            }
        }

        private async Task<List<TripleTripleMediaResponse>?> GetAlbumMediaAsync(string asin, CancellationToken token)
        {
            try
            {
                string codec = Options.Codec.ToString().ToLowerInvariant();
                string url = $"/api/amazon-music/media-from-asin?asin={asin}&country={Options.CountryCode}&codec={codec}";
                string response = await RequestAsync(url, token);

                return JsonSerializer.Deserialize<List<TripleTripleMediaResponse>>(response, IndexerParserHelper.StandardJsonOptions);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get album media: {ex.Message}", ex);
            }
        }

        private async Task<List<TripleTripleMediaResponse>?> GetAlbumMediaWithCodecAsync(string asin, string codec, CancellationToken token)
        {
            string url = $"/api/amazon-music/media-from-asin?asin={asin}&country={Options.CountryCode}&codec={codec}";
            string response = await RequestAsync(url, token);
            return JsonSerializer.Deserialize<List<TripleTripleMediaResponse>>(response, IndexerParserHelper.StandardJsonOptions);
        }

        private static string? GetFallbackCodec(string codec) => codec switch
        {
            "flac" => "opus",
            "eac3" => "opus",
            _ => null
        };

        private async Task<string> RequestAsync(string url, CancellationToken token)
        {
            using HttpRequestMessage request = _httpClient.CreateRequest(HttpMethod.Get, url);
            request.Headers.Add("Referer", Options.BaseUrl);

            using HttpResponseMessage response = await _httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(token);
        }

        private async Task DownloadAlbumCoverAsync(string? coverUrl, CancellationToken token)
        {
            if (string.IsNullOrEmpty(coverUrl))
                return;

            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(coverUrl, token);
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

        private string BuildCoverUrl(string? template)
        {
            if (string.IsNullOrEmpty(template))
                return string.Empty;

            return template
                .Replace("{size}", Options.CoverSize.ToString())
                .Replace("{jpegQuality}", "90")
                .Replace("{format}", "jpg");
        }

        private void InitiateDownload(TripleTripleMediaResponse media, string fileName, CancellationToken token)
        {
            LoadRequest downloadRequest = new(media.StreamInfo!.StreamUrl, new LoadRequestOptions()
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

            OwnRequest postProcessRequest = new((t) => PostProcessTrackAsync(media, downloadRequest, t), new RequestOptions<VoidStruct, VoidStruct>()
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

        private async Task<bool> PostProcessTrackAsync(TripleTripleMediaResponse media, LoadRequest request, CancellationToken token)
        {
            string trackPath = request.Destination;
            await Task.Delay(100, token);

            if (!File.Exists(trackPath))
                return false;

            try
            {
                if (Options.AudioProcessing == null)
                    return false;

                AudioFileContext audioFile = new(trackPath) { AlbumCover = _albumCover };

                if (!string.IsNullOrEmpty(media.DecryptionKey))
                {
                    if (!await Options.AudioProcessing.DecryptAsync(audioFile, media.DecryptionKey, media.StreamInfo?.Codec, token))
                    {
                        _logger.Error($"Failed to decrypt track: {Path.GetFileName(trackPath)}");
                        return false;
                    }
                }

                Album album = _currentAlbum != null ? CreateAlbumFromMetadata(_currentAlbum) : CreateAlbumFromMedia(media);
                Track track = CreateTrackFromMedia(media);

                if (Options.DownloadLyrics)
                {
                    string? syncedLyrics = media.Lyrics?.Synced ?? media.Tags?.PlainLyrics;
                    if (!string.IsNullOrEmpty(syncedLyrics))
                        audioFile.Lyric = ParseSyncedLyrics(syncedLyrics);
                }

                if (!Options.AudioProcessing.EmbedMetadata(audioFile, album, track))
                {
                    _logger.Warn($"Failed to embed metadata for: {Path.GetFileName(audioFile.FilePath)}");
                    return false;
                }

                if (Options.CreateLrcFile && audioFile.Lyric != null)
                    await Options.AudioProcessing.CreateLrcFileAsync(audioFile, token);

                _logger.Trace($"Successfully processed track: {Path.GetFileName(audioFile.FilePath)}");
                return true;
            }
            catch (Exception ex)
            {
                LogAndAppendMessage($"Post-processing failed for {Path.GetFileName(trackPath)}: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private static Lyric? ParseSyncedLyrics(string syncedLyrics)
        {
            if (string.IsNullOrEmpty(syncedLyrics))
                return null;

            return new Metadata.Lyrics.Converters.LrcConverter().Read(syncedLyrics);
        }

        private Album CreateAlbumFromMedia(TripleTripleMediaResponse media) => new()
        {
            Title = media.Tags?.Album ?? ReleaseInfo.Album ?? "Unknown Album",
            ReleaseDate = DateTime.TryParse(media.Tags?.Date, out DateTime date) ? date : ReleaseInfo.PublishDate,
            Artist = new LazyLoaded<Artist>(new Artist { Name = media.Tags?.AlbumArtist ?? media.Tags?.Artist ?? ReleaseInfo.Artist ?? "Unknown Artist" }),
            AlbumReleases = new LazyLoaded<List<AlbumRelease>>([
                new() {
                    TrackCount = media.Tags?.TrackTotal ?? 1,
                    Title = media.Tags?.Album ?? "Unknown Album",
                    Label = !string.IsNullOrEmpty(media.Tags?.Label) ? [media.Tags.Label] : [],
                }
            ]),
            Genres = !string.IsNullOrEmpty(media.Tags?.Genre) ? [media.Tags.Genre] : []
        };

        private Album CreateAlbumFromMetadata(TripleTripleAlbumInfo albumInfo) => new()
        {
            Title = albumInfo.Title,
            ReleaseDate = DateTimeOffset.FromUnixTimeMilliseconds(albumInfo.OriginalReleaseDate).DateTime,
            Artist = new LazyLoaded<Artist>(new Artist { Name = albumInfo.Artist.Name }),
            AlbumReleases = new LazyLoaded<List<AlbumRelease>>([
                new() {
                    TrackCount = albumInfo.TrackCount,
                    Title = albumInfo.Title,
                    Label = !string.IsNullOrEmpty(albumInfo.Label) ? [albumInfo.Label] : [],
                }
            ]),
            Genres = !string.IsNullOrEmpty(albumInfo.PrimaryGenreName) ? [albumInfo.PrimaryGenreName] : []
        };

        private Track CreateTrackFromMedia(TripleTripleMediaResponse media) => new()
        {
            Title = media.Tags?.Title ?? "Unknown Track",
            TrackNumber = media.Tags?.Track.ToString() ?? "1",
            AbsoluteTrackNumber = media.Tags?.Track ?? 1,
            MediumNumber = media.Tags?.Disc ?? 1,
            Artist = new LazyLoaded<Artist>(new Artist { Name = media.Tags?.Artist ?? "Unknown Artist" }),
            ForeignRecordingId = media.Tags?.Isrc
        };

        private Track CreateTrackFromAlbum(TripleTripleTrackInfo track, TripleTripleAlbumInfo album) => new()
        {
            Title = track.Title,
            TrackNumber = track.TrackNum.ToString(),
            AbsoluteTrackNumber = track.TrackNum,
            MediumNumber = 1,
            Duration = track.Duration * 1000,
            Artist = new LazyLoaded<Artist>(new Artist { Name = album.Artist.Name }),
            ForeignRecordingId = track.Isrc
        };
    }
}
