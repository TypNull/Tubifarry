using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using System.Collections.Concurrent;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Telemetry;
using Tubifarry.Download.Clients.Soulseek.Models;
using Tubifarry.Indexers.Soulseek;

namespace Tubifarry.Download.Clients.Soulseek;

internal static class SlskdEventTypes
{
    public const string DownloadDirectoryComplete = "DownloadDirectoryComplete";
    public const string DownloadFileComplete = "DownloadFileComplete";
}

public class SlskdDownloadManager : ISlskdDownloadManager
{
    private readonly ConcurrentDictionary<DownloadKey<int, string>, SlskdDownloadItem> _downloadMappings = new();

    // Adaptive transfer poll times per definition ID
    private readonly ConcurrentDictionary<int, DateTime> _lastTransferPollTimes = new();
    // Event poll times per definition ID (separate from transfer poll)
    private readonly ConcurrentDictionary<int, DateTime> _lastEventPollTimes = new();
    // Timestamp of the newest event already processed per definition ID.
    private readonly ConcurrentDictionary<int, DateTime> _lastEventTimestamps = new();
    // Latest settings snapshot per definition ID: used by event-triggered retry callbacks
    private readonly ConcurrentDictionary<int, SlskdProviderSettings> _settingsCache = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly ISlskdApiClient _apiClient;
    private readonly IDownloadHistoryRepository _downloadHistoryRepository;
    private readonly ISlskdItemsParser _slskdItemsParser;
    private readonly ISentryHelper _sentry;
    private readonly Logger _logger;
    private readonly SlskdRetryHandler _retryHandler;
    private readonly SlskdGrabMatcher _grabMatcher;
    private readonly SlskdFolderPostProcessor _folderPostProcessor;
    private readonly SlskdBatchRestorer _batchRestorer;
    private readonly SlskdTransferCleaner _transferCleaner;

    public SlskdDownloadManager(
        ISlskdApiClient apiClient,
        IDownloadHistoryRepository downloadHistoryRepository,
        ISlskdItemsParser slskdItemsParser,
        IRemotePathMappingService remotePathMappingService,
        IDiskProvider diskProvider,
        ISentryHelper sentry,
        Logger logger)
    {
        _apiClient = apiClient;
        _downloadHistoryRepository = downloadHistoryRepository;
        _slskdItemsParser = slskdItemsParser;
        _sentry = sentry;
        _logger = logger;
        _retryHandler = new SlskdRetryHandler(apiClient, sentry, NzbDroneLogger.GetLogger(typeof(SlskdRetryHandler)));
        _grabMatcher = new SlskdGrabMatcher(NzbDroneLogger.GetLogger(typeof(SlskdGrabMatcher)));
        SlskdLocalFiles localFiles = new(remotePathMappingService, diskProvider);
        _folderPostProcessor = new SlskdFolderPostProcessor(localFiles, diskProvider, NzbDroneLogger.GetLogger(typeof(SlskdFolderPostProcessor)));
        _batchRestorer = new SlskdBatchRestorer(apiClient, NzbDroneLogger.GetLogger(typeof(SlskdBatchRestorer)));
        _transferCleaner = new SlskdTransferCleaner(apiClient, localFiles, remotePathMappingService, diskProvider, NzbDroneLogger.GetLogger(typeof(SlskdTransferCleaner)));
    }

    public async Task<string> DownloadAsync(RemoteAlbum remoteAlbum, int definitionId, SlskdProviderSettings settings)
    {
        _settingsCache[definitionId] = settings;

        SlskdDownloadItem item = new(remoteAlbum.Release) { ID = Guid.NewGuid().ToString("N"), GrabbedAt = DateTime.UtcNow };
        _logger.Trace($"Download initiated: {remoteAlbum.Release.Title} | Files: {item.FileData.Count}");

        ISpan? span = _sentry.StartSpan("slskd.download", remoteAlbum.Release.Title);
        _sentry.SetSpanData(span, "album.title", remoteAlbum.Release.Album);
        _sentry.SetSpanData(span, "album.artist", remoteAlbum.Release.Artist);
        _sentry.SetSpanData(span, "file_count", item.FileData.Count);

        try
        {
            string username = SlskdDownloadItem.GetUsername(remoteAlbum.Release.DownloadUrl);
            List<(string Filename, long Size)> files = item.FileData.Select(f => (f.Filename ?? string.Empty, f.Size)).ToList();
            string? destination = SlskdFolderNaming.GetDownloadDestination(files.Select(f => f.Filename), remoteAlbum.Release.Artist, remoteAlbum.Release.Album);

            SlskdEnqueueResult result = await _apiClient.EnqueueDownloadAsync(settings, username, files, externalId: item.ID, destination: destination);

            if (result.AllFailed)
                throw new DownloadClientException(
                    $"All {result.Failed.Count} files failed to enqueue: {string.Join("; ", result.Failed.Select(f => $"{Path.GetFileName(f.Filename)}: {f.Message}"))}");

            if (result.Failed.Count > 0)
                _logger.Warn($"{result.Failed.Count} of {files.Count} files failed to enqueue for {username}: {string.Join("; ", result.Failed.Select(f => f.Message).Distinct())}");

            item.BatchId = result.BatchId;
            item.EnqueueDestination = destination;
            if (destination != null && result.BatchId != null)
                item.DerivedSubdirectory = destination;
            item.Username = username;
            SubscribeStateChanges(item, definitionId);
            AddItem(definitionId, item);

            _sentry.SetSpanTag(span, "download.id", item.ID);
            _sentry.FinishSpan(span, SpanStatus.Ok);

            return item.ID;
        }
        catch (Exception ex)
        {
            _sentry.FinishSpan(span, ex);
            throw;
        }
    }

    public IEnumerable<DownloadClientItem> GetItems(int definitionId, SlskdProviderSettings settings, OsPath remotePath)
    {
        _settingsCache[definitionId] = settings;

        try
        {
            RefreshAsync(definitionId, settings).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update download items from Slskd. Returning cached items.");
        }

        TimeSpan? timeout = settings.GetTimeout();
        DateTime now = DateTime.UtcNow;

        foreach (SlskdDownloadItem item in GetItemsForDef(definitionId))
        {
            DownloadClientItem clientItem;
            try
            {
                SlskdStatusResolver.DownloadStatus resolved = SlskdStatusResolver.Resolve(item, timeout, now);
                clientItem = new()
                {
                    DownloadId = item.ID,
                    Title = item.ReleaseInfo.Title,
                    Category = "slskd",
                    CanBeRemoved = true,
                    CanMoveFiles = true,
                    OutputPath = item.GetFullFolderPath(remotePath),
                    Status = resolved.Status,
                    Message = resolved.Message,
                    TotalSize = resolved.TotalSize,
                    RemainingSize = resolved.RemainingSize,
                    RemainingTime = resolved.RemainingTime,
                };

                EmitCompletionSpan(item, resolved);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Failed to build DownloadClientItem for {item.ID}. Skipping.");
                continue;
            }

            yield return clientItem;
        }
    }

    public void RemoveItem(DownloadClientItem clientItem, bool deleteData, int definitionId, SlskdProviderSettings settings)
    {
        if (!deleteData)
            return;

        SlskdDownloadItem? item = GetItem(definitionId, clientItem.DownloadId);
        if (item == null)
            return;

        List<SlskdDownloadItem> otherItems = GetItemsForDef(definitionId).Where(i => !ReferenceEquals(i, item)).ToList();

        _grabMatcher.MarkRemoved(item);
        RemoveItemFromDict(definitionId, clientItem.DownloadId);
        _ = _transferCleaner.RemoveAsync(item, otherItems, settings);
    }

    private async Task RefreshAsync(int definitionId, SlskdProviderSettings settings)
    {
        await _refreshLock.WaitAsync();
        try
        {
            HashSet<string> activeUsernames = GetActiveUsernames(definitionId);

            TimeSpan transferInterval = activeUsernames.Count > 0 ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(30);

            DateTime now = DateTime.UtcNow;

            DateTime lastTransfer = _lastTransferPollTimes.GetOrAdd(definitionId, DateTime.MinValue);
            if (now - lastTransfer >= transferInterval)
            {
                await PollTransfersAsync(definitionId, settings, activeUsernames);
                _lastTransferPollTimes[definitionId] = DateTime.UtcNow;
            }

            DateTime lastEvent = _lastEventPollTimes.GetOrAdd(definitionId, DateTime.MinValue);
            if (now - lastEvent >= TimeSpan.FromSeconds(5))
            {
                await PollEventsAsync(definitionId, settings);
                _lastEventPollTimes[definitionId] = DateTime.UtcNow;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task PollTransfersAsync(int definitionId, SlskdProviderSettings settings, HashSet<string> activeUsernames)
    {
        List<SlskdUserTransfers> transfers = !settings.Inclusive && activeUsernames.Count > 0
            ? (await Task.WhenAll(activeUsernames.Select(username => _apiClient.GetUserTransfersAsync(settings, username)))).OfType<SlskdUserTransfers>().ToList()
            : await _apiClient.GetAllTransfersAsync(settings);

        HashSet<string> currentIdSet = [];
        SlskdGrabHistory history = CreateGrabHistory(definitionId);
        foreach (SlskdUserTransfers userTransfers in transfers)
            await ProcessUserTransfersAsync(definitionId, settings, userTransfers, history, currentIdSet);

        _logger.Debug($"[def={definitionId}] Polled {activeUsernames.Count} users | Tracked: {currentIdSet.Count}");

        if (settings.Inclusive)
        {
            foreach (SlskdDownloadItem item in GetItemsForDef(definitionId)
                .Where(i => !currentIdSet.Contains(i.ID) && i.ReleaseInfo.DownloadProtocol == null)
                .ToList())
            {
                _logger.Trace($"[def={definitionId}] Pruning inclusive item {item.ID} (gone from Slskd)");
                RemoveItemFromDict(definitionId, item.ID);
            }
        }
    }

    private async Task ProcessUserTransfersAsync(
        int definitionId,
        SlskdProviderSettings settings,
        SlskdUserTransfers userTransfers,
        SlskdGrabHistory history,
        HashSet<string> currentIdSet)
    {
        SlskdDestinationConfig? destinationConfig = settings.GetDestinationConfig();
        List<SlskdTransferGroup> groups = _grabMatcher.Assign(userTransfers.Username, userTransfers.Directories, GetItemsForDef(definitionId).ToList(), history);

        foreach (SlskdTransferGroup group in groups)
        {
            try
            {
                await ProcessGroupAsync(definitionId, settings, userTransfers.Username, group, currentIdSet, destinationConfig);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"[def={definitionId}] Processing slskd transfers in '{group.Directory.Directory}' from {userTransfers.Username} failed");
            }
        }
    }

    private async Task ProcessGroupAsync(
        int definitionId,
        SlskdProviderSettings settings,
        string username,
        SlskdTransferGroup group,
        HashSet<string> currentIdSet,
        SlskdDestinationConfig? destinationConfig)
    {
        SlskdDownloadDirectory dir = group.Directory;
        currentIdSet.Add(SlskdDownloadItem.GetStableMD5Id(dir.Files?.Select(f => f.Filename) ?? []));

        SlskdDownloadItem? item = group.Owner ?? TrackUnownedGroup(definitionId, username, group, settings.Inclusive);
        if (item == null)
            return;

        currentIdSet.Add(item.ID);
        item.Username ??= username;

        if (item.BatchId == null && SlskdBatchRestorer.GetMainBatchId(dir) is string batchId)
            await _batchRestorer.RestoreAsync(item, batchId, settings);

        if (item.DerivedSubdirectory == null && destinationConfig?.UsesDefaultPattern == false &&
            dir.Files?.FirstOrDefault()?.Filename is { Length: > 0 } firstFile)
        {
            item.DerivedSubdirectory = SlskdPathResolver.ResolveSubdirectory(
                destinationConfig,
                username,
                firstFile,
                item.BatchId,
                item.BatchId != null ? item.ID : null);
        }

        item.SlskdDownloadDirectory = dir;
        _folderPostProcessor.Process(item, settings);
    }

    private SlskdDownloadItem? TrackUnownedGroup(int definitionId, string username, SlskdTransferGroup group, bool inclusive)
    {
        if (group.Grab != null && GetItem(definitionId, group.Grab.DownloadId) is SlskdDownloadItem existing)
            return existing;

        if (group.Grab != null && _grabMatcher.IsRemoved(group.Grab.DownloadId))
            return null;

        SlskdDownloadItem? item = group.Grab != null
            ? new SlskdDownloadItem(group.Grab.Release) { ID = group.Grab.DownloadId, GrabbedAt = group.Grab.Date }
            : inclusive ? new SlskdDownloadItem(CreateReleaseInfoFromDirectory(username, group.Directory)) : null;

        if (item == null)
            return null;

        item.FolderProcessingDisabled = group.Grab != null && IsGrabFinished(group.Grab.DownloadId);
        _logger.Debug($"[def={definitionId}] Tracking {item.ID} ({item.ReleaseInfo.Title}) from {group.Directory.Files?.Count ?? 0} slskd transfers of {username}{(item.FolderProcessingDisabled ? "; already finished, folder processing disabled" : "")}");
        SubscribeStateChanges(item, definitionId);
        AddItem(definitionId, item);
        return item;
    }

    private async Task PollEventsAsync(int definitionId, SlskdProviderSettings settings)
    {
        (List<SlskdEventRecord> events, _) = await _apiClient.GetEventsAsync(settings, 0, 50);
        if (events.Count == 0)
            return;

        DateTime newest = events.Max(e => e.Timestamp);

        if (!_lastEventTimestamps.TryGetValue(definitionId, out DateTime lastSeen))
        {
            _lastEventTimestamps[definitionId] = newest;
            return;
        }

        foreach (SlskdEventRecord record in events
            .Where(e => e.Timestamp > lastSeen)
            .OrderBy(e => e.Timestamp))
        {
            try
            {
                await HandleEventAsync(definitionId, settings, record);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"[def={definitionId}] Failed to process event {record.Type} ({record.Id})");
            }
        }

        if (newest > lastSeen)
            _lastEventTimestamps[definitionId] = newest;
    }

    private async Task HandleEventAsync(int definitionId, SlskdProviderSettings settings, SlskdEventRecord record)
    {
        if (string.IsNullOrEmpty(record.Data))
            return;

        if (record.Type == SlskdEventTypes.DownloadDirectoryComplete)
        {
            using JsonDocument doc = JsonDocument.Parse(record.Data);
            string remoteDir = GetString(doc.RootElement, "remoteDirectoryName") ?? "";
            string username = GetString(doc.RootElement, "username") ?? "";
            string? localDir = GetString(doc.RootElement, "localDirectoryName");

            List<SlskdDownloadItem> items = GetItemsForDef(definitionId)
                .Where(i => (i.Username == null || string.Equals(i.Username, username, StringComparison.OrdinalIgnoreCase)) &&
                            (string.Equals(i.SlskdDownloadDirectory?.Directory, remoteDir, StringComparison.OrdinalIgnoreCase) ||
                             ItemContainsRemoteDirectory(i, remoteDir)))
                .ToList();

            SlskdDownloadItem? item = items.FirstOrDefault();
            if (item != null)
            {
                if (items.Count == 1)
                    item.ConfirmedSubdirectory ??= SlskdPathResolver.MakeRelativeToDownloads(settings.DownloadPath, localDir);

                _logger.Trace($"[def={definitionId}] Event DownloadDirectoryComplete: {remoteDir} by {username} -> {item.ConfirmedSubdirectory ?? "<unresolved>"}");

                SlskdUserTransfers? userTransfers = await _apiClient.GetUserTransfersAsync(settings, username);
                if (userTransfers != null)
                    await ProcessUserTransfersAsync(definitionId, settings, userTransfers, CreateGrabHistory(definitionId), []);
            }
        }
        else if (record.Type == SlskdEventTypes.DownloadFileComplete)
        {
            using JsonDocument doc = JsonDocument.Parse(record.Data);

            JsonElement transferEl = doc.RootElement.TryGetProperty("transfer", out JsonElement transfer) ? transfer : default;
            string remoteFilename = GetString(doc.RootElement, "remoteFilename") is { Length: > 0 } rfn ? rfn : GetString(transferEl, "filename") ?? "";
            string? localFilename = GetString(doc.RootElement, "localFilename");
            string username = GetString(transferEl, "username") ?? "";
            string? batchId = GetString(transferEl, "batchId");
            DateTime requestedAt = DateTime.TryParse(GetString(transferEl, "requestedAt"), out DateTime parsed) ? parsed : DateTime.MinValue;

            _logger.Trace($"[def={definitionId}] Event DownloadFileComplete: {Path.GetFileName(remoteFilename)} by {username}");

            if (localFilename == null || remoteFilename.Length == 0)
                return;

            string? localDir = Path.GetDirectoryName(localFilename.Replace('\\', '/'))?.Replace('\\', '/');
            string? subdirectory = SlskdPathResolver.MakeRelativeToDownloads(settings.DownloadPath, localDir);
            if (subdirectory == null)
                return;

            SlskdDownloadItem? item = _grabMatcher.FindOwner(GetItemsForDef(definitionId), username, remoteFilename, batchId, requestedAt);
            if (item != null)
                SlskdLocalFiles.Record(item, settings, remoteFilename, localFilename);

            if (item != null && item.ConfirmedSubdirectory == null)
            {
                item.ConfirmedSubdirectory = subdirectory;
                _logger.Debug($"[def={definitionId}] Confirmed subdirectory from DownloadFileComplete: '{subdirectory}' for {item.ID}");
            }
        }
    }

    private bool IsGrabFinished(string downloadId)
    {
        try
        {
            return _downloadHistoryRepository.FindByDownloadId(downloadId)
                .Any(h => h.EventType is DownloadHistoryEventType.DownloadImported or DownloadHistoryEventType.DownloadFailed or DownloadHistoryEventType.DownloadIgnored);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, $"Reading download history for {downloadId} failed");
            return false;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ItemContainsRemoteDirectory(SlskdDownloadItem item, string remoteDir)
    {
        if (string.IsNullOrEmpty(remoteDir))
            return false;

        string normalized = remoteDir.Replace('/', '\\').TrimEnd('\\');
        return item.FileData.Any(f => string.Equals(SlskdFolderNaming.GetParentDirectory(f.Filename), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private void EmitCompletionSpan(SlskdDownloadItem item, SlskdStatusResolver.DownloadStatus resolved)
    {
        bool isTerminal = resolved.Status is DownloadItemStatus.Completed or DownloadItemStatus.Failed;
        if (!isTerminal || item.LastReportedStatus == resolved.Status)
            return;

        item.LastReportedStatus = resolved.Status;

        int failedCount = item.FileStates.Values.Count(fs => fs.GetStatus() == DownloadItemStatus.Failed);

        ISpan? span = _sentry.StartSpan("slskd.completion", item.ReleaseInfo.Title);
        _sentry.SetSpanData(span, "download.id", item.ID);
        _sentry.SetSpanData(span, "username", item.Username);
        _sentry.SetSpanData(span, "file_count", item.FileStates.Count);
        _sentry.SetSpanData(span, "failed_count", failedCount);
        _sentry.SetSpanData(span, "status", resolved.Status == DownloadItemStatus.Completed ? "completed" : "failed");
        if (resolved.Message != null)
            _sentry.SetSpanData(span, "message", resolved.Message);

        _sentry.FinishSpan(span, resolved.Status == DownloadItemStatus.Completed
            ? SpanStatus.Ok
            : SpanStatus.InternalError);
    }

    private void SubscribeStateChanges(SlskdDownloadItem item, int definitionId)
    {
        item.FileStateChanged += (sender, fileState) =>
        {
            if (_settingsCache.TryGetValue(definitionId, out SlskdProviderSettings? s))
                _retryHandler.OnFileStateChanged(sender as SlskdDownloadItem, fileState, s);
        };
    }

    private ReleaseInfo CreateReleaseInfoFromDirectory(string username, SlskdDownloadDirectory dir)
    {
        SlskdFolderData folderData = dir.CreateFolderData(username, _slskdItemsParser);
        SlskdSearchData searchData = new(null, null, false, false, 1, null);
        IGrouping<string, SlskdFileData> dirGroup = dir.ToSlskdFileDataList().GroupBy(_ => dir.Directory).First();
        AlbumData albumData = _slskdItemsParser.CreateAlbumData(string.Empty, dirGroup, searchData, folderData, null, 0);
        ReleaseInfo release = albumData.ToReleaseInfo();
        release.DownloadProtocol = null;
        return release;
    }

    private SlskdGrabHistory CreateGrabHistory(int definitionId) =>
        new(_downloadHistoryRepository, definitionId, NzbDroneLogger.GetLogger(typeof(SlskdGrabHistory)));

    private SlskdDownloadItem? GetItem(int definitionId, string id) =>
        _downloadMappings.TryGetValue(new DownloadKey<int, string>(definitionId, id), out SlskdDownloadItem? item)
            ? item : null;

    private IEnumerable<SlskdDownloadItem> GetItemsForDef(int definitionId) =>
        _downloadMappings
            .Where(kvp => kvp.Key.OuterKey == definitionId)
            .Select(kvp => kvp.Value);

    private void AddItem(int definitionId, SlskdDownloadItem item) =>
        _downloadMappings[new DownloadKey<int, string>(definitionId, item.ID)] = item;

    private void RemoveItemFromDict(int definitionId, string id) =>
        _downloadMappings.TryRemove(new DownloadKey<int, string>(definitionId, id), out _);

    private HashSet<string> GetActiveUsernames(int definitionId) =>
        [.. GetItemsForDef(definitionId)
            .Where(i => i.Username != null)
            .Select(i => i.Username!)];
}
