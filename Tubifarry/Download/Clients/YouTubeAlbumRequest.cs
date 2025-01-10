using DownloadAssistant.Base;
using DownloadAssistant.Options;
using DownloadAssistant.Requests;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.YouTube;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using Requests;
using Requests.Options;
using System.Text;
using Tubifarry.Core;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models;
using YouTubeMusicAPI.Models.Info;
using YouTubeMusicAPI.Models.Streaming;

namespace Tubifarry.Download.Clients
{
    internal class YouTubeAlbumRequest : Request<YouTubeAlbumOptions, string, string>
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

        private DateTime _lastUpdateTime = DateTime.MinValue;
        private long _lastRemainingSize = 0;
        private byte[]? _albumCover;

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

        public YouTubeAlbumRequest(RemoteAlbum remoteAlbum, YouTubeAlbumOptions? options) : base(options)
        {
            Options.YouTubeMusicClient ??= new YouTubeMusicClient();
            _logger = NzbDroneLogger.GetLogger(this);
            _remoteAlbum = remoteAlbum;
            _albumData = remoteAlbum.Albums.FirstOrDefault() ?? new();
            _releaseFormatter = new(ReleaseInfo, remoteAlbum.Artist, Options.NameingConfig);
            _requestContainer.Add(_trackContainer);
            _destinationPath = new(Path.Combine(Options.DownloadPath, _releaseFormatter.BuildArtistFolderName(null), _releaseFormatter.BuildAlbumFilename("{Album Title}", new Album() { Title = ReleaseInfo.Title })));
            _clientItem = CreateClientItem();
            ProcessAlbum();
        }

        private void ProcessAlbum()
        {
            _requestContainer.Add(new OwnRequest(async (token) =>
            {
                await ProcessAlbumAsync(token);
                return true;
            }, new()
            {
                CancellationToken = Token,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                NumberOfAttempts = Options.NumberOfAttempts,
                Priority = RequestPriority.Low,
                Handler = Options.Handler
            }));
        }

        private async Task ProcessAlbumAsync(CancellationToken token)
        {
            string albumBrowseID = await Options.YouTubeMusicClient!.GetAlbumBrowseIdAsync(ReleaseInfo.DownloadUrl, token).ConfigureAwait(false);
            AlbumInfo albumInfo = await Options.YouTubeMusicClient.GetAlbumInfoAsync(albumBrowseID, token).ConfigureAwait(false);
            if (albumInfo?.Songs == null || !albumInfo.Songs.Any())
            {
                LogAndAppendMessage($"No tracks to download found in the album: {ReleaseInfo.Album}", LogLevel.Debug);
                return;
            }

            _albumCover = await TryDownloadCoverAsync(albumInfo, token).ConfigureAwait(false);

            foreach (AlbumSongInfo trackInfo in albumInfo.Songs)
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
                    AddTrackDownloadRequests(albumInfo, trackInfo, highestAudioStreamInfo);
                }
                catch (Exception ex)
                {
                    LogAndAppendMessage($"Failed to process track '{trackInfo.Name}' in album '{ReleaseInfo.Album}'", LogLevel.Error);
                    _logger?.Error(ex, $"Failed to process track '{trackInfo.Name}' in album '{ReleaseInfo.Album}'.");
                }
            }
        }

        private void LogAndAppendMessage(string message, LogLevel logLevel)
        {
            _message.AppendLine(message);
            _logger?.Log(logLevel, message);
        }

        public async Task<byte[]?> TryDownloadCoverAsync(AlbumInfo albumInfo, CancellationToken token)
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

        private void AddTrackDownloadRequests(AlbumInfo albumInfo, AlbumSongInfo trackInfo, AudioStreamInfo audioStreamInfo)
        {
            _albumData.Title = albumInfo.Name;
            Track musicInfo = new() { Title = trackInfo.Name, Artist = _remoteAlbum.Artist, Duration = (int)trackInfo.Duration.TotalSeconds, Explicit = trackInfo.IsExplicit, TrackNumber = trackInfo.SongNumber.ToString(), AbsoluteTrackNumber = trackInfo.SongNumber ?? 0 };
            LoadRequest downloadingReq = new(audioStreamInfo.Url, new LoadRequestOptions()
            {
                CancellationToken = Token,
                CreateSpeedReporter = true,
                SpeedReporterTimeout = 1,
                Priority = RequestPriority.Normal,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                Filename = _releaseFormatter.BuildTrackFilename(null, musicInfo, _albumData) + ".m4a",
                DestinationPath = _destinationPath.FullPath,
                Handler = Options.Handler,
                DeleteFilesOnFailure = true,
                Chunks = Options.Chunks,
                RequestFailed = (req, path) =>
                {
                    LogAndAppendMessage($"Downloading track '{trackInfo.Name}' in album '{albumInfo.Name}' failed.", LogLevel.Debug);
                },
                WriteMode = WriteMode.AppendOrTruncate,
            });

            OwnRequest postProcessReq = new((token) => SongDownloadCompletedAsync(albumInfo, trackInfo, downloadingReq, token), new()
            {
                AutoStart = false,
                Priority = RequestPriority.High,
                DelayBetweenAttemps = Options.DelayBetweenAttemps,
                Handler = Options.Handler,
                RequestFailed = (req, path) =>
                {
                    LogAndAppendMessage($"Post-processing for track '{trackInfo.Name}' in album '{albumInfo.Name}' failed.", LogLevel.Debug);
                    try
                    {
                        if (File.Exists(downloadingReq.Destination))
                            File.Delete(downloadingReq.Destination);
                    }
                    catch (Exception) { }
                },
                CancellationToken = Token
            });
            downloadingReq.TrySetSubsequentRequest(postProcessReq);

            _ = postProcessReq.TrySetIdle();

            _trackContainer.Add(downloadingReq);
            _requestContainer.Add(postProcessReq);
        }

        private async Task<bool> SongDownloadCompletedAsync(AlbumInfo albumInfo, AlbumSongInfo trackInfo, LoadRequest req, CancellationToken token)
        {
            string trackPath = req.Destination;
            if (!File.Exists(trackPath))
                return false;

            AudioMetadataHandler audioData = new(trackPath) { AlbumCover = _albumCover, UseID3v2_3 = Options.UseID3v2_3 };

            if (Options.TryIncludeLrc)
                audioData.Lyric = await Lyric.FetchLyricsFromLRCLIBAsync(Options.LRCLIBInstance, ReleaseInfo, trackInfo, token);

            AudioFormat format = AudioFormatHelper.ConvertOptionToAudioFormat(Options.ReEncodeOptions);

            if (Options.ReEncodeOptions == ReEncodeOptions.OnlyExtract)
                await audioData.TryExtractAudioFromVideoAsync();
            else if (format != AudioFormat.Unknown)
                await audioData.TryConvertToFormatAsync(format);

            if (!audioData.TryEmbedMetadata(albumInfo, trackInfo, ReleaseInfo))
                return false;

            if (Options.TryIncludeSycLrc)
                await audioData.TryCreateLrcFileAsync(token);
            GetRemainingTime();
            return true;
        }

        public DownloadClientItem CreateClientItem() => new()
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

                    if (remainingSeconds < 0)
                        return TimeSpan.FromSeconds(10);
                    return TimeSpan.FromSeconds(remainingSeconds);
                }
            }

            _lastUpdateTime = DateTime.UtcNow;
            _lastRemainingSize = remainingSize;
            return null;
        }

        private string GetDistinctMessages()
        {
            HashSet<string> distinctMessages = new();
            StringBuilder.ChunkEnumerator chunkEnumerator = _message.GetChunks();

            while (chunkEnumerator.MoveNext())
            {
                ReadOnlyMemory<char> chunk = chunkEnumerator.Current;
                distinctMessages.Add(chunk.ToString());
            }
            return string.Join("", distinctMessages);
        }

        private long GetRemainingSize() => Math.Max(_trackContainer.Sum(x => x.ContentLength), ReleaseInfo.Size) - _trackContainer.Sum(x => x.BytesDownloaded);

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