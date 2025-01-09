using NzbDrone.Common.Disk;
using NzbDrone.Core.Indexers.Soulseek;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;

namespace NzbDrone.Core.Download.Clients.Soulseek
{
    class SlskdDownloadItem
    {
        private readonly DownloadClientItem _downloadClientItem;

        public string ID { get; set; } = string.Empty;
        public List<SlskdFileData> FileData { get; set; } = new();
        public string Username { get; set; } = string.Empty;
        public RemoteAlbum RemoteAlbum { get; set; }
        public SlskdDownloadDirectory? SlskdDownloadDirectory { get; set; }

        public SlskdDownloadItem(string id, RemoteAlbum remoteAlbum)
        {
            ID = id;
            RemoteAlbum = remoteAlbum;
            FileData = JsonSerializer.Deserialize<List<SlskdFileData>>(RemoteAlbum.Release.Source) ?? new();
            _downloadClientItem = new() { DownloadId = ID, CanBeRemoved = true, CanMoveFiles = true };
        }

        public DownloadClientItem GetDownloadClientItem(string downloadPath)
        {
            _downloadClientItem.OutputPath = new OsPath(Path.Combine(downloadPath, SlskdDownloadDirectory?.Directory ?? ""));
            _downloadClientItem.Title = RemoteAlbum.Release.Title;
            if (SlskdDownloadDirectory?.Files == null)
                return _downloadClientItem;

            long totalSize = SlskdDownloadDirectory.Files.Sum(file => file.Size);
            long remainingSize = SlskdDownloadDirectory.Files.Sum(file => file.BytesRemaining);
            TimeSpan? remainingTime = SlskdDownloadDirectory.Files.Any()
                ? SlskdDownloadDirectory.Files.Max(file => file.RemainingTime) : null;

            DownloadItemStatus status = DownloadItemStatus.Queued;

            if (SlskdDownloadDirectory.Files.All(file => file.IsCompleted))
                status = DownloadItemStatus.Completed;
            else if (SlskdDownloadDirectory.Files.Any(file => file.IsFailed))
                status = DownloadItemStatus.Failed;
            else if (SlskdDownloadDirectory.Files.Any(file => file.IsPaused))
                status = DownloadItemStatus.Paused;
            else if (SlskdDownloadDirectory.Files.Any(file => file.IsDownloading))
                status = DownloadItemStatus.Downloading;
            else if (SlskdDownloadDirectory.Files.Any(file => file.IsWarning))
                status = DownloadItemStatus.Warning;

            _downloadClientItem.TotalSize = totalSize;
            _downloadClientItem.RemainingSize = remainingSize;
            _downloadClientItem.RemainingTime = remainingTime;
            _downloadClientItem.Status = status;
            return _downloadClientItem;
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
        TimeSpan RemainingTime
    )
    {
        public bool IsCompleted => State == "Completed";
        public bool IsFailed => State == "Failed";
        public bool IsPaused => State == "Paused";
        public bool IsDownloading => State == "Downloading";
        public bool IsWarning => State == "Warning";
        public bool IsQueued => State == "Queued";

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
                    StartedAt: file.TryGetProperty("startedAt", out JsonElement startedAt) && DateTime.TryParse(startedAt.GetString(), out DateTime startedAtParsed) ? startedAtParsed : DateTime.MinValue,
                    BytesTransferred: file.TryGetProperty("bytesTransferred", out JsonElement bytesTransferred) ? bytesTransferred.GetInt64() : 0L,
                    AverageSpeed: file.TryGetProperty("averageSpeed", out JsonElement averageSpeed) ? averageSpeed.GetDouble() : 0.0,
                    BytesRemaining: file.TryGetProperty("bytesRemaining", out JsonElement bytesRemaining) ? bytesRemaining.GetInt64() : 0L,
                    ElapsedTime: file.TryGetProperty("elapsedTime", out JsonElement elapsedTime) && TimeSpan.TryParse(elapsedTime.GetString(), out TimeSpan elapsedTimeParsed) ? elapsedTimeParsed : TimeSpan.Zero,
                    PercentComplete: file.TryGetProperty("percentComplete", out JsonElement percentComplete) ? percentComplete.GetDouble() : 0.0,
                    RemainingTime: file.TryGetProperty("remainingTime", out JsonElement remainingTime) && TimeSpan.TryParse(remainingTime.GetString(), out TimeSpan remainingTimeParsed) ? remainingTimeParsed : TimeSpan.Zero
                );
            }
        }
    }
}