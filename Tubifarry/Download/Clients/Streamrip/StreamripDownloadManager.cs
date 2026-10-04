using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using System.Collections.Concurrent;
using Tubifarry.Core.Python;
using Tubifarry.Core.Utilities;
using Tubifarry.Indexers.Streamrip;

namespace Tubifarry.Download.Clients.Streamrip
{
    public sealed record StreamripJobRequest(RemoteAlbum RemoteAlbum, StreamripIndexerSettings Source, StreamripClientSettings Client, int ClientId, DownloadClientItemClientInfo ClientInfo);

    public interface IStreamripDownloadManager
    {
        string Enqueue(StreamripJobRequest request);
        IEnumerable<DownloadClientItem> GetItems(int clientId);
        void Remove(string downloadId);
    }

    public sealed class StreamripDownloadManager(IStreamripService streamrip, Logger logger) : IStreamripDownloadManager, IHandle<ApplicationShutdownRequested>
    {
        private readonly ConcurrentDictionary<string, StreamripJob> _jobs = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, SlotPool> _slots = new();
        private readonly CancellationTokenSource _shutdown = new();

        public string Enqueue(StreamripJobRequest request)
        {
            string[] parts = request.RemoteAlbum.Release.DownloadUrl.Split('/', 3);
            if (parts.Length != 3 || !string.Equals(parts[0], request.Source.SourceName, StringComparison.Ordinal))
                throw new InvalidOperationException($"'{request.RemoteAlbum.Release.DownloadUrl}' is not a {request.Source.SourceType} release of this indexer");

            string id = Guid.NewGuid().ToString("N");
            string folder = Path.Combine(request.Client.DownloadPath, $"{FileSystemHelper.SanitizeFileName(request.RemoteAlbum.Release.Title)} [{id[..8]}]");
            int tracks = request.RemoteAlbum.Albums.FirstOrDefault()?.AlbumReleases.Value?.FirstOrDefault(r => r.Monitored)?.TrackCount ?? 0;

            StreamripJob job = new(id, request.ClientId, new DownloadClientItem
            {
                DownloadId = id,
                Title = request.RemoteAlbum.Release.Title,
                TotalSize = request.RemoteAlbum.Release.Size,
                RemainingSize = request.RemoteAlbum.Release.Size,
                DownloadClientInfo = request.ClientInfo,
                OutputPath = new OsPath(folder),
                Status = DownloadItemStatus.Queued
            });

            _jobs[id] = job;
            SlotPool slots = _slots.AddOrUpdate(
                request.ClientId,
                _ => new SlotPool(request.Client.MaxParallelDownloads),
                (_, existing) => existing.Size == request.Client.MaxParallelDownloads ? existing : new SlotPool(request.Client.MaxParallelDownloads));
            job.Task = Task.Run(() => RunAsync(job, slots, request.Source, parts[1], parts[2], tracks, folder));

            logger.Debug("Queued streamrip download '{0}' ({1})", job.Item.Title, request.RemoteAlbum.Release.DownloadUrl);
            return id;
        }

        public IEnumerable<DownloadClientItem> GetItems(int clientId) =>
            _jobs.Values.Where(job => job.ClientId == clientId).Select(job => job.Snapshot());

        public void Remove(string downloadId)
        {
            if (!_jobs.TryRemove(downloadId, out StreamripJob? job))
                return;

            job.Cancellation.Cancel();
            logger.Debug("Removed streamrip download '{0}'", job.Item.Title);
        }

        public void Handle(ApplicationShutdownRequested message) => _shutdown.Cancel();

        private async Task RunAsync(StreamripJob job, SlotPool slots, StreamripIndexerSettings source, string type, string itemId, int tracks, string folder)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, _shutdown.Token);
            int slot = -1;
            try
            {
                slot = await slots.AcquireAsync(linked.Token);
                job.Update(DownloadItemStatus.Downloading, 0, null);

                Progress<PythonWorkerProgress> progress = new(value => job.ReportProgress(value.Value));
                StreamripDownloadResult result = await streamrip.DownloadAsync(source, type, itemId, tracks, folder, slot, progress, linked.Token);

                if (result.Files.Count == 0)
                {
                    job.Update(DownloadItemStatus.Failed, 0, FirstLines(result.Errors, "No tracks were downloaded"));
                    logger.Warn("Streamrip download '{0}' failed: {1}", job.Item.Title, job.Item.Message);
                }
                else if (result.Errors.Count > 0)
                {
                    job.Update(DownloadItemStatus.Failed, 1, $"{result.Errors.Count} track(s) failed: {FirstLines(result.Errors, string.Empty)}");
                    logger.Warn("Streamrip download '{0}' is incomplete: {1}", job.Item.Title, job.Item.Message);
                }
                else
                {
                    job.Update(DownloadItemStatus.Completed, 1, null);
                    logger.Info("Streamrip downloaded {0} track(s) of '{1}'", result.Files.Count, job.Item.Title);
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                logger.Debug("Streamrip download '{0}' was cancelled", job.Item.Title);
            }
            catch (PythonWorkerException ex)
            {
                job.Update(DownloadItemStatus.Failed, 0, ex.Message);
                logger.Warn("Streamrip download '{0}' failed: {1}\n{2}", job.Item.Title, ex.Message, ex.PythonTraceback);
            }
            catch (Exception ex)
            {
                job.Update(DownloadItemStatus.Failed, 0, ex.Message);
                logger.Error(ex, "Streamrip download '{0}' failed", job.Item.Title);
            }
            finally
            {
                if (slot >= 0)
                    slots.Release(slot);
            }
        }

        private static string FirstLines(IReadOnlyList<string> lines, string fallback) =>
            lines.Count == 0 ? fallback : string.Join("; ", lines.Distinct().Take(3));

        private sealed class StreamripJob(string id, int clientId, DownloadClientItem item)
        {
            private readonly object _lock = new();

            public string Id { get; } = id;
            public int ClientId { get; } = clientId;
            public DownloadClientItem Item { get; } = item;
            public CancellationTokenSource Cancellation { get; } = new();
            public Task? Task { get; set; }

            public void Update(DownloadItemStatus status, double progress, string? message)
            {
                lock (_lock)
                {
                    Item.Status = status;
                    Item.RemainingSize = (long)(Item.TotalSize * (1 - Math.Clamp(progress, 0, 1)));
                    Item.Message = message;
                    Item.CanMoveFiles = status == DownloadItemStatus.Completed;
                    Item.CanBeRemoved = status is DownloadItemStatus.Completed or DownloadItemStatus.Failed;
                }
            }

            public void ReportProgress(double progress)
            {
                lock (_lock)
                {
                    if (Item.Status == DownloadItemStatus.Downloading)
                        Item.RemainingSize = (long)(Item.TotalSize * (1 - Math.Clamp(progress, 0, 1)));
                }
            }

            public DownloadClientItem Snapshot()
            {
                lock (_lock)
                    return Item.Clone();
            }
        }

        private sealed class SlotPool(int size)
        {
            private readonly SemaphoreSlim _available = new(size, size);
            private readonly bool[] _used = new bool[size];

            public int Size { get; } = size;

            public async Task<int> AcquireAsync(CancellationToken token)
            {
                await _available.WaitAsync(token);
                lock (_used)
                {
                    int slot = Array.IndexOf(_used, false);
                    _used[slot] = true;
                    return slot;
                }
            }

            public void Release(int slot)
            {
                lock (_used)
                    _used[slot] = false;
                _available.Release();
            }
        }
    }
}
