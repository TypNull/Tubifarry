using NzbDrone.Core.Download;
using Tubifarry.Download.Clients.Soulseek.Models;

namespace Tubifarry.Download.Clients.Soulseek;

public static class SlskdStatusResolver
{
    public record DownloadStatus(
        DownloadItemStatus Status,
        string? Message,
        long TotalSize,
        long RemainingSize,
        TimeSpan? RemainingTime
    );

    public static DownloadStatus Resolve(SlskdDownloadItem item, TimeSpan? timeout, DateTime utcNow)
    {
        if (item.SlskdDownloadDirectory?.Files == null)
            return new(DownloadItemStatus.Queued, null, 0, 0, null);

        IReadOnlyList<SlskdDownloadFile> files = item.SlskdDownloadDirectory.Files;

        long totalSize = 0, remainingSize = 0, totalSpeed = 0;
        bool anyActive = false, anyIncomplete = false, allIncompleteRemoteQueued = true;
        DateTime lastActivity = DateTime.MinValue;

        foreach (SlskdDownloadFile f in files)
        {
            totalSize += f.Size;
            remainingSize += f.BytesRemaining;

            DownloadItemStatus fs = SlskdFileState.GetStatus(f.State);
            if (fs == DownloadItemStatus.Completed)
                continue;

            anyIncomplete = true;

            if (fs == DownloadItemStatus.Downloading)
            {
                anyActive = true;
                totalSpeed += (long)f.AverageSpeed;
            }
            else if (fs == DownloadItemStatus.Queued)
            {
                // Remotely queued files are waiting on the uploader, not on us.
                // They must NOT count as "active", otherwise the stale-timeout
                // branches below can never fire (a remotely-queued file would
                // simultaneously satisfy "all stuck in remote queue" and negate it).
                bool remotelyQueued = Enum.TryParse(f.State, ignoreCase: true, out TransferStates queuedState)
                    && queuedState.HasFlag(TransferStates.Queued)
                    && queuedState.HasFlag(TransferStates.Remotely);

                if (!remotelyQueued)
                    anyActive = true;
            }

            // Inactivity timestamp: max of enqueued / started / (started + elapsed)
            DateTime t3 = f.StartedAt + f.ElapsedTime;
            DateTime latest = f.EnqueuedAt > f.StartedAt ? f.EnqueuedAt : f.StartedAt;
            if (t3 > latest) latest = t3;
            if (latest > lastActivity) lastActivity = latest;

            // All-stuck check: short-circuit once one file is NOT stuck
            if (allIncompleteRemoteQueued)
            {
                // Reference time for "how long has this been waiting": prefer EnqueuedAt,
                // fall back to RequestedAt. If neither is known (MinValue), the file must
                // NOT be considered stuck — otherwise a fresh enqueue with a missing
                // timestamp would be failed instantly.
                DateTime queuedSince = f.EnqueuedAt != DateTime.MinValue ? f.EnqueuedAt : f.RequestedAt;
                bool stuckRemote = timeout.HasValue
                    && queuedSince != DateTime.MinValue
                    && Enum.TryParse<TransferStates>(f.State, ignoreCase: true, out TransferStates ts)
                    && ts.HasFlag(TransferStates.Queued)
                    && ts.HasFlag(TransferStates.Remotely)
                    && (utcNow - queuedSince) > timeout.Value;
                if (!stuckRemote)
                    allIncompleteRemoteQueued = false;
            }
        }

        bool allStuckInRemoteQueue = anyIncomplete && allIncompleteRemoteQueued;

        int totalFileCount = 0, failedCount = 0, completedCount = 0;
        bool anyWarning = false, anyPaused = false, anyDownloadingState = false;
        List<string> failedFileNames = [];

        foreach (SlskdFileState fs in item.FileStates.Values)
        {
            totalFileCount++;
            DownloadItemStatus s = fs.GetStatus();
            switch (s)
            {
                case DownloadItemStatus.Completed: completedCount++; break;
                case DownloadItemStatus.Failed:
                    failedCount++;
                    failedFileNames.Add(Path.GetFileName(fs.File.Filename));
                    break;
                case DownloadItemStatus.Warning: anyWarning = true; break;
                case DownloadItemStatus.Paused: anyPaused = true; break;
                case DownloadItemStatus.Downloading: anyDownloadingState = true; break;
            }
        }

        DownloadItemStatus status;
        string? message = null;

        if (allStuckInRemoteQueue && !anyActive)
        {
            status = DownloadItemStatus.Failed;
            message = "All files stuck in remote queue past timeout.";
        }
        else if (!anyActive && anyIncomplete)
        {
            status = timeout.HasValue && lastActivity != DateTime.MinValue && (utcNow - lastActivity) > timeout.Value * 2
                ? DownloadItemStatus.Failed
                : DownloadItemStatus.Queued;
        }
        else if (totalFileCount > 0 && (double)failedCount / totalFileCount * 100 > 20)
        {
            status = DownloadItemStatus.Failed;
            message = $"Downloading {failedCount} files failed: {string.Join(", ", failedFileNames)}";
        }
        else if (failedCount != 0)
        {
            status = DownloadItemStatus.Warning;
            message = $"Downloading {failedCount} files failed: {string.Join(", ", failedFileNames)}";
        }
        else if (totalFileCount > 0 && completedCount == totalFileCount)
        {
            status = item.PostProcessTasks.Any(t => !t.IsCompleted)
                ? DownloadItemStatus.Downloading
                : DownloadItemStatus.Completed;
        }
        else if (anyPaused)
        {
            status = DownloadItemStatus.Paused;
        }
        else if (anyWarning)
        {
            status = DownloadItemStatus.Warning;
            message = "Some files failed. Retrying download...";
        }
        else if (anyDownloadingState)
        {
            status = DownloadItemStatus.Downloading;
        }
        else
        {
            status = DownloadItemStatus.Queued;
        }

        TimeSpan? remainingTime = totalSpeed > 0
            ? TimeSpan.FromSeconds(remainingSize / (double)totalSpeed)
            : null;

        return new(status, message, totalSize, remainingSize, remainingTime);
    }
}
