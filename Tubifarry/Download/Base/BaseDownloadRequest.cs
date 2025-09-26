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
using System.Text.RegularExpressions;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Download.Base
{
    /// <summary>
    /// Base class for download requests containing common functionality
    /// </summary>
    public abstract class BaseDownloadRequest<TOptions> : Request<TOptions, string, string> where TOptions : BaseDownloadOptions, new()
    {
        protected readonly OsPath _destinationPath;
        protected readonly StringBuilder _message = new();
        protected readonly RequestContainer<IRequest> _requestContainer = new();
        protected readonly RequestContainer<LoadRequest> _trackContainer = new();
        protected readonly RemoteAlbum _remoteAlbum;
        protected readonly Album _albumData;
        protected readonly DownloadClientItem _clientItem;
        protected readonly ReleaseFormatter _releaseFormatter;
        protected readonly Logger _logger;
        protected int _expectedTrackCount;
        protected byte[]? _albumCover;

        // Regex pattern for sanitizing filenames
        protected static readonly Regex FileNameSanitizer = new(@"[\\/:\*\?""<>\|]", RegexOptions.Compiled);

        // Progress tracking
        private DateTime _lastUpdateTime = DateTime.MinValue;
        private long _lastRemainingSize;


        protected ReleaseInfo ReleaseInfo => _remoteAlbum.Release;
        public override Task Task => _requestContainer.Task;
        public override RequestState State => _requestContainer.State;
        public string ID { get; } = Guid.NewGuid().ToString();

        public virtual DownloadClientItem ClientItem
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

        protected BaseDownloadRequest(RemoteAlbum remoteAlbum, TOptions? options) : base(options)
        {
            _logger = NzbDroneLogger.GetLogger(this);
            _remoteAlbum = remoteAlbum;
            _albumData = remoteAlbum.Albums.FirstOrDefault() ?? new Album();
            _releaseFormatter = new ReleaseFormatter(ReleaseInfo, remoteAlbum.Artist, Options.NamingConfig);
            _requestContainer.Add(_trackContainer);
            _expectedTrackCount = Options.IsTrack ? 1 : remoteAlbum.Albums.FirstOrDefault()?.AlbumReleases.Value?.FirstOrDefault()?.TrackCount ?? 0;

            _destinationPath = new OsPath(Path.Combine(
                Options.DownloadPath,
                _releaseFormatter.BuildArtistFolderName(null),
                _releaseFormatter.BuildAlbumFilename("{Album Title}", new Album() { Title = GetAlbumTitle() })
            ));

            _clientItem = CreateClientItem();
            _logger.Debug($"Processing download. Type: {(Options.IsTrack ? "track" : "album")}, ID: {Options.ItemId}");
        }

        /// <summary>
        /// Implement the main download processing logic
        /// </summary>
        protected abstract Task ProcessDownloadAsync(CancellationToken token);

        /// <summary>
        /// Get the album title for folder naming
        /// </summary>
        protected virtual string GetAlbumTitle() => ReleaseInfo.Album ?? ReleaseInfo.Title;

        /// <summary>
        /// Sanitizes a filename by removing invalid characters
        /// </summary>
        protected static string SanitizeFileName(string fileName) => string.IsNullOrEmpty(fileName) ? "Unknown" : FileNameSanitizer.Replace(fileName, "_").Trim();

        /// <summary>
        /// Logs a message and appends it to the client message buffer
        /// </summary>
        protected void LogAndAppendMessage(string message, LogLevel logLevel)
        {
            _message.AppendLine(message);
            _logger?.Log(logLevel, message);
        }

        /// <summary>
        /// Creates the initial download client item
        /// </summary>
        protected virtual DownloadClientItem CreateClientItem() => new()
        {
            DownloadId = ID,
            Title = ReleaseInfo.Title,
            TotalSize = ReleaseInfo.Size,
            DownloadClientInfo = Options.ClientInfo,
            OutputPath = _destinationPath,
        };

        /// <summary>
        /// Calculates remaining download time based on current progress
        /// </summary>
        protected virtual TimeSpan? GetRemainingTime()
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

        /// <summary>
        /// Gets distinct messages from the message buffer
        /// </summary>
        protected virtual string GetDistinctMessages() => string.Join(Environment.NewLine, _message.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Distinct());

        /// <summary>
        /// Calculates remaining download size based on track progress
        /// </summary>
        protected virtual long GetRemainingSize()
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

        /// <summary>
        /// Maps request state to download item status
        /// </summary>
        public virtual DownloadItemStatus GetDownloadItemStatus() => State switch
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

        /// <summary>
        /// Builds a track filename using the release formatter
        /// </summary>
        protected string BuildTrackFilename(Track track, Album album, string extension = ".flac") => _releaseFormatter.BuildTrackFilename(null, track, album) + extension;

        public override void Start() => throw new NotImplementedException();
        public override void Pause() => throw new NotImplementedException();
        protected override Task<RequestReturn> RunRequestAsync() => throw new NotImplementedException();
    }
}