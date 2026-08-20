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
using Tubifarry.Core.Utilities;
using Tubifarry.Download.Base;
using Tubifarry.Download.Clients.YouTube;
using Tubifarry.Indexers.Invidious;

namespace Tubifarry.Download.Clients.Invidious
{
    public class InvidiousDownloadRequest : BaseDownloadRequest<InvidiousDownloadOptions>
    {
        private static readonly string[] TopicSuffixes = [" - Topic", " - Thema"];
        private static readonly string[] TitleSeparators = [" - ", " – ", " — "];

        private readonly BaseHttpClient _httpClient;

        public InvidiousDownloadRequest(RemoteAlbum remoteAlbum, InvidiousDownloadOptions? options) : base(remoteAlbum, options)
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
            string playlistUrl = $"/api/v1/playlists/{Options.ItemId}";
            string playlistResponse = await RequestAsync(playlistUrl, token);

            InvidiousPlaylistInfo? playlistInfo = JsonSerializer.Deserialize<InvidiousPlaylistInfo>(playlistResponse, IndexerParserHelper.StandardJsonOptions);
            if (playlistInfo?.Videos == null || playlistInfo.Videos.Count == 0)
            {
                LogAndAppendMessage($"No tracks found in playlist: {Options.ItemId}", LogLevel.Debug);
                return;
            }

            _expectedTrackCount = playlistInfo.Videos.Count;

            string localParam = Options.ProxyVideos ? "&local=true" : "";
            List<(int TrackNumber, InvidiousVideoInfo VideoInfo)> tracks = [];
            int trackNumber = 0;

            foreach (InvidiousPlaylistVideoDetail playlistVideo in playlistInfo.Videos)
            {
                trackNumber++;
                try
                {
                    string videoUrl = $"/api/v1/videos/{playlistVideo.VideoId}?fields=title,videoId,author,authorId,lengthSeconds,published,videoThumbnails,adaptiveFormats,musicTracks,genre{localParam}";
                    string videoResponse = await RequestAsync(videoUrl, token);

                    InvidiousVideoInfo? videoInfo = JsonSerializer.Deserialize<InvidiousVideoInfo>(videoResponse, IndexerParserHelper.StandardJsonOptions);
                    if (videoInfo == null)
                    {
                        LogAndAppendMessage($"Failed to get info for track {trackNumber}: {playlistVideo.Title}", LogLevel.Warn);
                        continue;
                    }

                    tracks.Add((trackNumber, videoInfo));
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Failed to process track {trackNumber}/{_expectedTrackCount}: {playlistVideo.Title}", LogLevel.Error);
                    _logger.Error(ex, "Failed to process playlist track: {Title}", playlistVideo.Title);
                }
            }

            DateTime albumReleaseDate = ResolveAlbumReleaseDate(tracks.Select(t => t.VideoInfo), ReleaseInfo.PublishDate);

            foreach ((int number, InvidiousVideoInfo videoInfo) in tracks)
            {
                try
                {
                    InvidiousAdaptiveFormat? bestAudio = videoInfo.BestAudioStream;
                    if (bestAudio == null)
                    {
                        LogAndAppendMessage($"No audio stream for track {number}: {videoInfo.Title}", LogLevel.Warn);
                        continue;
                    }

                    if (number == 1)
                        await DownloadCoverAsync(videoInfo, token);

                    Album album = CreateAlbumFromPlaylistInfo(playlistInfo, videoInfo, albumReleaseDate);
                    Track track = CreateTrackFromVideoInfo(videoInfo, number, album.Title);

                    string extension = DetermineFileExtension(bestAudio);
                    string fileName = BuildTrackFilename(track, album, extension);

                    string streamUrl = bestAudio.Url;
                    if (Options.ProxyVideos && !streamUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        streamUrl = Options.BaseUrl.TrimEnd('/') + streamUrl;

                    InitiateDownload(videoInfo, bestAudio, album, track, streamUrl, fileName, token);
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Failed to process track {number}/{_expectedTrackCount}: {videoInfo.Title}", LogLevel.Error);
                    _logger.Error(ex, "Failed to process playlist track: {Title}", videoInfo.Title);
                }
            }
        }

        private static string DetermineFileExtension(InvidiousAdaptiveFormat format)
        {
            if (!string.IsNullOrEmpty(format.Container))
            {
                return format.Container.ToLowerInvariant() switch
                {
                    "mp4" or "m4a" => ".m4a",
                    "webm" => ".webm",
                    _ => ".m4a"
                };
            }

            if (format.Type?.Contains("mp4", StringComparison.OrdinalIgnoreCase) == true)
                return ".m4a";
            if (format.Type?.Contains("webm", StringComparison.OrdinalIgnoreCase) == true)
                return ".webm";

            return ".m4a";
        }

        private void InitiateDownload(InvidiousVideoInfo videoInfo, InvidiousAdaptiveFormat audioFormat, Album album, Track track, string streamUrl, string fileName, CancellationToken token)
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

            OwnRequest postProcessRequest = new((t) => PostProcessTrackAsync(videoInfo, audioFormat, album, track, downloadRequest, t), new RequestOptions<VoidStruct, VoidStruct>()
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

        private async Task<bool> PostProcessTrackAsync(InvidiousVideoInfo videoInfo, InvidiousAdaptiveFormat audioFormat, Album album, Track track, LoadRequest request, CancellationToken token)
        {
            string trackPath = request.Destination;
            await Task.Delay(100, token);

            if (!File.Exists(trackPath))
                return false;

            try
            {
                if (Options.AudioProcessing == null)
                    return false;

                AudioFileContext audioFile = new(trackPath) { AlbumCover = _albumCover, UseID3v2_3 = Options.UseID3v2_3 };

                AudioFormat format = AudioFormatHelper.ConvertOptionToAudioFormat(Options.ReEncodeOptions);
                if (Options.ReEncodeOptions == ReEncodeOptions.OnlyExtract)
                    await Options.AudioProcessing.ExtractAudioFromVideoAsync(audioFile);
                else if (format != AudioFormat.Unknown)
                    await Options.AudioProcessing.ConvertToFormatAsync(audioFile, format);

                if (Options.UseSponsorBlock && !string.IsNullOrWhiteSpace(videoInfo.VideoId))
                    await new SponsorBlock(audioFile.FilePath, videoInfo.VideoId, Options.SponsorBlockApiEndpoint).LookupAndTrimAsync(token);

                if (!Options.AudioProcessing.EmbedMetadata(audioFile, album, track))
                {
                    _logger.Warn($"Failed to embed metadata for: {Path.GetFileName(audioFile.FilePath)}");
                    return false;
                }

                _logger.Trace($"Successfully processed track: {Path.GetFileName(audioFile.FilePath)}");
                return true;
            }
            catch (Exception ex)
            {
                LogAndAppendMessage($"Post-processing failed for {Path.GetFileName(trackPath)}: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private async Task DownloadCoverAsync(InvidiousVideoInfo videoInfo, CancellationToken token)
        {
            string? coverUrl = videoInfo.BestThumbnailUrl;
            if (string.IsNullOrEmpty(coverUrl))
                return;

            if (coverUrl.StartsWith('/'))
                coverUrl = Options.BaseUrl.TrimEnd('/') + coverUrl;

            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(coverUrl, token);
                response.EnsureSuccessStatusCode();
                _albumCover = await response.Content.ReadAsByteArrayAsync(token);
                _logger.Trace($"Downloaded cover: {_albumCover.Length} bytes");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to download cover art");
                _albumCover = null;
            }
        }

        private async Task<string> RequestAsync(string url, CancellationToken token) =>
            await _httpClient.GetStringAsync(url, token);

        private string ResolveArtist(InvidiousVideoInfo videoInfo)
        {
            string? artist = videoInfo.MusicTracks?.FirstOrDefault()?.Artist;
            if (string.IsNullOrWhiteSpace(artist))
                artist = StripTopicSuffix(videoInfo.Author);
            if (string.IsNullOrWhiteSpace(artist))
                artist = ReleaseInfo.Artist;
            return string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist;
        }

        private static string? StripTopicSuffix(string? author)
        {
            if (string.IsNullOrWhiteSpace(author))
                return author;

            foreach (string suffix in TopicSuffixes)
            {
                if (author.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return author[..^suffix.Length].TrimEnd();
            }
            return author;
        }

        private string ResolveTrackTitle(InvidiousVideoInfo videoInfo)
        {
            string? title = videoInfo.MusicTracks?.FirstOrDefault()?.Song;
            if (!string.IsNullOrWhiteSpace(title))
                return title;

            return StripArtistPrefix(videoInfo.Title, ResolveArtist(videoInfo));
        }

        private static string StripArtistPrefix(string title, string artist)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
                return title;

            foreach (string separator in TitleSeparators)
            {
                int index = title.IndexOf(separator, StringComparison.Ordinal);
                if (index <= 0)
                    continue;

                string left = title[..index].Trim();
                string right = title[(index + separator.Length)..].Trim();
                if (right.Length == 0)
                    continue;

                if (left.Equals(artist, StringComparison.OrdinalIgnoreCase) ||
                    left.StartsWith(artist + " ", StringComparison.OrdinalIgnoreCase))
                    return right;
            }
            return title;
        }

        private static DateTime ResolveAlbumReleaseDate(IEnumerable<InvidiousVideoInfo> videos, DateTime fallback)
        {
            long[] published = [.. videos.Select(v => v.Published).Where(p => p > 0)];
            if (published.Length == 0)
                return fallback;

            long dominant = published
                .GroupBy(p => DateTimeOffset.FromUnixTimeSeconds(p).UtcDateTime.Date)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .First()
                .Min();

            return DateTimeOffset.FromUnixTimeSeconds(dominant).UtcDateTime;
        }

        private Track CreateTrackFromVideoInfo(InvidiousVideoInfo videoInfo, int trackNumber, string albumTitle)
        {
            string trackTitle = ResolveTrackTitle(videoInfo);
            string artistName = ResolveArtist(videoInfo);

            return new Track
            {
                Title = trackTitle,
                TrackNumber = trackNumber.ToString(),
                AbsoluteTrackNumber = trackNumber,
                MediumNumber = 1,
                Duration = videoInfo.LengthSeconds * 1000,
                Artist = new LazyLoaded<Artist>(new Artist { Name = artistName })
            };
        }

        private Album CreateAlbumFromPlaylistInfo(InvidiousPlaylistInfo playlistInfo, InvidiousVideoInfo videoInfo, DateTime releaseDate)
        {
            InvidiousMusicTrack? musicTrack = videoInfo.MusicTracks?.FirstOrDefault();
            string albumTitle = musicTrack?.Album ?? playlistInfo.Title;
            string artistName = StripTopicSuffix(playlistInfo.Author) is { Length: > 0 } playlistArtist
                ? playlistArtist
                : ResolveArtist(videoInfo);

            return new Album
            {
                Title = albumTitle,
                ReleaseDate = releaseDate,
                Artist = new LazyLoaded<Artist>(new Artist { Name = artistName }),
                AlbumReleases = new LazyLoaded<List<AlbumRelease>>([
                    new() {
                        TrackCount = playlistInfo.VideoCount,
                        Title = albumTitle
                    }
                ]),
                Genres = !string.IsNullOrEmpty(videoInfo.Genre) ? [videoInfo.Genre] : []
            };
        }
    }
}
