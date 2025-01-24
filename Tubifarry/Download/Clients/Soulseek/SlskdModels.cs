using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;
using Tubifarry.Indexers.Soulseek;

namespace Tubifarry.Download.Clients.Soulseek
{
    public class SlskdDownloadItem
    {
        private readonly DownloadClientItem _downloadClientItem;
        private DateTime _lastUpdateTime;
        private long _lastDownloadedSize;

        public int ID { get; set; }
        public List<SlskdFileData> FileData { get; set; } = new();
        public string? Username { get; set; }
        public RemoteAlbum RemoteAlbum { get; set; }

        public event EventHandler<SlskdFileState>? FileStateChanged;

        private Logger _logger;

        private SlskdDownloadDirectory? _slskdDownloadDirectory;
        private Dictionary<string, SlskdFileState> _previousFileStates = new();

        public List<Task> PostProcessTasks { get; } = new();

        public SlskdDownloadDirectory? SlskdDownloadDirectory
        {
            get => _slskdDownloadDirectory;
            set
            {
                if (_slskdDownloadDirectory == value)
                    return;
                CompareFileStates(value);
                _slskdDownloadDirectory = value;
            }
        }

        public SlskdDownloadItem(RemoteAlbum remoteAlbum)
        {
            _logger = NzbDroneLogger.GetLogger(this);
            RemoteAlbum = remoteAlbum;
            FileData = JsonSerializer.Deserialize<List<SlskdFileData>>(RemoteAlbum.Release.Source) ?? new();
            _lastUpdateTime = DateTime.UtcNow;
            _lastDownloadedSize = 0;
            HashCode hash = new();
            List<string?> sortedFilenames = FileData
                .Select(file => file.Filename)
                .OrderBy(filename => filename)
                .ToList();
            foreach (string? filename in sortedFilenames)
                hash.Add(filename);
            ID = hash.ToHashCode();
            _downloadClientItem = new() { DownloadId = ID.ToString(), CanBeRemoved = true, CanMoveFiles = true };
        }

        private void CompareFileStates(SlskdDownloadDirectory? newDirectory)
        {
            if (newDirectory?.Files == null)
                return;

            foreach (SlskdDownloadFile file in newDirectory.Files)
            {
                if (_previousFileStates.TryGetValue(file.Filename, out SlskdFileState? fileState) && fileState != null)
                {
                    fileState.UpdateFile(file);
                    if (fileState.State != fileState.PreviousState)
                        FileStateChanged?.Invoke(this, fileState);
                }
                else
                    _previousFileStates.Add(file.Filename, new(file));


            }
        }

        public OsPath GetFullFolderPath(OsPath downloadPath) => new(Path.Combine(downloadPath.FullPath, SlskdDownloadDirectory?.Directory
            .Replace('\\', '/')
            .TrimEnd('/')
            .Split('/')
            .LastOrDefault() ?? ""));

        public DownloadClientItem GetDownloadClientItem(OsPath downloadPath, TimeSpan? timeout)
        {
            _downloadClientItem.OutputPath = GetFullFolderPath(downloadPath);
            _downloadClientItem.Title = RemoteAlbum.Release.Title;

            if (SlskdDownloadDirectory?.Files == null)
                return _downloadClientItem;

            long totalSize = SlskdDownloadDirectory.Files.Sum(file => file.Size);
            long remainingSize = SlskdDownloadDirectory.Files.Sum(file => file.BytesRemaining);
            long downloadedSize = totalSize - remainingSize;

            DateTime now = DateTime.UtcNow;
            TimeSpan timeSinceLastUpdate = now - _lastUpdateTime;
            long sizeSinceLastUpdate = downloadedSize - _lastDownloadedSize;
            double downloadSpeed = timeSinceLastUpdate.TotalSeconds > 0 ? sizeSinceLastUpdate / timeSinceLastUpdate.TotalSeconds : 0;
            TimeSpan? remainingTime = downloadSpeed > 0 ? TimeSpan.FromSeconds(remainingSize / downloadSpeed) : null;

            _lastUpdateTime = now;
            _lastDownloadedSize = downloadedSize;

            List<DownloadItemStatus> fileStatuses = _previousFileStates.Values.Select(file => file.GetStatus()).ToList();
            List<string> failedFiles = _previousFileStates.Values
                .Where(file => file.GetStatus() == DownloadItemStatus.Failed)
                .Select(file => Path.GetFileName(file.File.Filename)).ToList();

            DownloadItemStatus status = DownloadItemStatus.Queued;
            DateTime lastTime = SlskdDownloadDirectory.Files.Max(x => x.EnqueuedAt > x.StartedAt ? x.EnqueuedAt : x.StartedAt + x.ElapsedTime);

            if (now - lastTime > timeout)
                status = DownloadItemStatus.Failed;
            else if ((double)failedFiles.Count / fileStatuses.Count * 100 > 20)
            {
                status = DownloadItemStatus.Failed;
                _downloadClientItem.Message = $"Downloading {failedFiles.Count} files failed: {string.Join(", ", failedFiles)}";
            }
            else if (failedFiles.Any())
            {
                status = DownloadItemStatus.Warning;
                _downloadClientItem.Message = $"Downloading {failedFiles.Count} files failed: {string.Join(", ", failedFiles)}";
            }
            else if (fileStatuses.All(status => status == DownloadItemStatus.Completed))
            {
                if (PostProcessTasks.Any(task => !task.IsCompleted))
                    status = DownloadItemStatus.Downloading;
                else
                    status = DownloadItemStatus.Completed;
            }
            else if (fileStatuses.Any(status => status == DownloadItemStatus.Paused))
                status = DownloadItemStatus.Paused;
            else if (fileStatuses.Any(status => status == DownloadItemStatus.Warning))
            {
                _downloadClientItem.Message = "Some files failed. Retrying download...";
                status = DownloadItemStatus.Warning;
            }
            else if (fileStatuses.Any(status => status == DownloadItemStatus.Downloading))
                status = DownloadItemStatus.Downloading;

            // Update DownloadClientItem
            _downloadClientItem.TotalSize = totalSize;
            _downloadClientItem.RemainingSize = remainingSize;
            _downloadClientItem.RemainingTime = remainingTime;
            _downloadClientItem.Status = status;

            return _downloadClientItem;
        }
    }

    public class SlskdFileState
    {
        public SlskdDownloadFile File { get; private set; } = null!;
        public int RetryCount { get; private set; }
        private bool _retried = true;
        public int MaxRetryCount { get; private set; } = 1;
        public string State => File.State;
        public string PreviousState { get; private set; } = "Requested";

        public DownloadItemStatus GetStatus()
        {
            DownloadItemStatus status = GetStatus(State);
            if ((status == DownloadItemStatus.Failed && RetryCount < MaxRetryCount) || _retried)
                return DownloadItemStatus.Warning;
            return status;
        }

        private static DownloadItemStatus GetStatus(string state) => state switch
        {
            "Requested" => DownloadItemStatus.Queued, // "Requested" is treated as "Queued"
            "Queued, Remotely" or "Queued, Locally" => DownloadItemStatus.Queued, // Both are queued states
            "Initializing" => DownloadItemStatus.Queued, // "Initializing" is treated as "Queued"
            "InProgress" => DownloadItemStatus.Downloading, // "InProgress" maps to "Downloading"
            "Completed, Succeeded" => DownloadItemStatus.Completed, // Successful completion
            "Completed, Cancelled" => DownloadItemStatus.Failed, // Cancelled is treated as "Failed"
            "Completed, TimedOut" => DownloadItemStatus.Failed, // Timed out is treated as "Failed"
            "Completed, Errored" => DownloadItemStatus.Failed, // Errored is treated as "Failed"
            "Completed, Rejected" => DownloadItemStatus.Failed, // Rejected is treated as "Failed"
            _ => DownloadItemStatus.Queued // Default to "Queued" for unknown states
        };

        public SlskdFileState(SlskdDownloadFile file) => UpdateFile(file);

        public void UpdateFile(SlskdDownloadFile file)
        {
            if (!_retried)
                PreviousState = State;
            else if (File != null && GetStatus(file.State) == DownloadItemStatus.Failed)
                PreviousState = "Requested";
            File = file;
            _retried = false;
        }

        public void UpdateMaxRetryCount(int maxRetryCount) => MaxRetryCount = maxRetryCount;

        public void IncrementAttempt()
        {
            _retried = true;
            RetryCount++;
        }
    }

    public record SlskdDownloadDirectory(string Directory, int FileCount, List<SlskdDownloadFile>? Files)
    {
        public static IEnumerable<SlskdDownloadDirectory> GetDirectories(JsonElement directoriesElement)
        {
            if (directoriesElement.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (JsonElement directory in directoriesElement.EnumerateArray())
            {
                yield return new SlskdDownloadDirectory(
                    Directory: directory.TryGetProperty("directory", out JsonElement directoryElement) ? directoryElement.GetString() ?? string.Empty : string.Empty,
                    FileCount: directory.TryGetProperty("fileCount", out JsonElement fileCountElement) ? fileCountElement.GetInt32() : 0,
                    Files: directory.TryGetProperty("files", out JsonElement filesElement) ? SlskdDownloadFile.GetFiles(filesElement).ToList() : new List<SlskdDownloadFile>()
                );
            }
        }
    }

    public record SlskdDownloadFile(
       string Id,
       string Username,
       string Direction,
       string Filename,
       long Size,
       long StartOffset,
       string State,
       DateTime RequestedAt,
       DateTime EnqueuedAt,
       DateTime StartedAt,
       long BytesTransferred,
       double AverageSpeed,
       long BytesRemaining,
       TimeSpan ElapsedTime,
       double PercentComplete,
       TimeSpan RemainingTime,
       TimeSpan? EndedAt
    )
    {
        public static IEnumerable<SlskdDownloadFile> GetFiles(JsonElement filesElement)
        {
            if (filesElement.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (JsonElement file in filesElement.EnumerateArray())
            {
                yield return new SlskdDownloadFile(
                    Id: file.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty,
                    Username: file.TryGetProperty("username", out JsonElement username) ? username.GetString() ?? string.Empty : string.Empty,
                    Direction: file.TryGetProperty("direction", out JsonElement direction) ? direction.GetString() ?? string.Empty : string.Empty,
                    Filename: file.TryGetProperty("filename", out JsonElement filename) ? filename.GetString() ?? string.Empty : string.Empty,
                    Size: file.TryGetProperty("size", out JsonElement size) ? size.GetInt64() : 0L,
                    StartOffset: file.TryGetProperty("startOffset", out JsonElement startOffset) ? startOffset.GetInt64() : 0L,
                    State: file.TryGetProperty("state", out JsonElement state) ? state.GetString() ?? string.Empty : string.Empty,
                    RequestedAt: file.TryGetProperty("requestedAt", out JsonElement requestedAt) && DateTime.TryParse(requestedAt.GetString(), out DateTime requestedAtParsed) ? requestedAtParsed : DateTime.MinValue,
                    EnqueuedAt: file.TryGetProperty("enqueuedAt", out JsonElement enqueuedAt) && DateTime.TryParse(enqueuedAt.GetString(), out DateTime enqueuedAtParsed) ? enqueuedAtParsed : DateTime.MinValue,
                    StartedAt: file.TryGetProperty("startedAt", out JsonElement startedAt) && DateTime.TryParse(startedAt.GetString(), out DateTime startedAtParsed) ? startedAtParsed.ToUniversalTime() : DateTime.MinValue,
                    BytesTransferred: file.TryGetProperty("bytesTransferred", out JsonElement bytesTransferred) ? bytesTransferred.GetInt64() : 0L,
                    AverageSpeed: file.TryGetProperty("averageSpeed", out JsonElement averageSpeed) ? averageSpeed.GetDouble() : 0.0,
                    BytesRemaining: file.TryGetProperty("bytesRemaining", out JsonElement bytesRemaining) ? bytesRemaining.GetInt64() : 0L,
                    ElapsedTime: file.TryGetProperty("elapsedTime", out JsonElement elapsedTime) && TimeSpan.TryParse(elapsedTime.GetString(), out TimeSpan elapsedTimeParsed) ? elapsedTimeParsed : TimeSpan.Zero,
                    PercentComplete: file.TryGetProperty("percentComplete", out JsonElement percentComplete) ? percentComplete.GetDouble() : 0.0,
                    RemainingTime: file.TryGetProperty("remainingTime", out JsonElement remainingTime) && TimeSpan.TryParse(remainingTime.GetString(), out TimeSpan remainingTimeParsed) ? remainingTimeParsed : TimeSpan.Zero,
                    EndedAt: file.TryGetProperty("endedAt", out JsonElement endedAt) && TimeSpan.TryParse(endedAt.GetString(), out TimeSpan endedAtParsed) ? endedAtParsed : null
                );
            }
        }
    }


    public readonly struct DownloadKey
    {
        public int OuterKey { get; }
        public int InnerKey { get; }

        public DownloadKey(int outerKey, int innerKey)
        {
            OuterKey = outerKey;
            InnerKey = innerKey;
        }

        public override readonly bool Equals(object? obj) => obj is DownloadKey other && OuterKey == other.OuterKey && InnerKey == other.InnerKey;
        public override readonly int GetHashCode() => HashCode.Combine(OuterKey, InnerKey);
        public static bool operator ==(DownloadKey left, DownloadKey right) => left.Equals(right);
        public static bool operator !=(DownloadKey left, DownloadKey right) => !(left == right);
    }
}