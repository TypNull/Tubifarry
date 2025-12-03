using DownloadAssistant.Base;
using DownloadAssistant.Options;
using DownloadAssistant.Requests;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using Requests;
using Requests.Options;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;
using Tubifarry.Download.Base;
using YouTubeMusicAPI.Models;
using YouTubeMusicAPI.Models.Info;
using YouTubeMusicAPI.Models.Streaming;

namespace Tubifarry.Download.Clients.YouTube
{
    /// <summary>
    /// YouTube download request handling album downloads.
    /// </summary>
    public class YouTubeDownloadRequest : BaseDownloadRequest<YouTubeDownloadOptions>
    {
        public YouTubeDownloadRequest(RemoteAlbum remoteAlbum, YouTubeDownloadOptions? options) : base(remoteAlbum, options)
        {
            Options.YouTubeMusicClient ??= TrustedSessionHelper.CreateAuthenticatedClientAsync().Result;

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
            _logger.Trace($"Processing YouTube album: {ReleaseInfo.Title}");
            await ProcessAlbumAsync(Options.ItemId, token);
        }

        private async Task ProcessAlbumAsync(string downloadUrl, CancellationToken token)
        {
            string albumBrowseID = await Options.YouTubeMusicClient!.GetAlbumBrowseIdAsync(downloadUrl, token).ConfigureAwait(false);
            AlbumInfo albumInfo = await Options.YouTubeMusicClient.GetAlbumInfoAsync(albumBrowseID, token).ConfigureAwait(false);

            await ApplyRandomDelayAsync(token);

            if (albumInfo?.Songs == null || albumInfo.Songs.Length == 0)
            {
                LogAndAppendMessage($"No tracks to download found in the album: {ReleaseInfo.Album}", LogLevel.Debug);
                return;
            }

            _expectedTrackCount = albumInfo.Songs.Length;
            _albumCover = await TryDownloadCoverAsync(albumInfo, token).ConfigureAwait(false);

            foreach (AlbumSong trackInfo in albumInfo.Songs)
            {
                if (trackInfo.Id == null)
                {
                    LogAndAppendMessage($"Skipping track '{trackInfo.Name}' in album '{ReleaseInfo.Album}' because it has no valid download URL.", LogLevel.Debug);
                    continue;
                }

                try
                {
                    StreamingData streamingData = await Options.YouTubeMusicClient.GetStreamingDataAsync(trackInfo.Id, token).ConfigureAwait(false);
                    AudioStreamInfo? highestAudioStreamInfo = streamingData.StreamInfo.OfType<AudioStreamInfo>().OrderByDescending(info => info.Bitrate).FirstOrDefault();

                    if (highestAudioStreamInfo == null)
                    {
                        LogAndAppendMessage($"Skipping track '{trackInfo.Name}' in album '{ReleaseInfo.Album}' because no audio stream was found.", LogLevel.Debug);
                        continue;
                    }

                    AddTrackDownloadRequest(albumInfo, trackInfo, highestAudioStreamInfo, token);
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Failed to process track '{trackInfo.Name}' in album '{ReleaseInfo.Album}'", LogLevel.Error);
                    _logger?.Error(ex, $"Failed to process track '{trackInfo.Name}' in album '{ReleaseInfo.Album}'.");
                }
            }

            _requestContainer.Add(_trackContainer);
        }

        private void AddTrackDownloadRequest(AlbumInfo albumInfo, AlbumSong trackInfo, AudioStreamInfo audioStreamInfo, CancellationToken token)
        {
            _albumData.Title = albumInfo.Name;

            string fileName = BuildTrackFilename(CreateTrackFromYouTubeData(trackInfo, albumInfo), _albumData, ".m4a");

            LoadRequest downloadRequest = new(audioStreamInfo.Url, new LoadRequestOptions()
            {
                CancellationToken = token,
                CreateSpeedReporter = true,
                SpeedReporterTimeout = 1000,
                Priority = RequestPriority.Normal,
                MaxBytesPerSecond = Options.MaxDownloadSpeed,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                Filename = fileName,
                DestinationPath = _destinationPath.FullPath,
                Handler = Options.Handler,
                NumberOfAttempts = 3,
                DeleteFilesOnFailure = true,
                Chunks = Options.Chunks,
                RequestFailed = (_, __) => LogAndAppendMessage($"Downloading track '{trackInfo.Name}' in album '{albumInfo.Name}' failed.", LogLevel.Debug),
                WriteMode = WriteMode.AppendOrTruncate,
                AutoStart = true
            });

            // Add post-processing
            OwnRequest postProcessRequest = new((t) => PostProcessTrackAsync(albumInfo, trackInfo, downloadRequest, t), new RequestOptions<VoidStruct, VoidStruct>()
            {
                AutoStart = false,
                Priority = RequestPriority.High,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                Handler = Options.Handler,
                RequestFailed = (_, __) =>
                {
                    LogAndAppendMessage($"Post-processing for track '{trackInfo.Name}' in album '{albumInfo.Name}' failed.", LogLevel.Debug);
                    try
                    {
                        if (File.Exists(downloadRequest.Destination))
                            File.Delete(downloadRequest.Destination);
                    }
                    catch { }
                },
                CancellationToken = token
            });

            downloadRequest.TrySetSubsequentRequest(postProcessRequest);
            postProcessRequest.TrySetIdle();

            _trackContainer.Add(downloadRequest);
            _requestContainer.Add(postProcessRequest);
        }

        private async Task<bool> PostProcessTrackAsync(AlbumInfo albumInfo, AlbumSong trackInfo, LoadRequest request, CancellationToken token)
        {
            string trackPath = request.Destination;
            await Task.Delay(100, token);

            if (!File.Exists(trackPath))
                return false;

            try
            {
                AudioMetadataHandler audioData = new(trackPath) { AlbumCover = _albumCover, UseID3v2_3 = Options.UseID3v2_3 };

                AudioFormat format = AudioFormatHelper.ConvertOptionToAudioFormat(Options.ReEncodeOptions);

                if (Options.ReEncodeOptions == ReEncodeOptions.OnlyExtract)
                    await audioData.TryExtractAudioFromVideoAsync();
                else if (format != AudioFormat.Unknown)
                    await audioData.TryConvertToFormatAsync(format);

                if (Options.UseSponsorBlock && !string.IsNullOrWhiteSpace(trackInfo.Id))
                    await new SponsorBlock(audioData.TrackPath, trackInfo.Id, Options.SponsorBlockApiEndpoint).LookupAndTrimAsync(token);
                trackPath = audioData.TrackPath;
                Album album = CreateAlbumFromYouTubeData(albumInfo);
                Track track = CreateTrackFromYouTubeData(trackInfo, albumInfo);

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

        private Album CreateAlbumFromYouTubeData(AlbumInfo albumInfo) => new()
        {
            Title = albumInfo.Name ?? ReleaseInfo.Album,
            ReleaseDate = albumInfo.ReleaseYear > 0 ? new DateTime(albumInfo.ReleaseYear, 1, 1) : ReleaseInfo.PublishDate,
            Artist = new LazyLoaded<Artist>(new Artist
            {
                Name = albumInfo.Artists?.FirstOrDefault()?.Name ?? ReleaseInfo.Artist,
            }),
            AlbumReleases = new LazyLoaded<List<AlbumRelease>>([ new() {
                    TrackCount = albumInfo.SongCount,
                    Title =  albumInfo.Name ?? ReleaseInfo.Album,
                    Duration = (int)albumInfo.Duration.TotalMilliseconds
                } ]),
            Genres = _remoteAlbum.Albums.FirstOrDefault()?.Genres
        };

        private Track CreateTrackFromYouTubeData(AlbumSong trackInfo, AlbumInfo albumInfo) => new()
        {
            Title = trackInfo.Name,
            AbsoluteTrackNumber = trackInfo.SongNumber ?? 0,
            TrackNumber = (trackInfo.SongNumber ?? 0).ToString(),
            Duration = (int)trackInfo.Duration.TotalMilliseconds,
            Explicit = trackInfo.IsExplicit,
            Artist = new LazyLoaded<Artist>(new Artist
            {
                Name = albumInfo.Artists?.FirstOrDefault()?.Name ?? ReleaseInfo.Artist,
            })
        };

        private async Task<byte[]?> TryDownloadCoverAsync(AlbumInfo albumInfo, CancellationToken token)
        {
            try
            {
                Thumbnail? bestThumbnail = albumInfo.Thumbnails.OrderByDescending(x => x.Height * x.Width).FirstOrDefault();
                int[] releaseResolution = ReleaseInfo.Resolution.Split('x').Select(int.Parse).ToArray();
                int releaseArea = releaseResolution[0] * releaseResolution[1];
                int albumArea = (bestThumbnail?.Height ?? 0) * (bestThumbnail?.Width ?? 0);

                string coverUrl = albumArea > releaseArea ? bestThumbnail?.Url ?? ReleaseInfo.Source : ReleaseInfo.Source;

                using HttpResponseMessage response = await HttpGet.HttpClient.GetAsync(coverUrl, token);
                if (!response.IsSuccessStatusCode)
                {
                    LogAndAppendMessage($"Failed to download cover art for album '{albumInfo.Name}'. Status code: {response.StatusCode}.", LogLevel.Debug);
                    return null;
                }

                return await response.Content.ReadAsByteArrayAsync(token);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to download album cover");
                return null;
            }
        }

        private async Task ApplyRandomDelayAsync(CancellationToken token)
        {
            if (Options.RandomDelayMin > 0 && Options.RandomDelayMax > 0)
            {
                int delay = new Random().Next(Options.RandomDelayMin, Options.RandomDelayMax);
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
    }
}