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
    // Last-seen event offset per definition ID for incremental polling
    private readonly ConcurrentDictionary<int, int> _lastEventOffsets = new();
    // Latest settings snapshot per definition ID: used by event-triggered retry callbacks
    private readonly ConcurrentDictionary<int, SlskdProviderSettings> _settingsCache = new();

    private readonly ISlskdApiClient _apiClient;
    private readonly IDownloadHistoryService _downloadHistoryService;
    private readonly ISlskdItemsParser _slskdItemsParser;
    private readonly IRemotePathMappingService _remotePathMappingService;
    private readonly IDiskProvider _diskProvider;
    private readonly ISentryHelper _sentry;
    private readonly Logger _logger;
    private readonly SlskdRetryHandler _retryHandler;

    public SlskdDownloadManager(
        ISlskdApiClient apiClient,
        IDownloadHistoryService downloadHistoryService,
        ISlskdItemsParser slskdItemsParser,
        IRemotePathMappingService remotePathMappingService,
        IDiskProvider diskProvider,
        ISentryHelper sentry,
        Logger logger)
    {
        _apiClient = apiClient;
        _downloadHistoryService = downloadHistoryService;
        _slskdItemsParser = slskdItemsParser;
        _remotePathMappingService = remotePathMappingService;
        _diskProvider = diskProvider;
        _sentry = sentry;
        _logger = logger;
        _retryHandler = new SlskdRetryHandler(apiClient, sentry, NzbDroneLogger.GetLogger(typeof(SlskdRetryHandler)));
    }

    public async Task<string> DownloadAsync(RemoteAlbum remoteAlbum, int definitionId, SlskdProviderSettings settings)
    {
        _settingsCache[definitionId] = settings;

        SlskdDownloadItem item = new(remoteAlbum.Release);
        _logger.Trace($"Download initiated: {remoteAlbum.Release.Title} | Files: {item.FileData.Count}");

        ISpan? span = _sentry.StartSpan("slskd.download", remoteAlbum.Release.Title);
        _sentry.SetSpanData(span, "album.title", remoteAlbum.Release.Album);
        _sentry.SetSpanData(span, "album.artist", remoteAlbum.Release.Artist);
        _sentry.SetSpanData(span, "file_count", item.FileData.Count);

        try
        {
            string username = ExtractUsernameFromPath(remoteAlbum.Release.DownloadUrl);
            List<(string Filename, long Size)> files = ParseFilesFromSource(remoteAlbum.Release.Source);
            string? destination = GetMultiDiscDestination(files.Select(f => f.Filename));

            SlskdEnqueueResult result = await _apiClient.EnqueueDownloadAsync(settings, username, files, externalId: item.ID, destination: destination);

            if (result.AllFailed)
                throw new DownloadClientException(
                    $"All {result.Failed.Count} files failed to enqueue: {string.Join("; ", result.Failed.Select(f => $"{Path.GetFileName(f.Filename)}: {f.Message}"))}");

            if (result.Failed.Count > 0)
                _logger.Warn($"{result.Failed.Count} of {files.Count} files failed to enqueue for {username}: {string.Join("; ", result.Failed.Select(f => f.Message).Distinct())}");

            item.BatchId = result.BatchId;
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

        string? directory = item.SlskdDownloadDirectory?.Directory;
        string localPath = GetLocalFolderPath(item, settings);

        _ = RemoveItemFilesAsync(item, settings);
        RemoveItemFromDict(definitionId, clientItem.DownloadId);

        if (settings.CleanStaleDirectories && !string.IsNullOrEmpty(directory))
            _ = CleanStaleDirectoriesAsync(directory, localPath, settings);
    }

    private async Task RefreshAsync(int definitionId, SlskdProviderSettings settings)
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
            int offset = _lastEventOffsets.GetOrAdd(definitionId, 0);
            await PollEventsAsync(definitionId, settings, offset);
            _lastEventPollTimes[definitionId] = DateTime.UtcNow;
        }
    }

    private async Task PollTransfersAsync(int definitionId, SlskdProviderSettings settings, HashSet<string> activeUsernames)
    {
        ConcurrentDictionary<string, bool> currentIdSet = new();
        SlskdDestinationConfig? destinationConfig = settings.GetDestinationConfig();

        if (!settings.Inclusive && activeUsernames.Count > 0)
        {
            await Task.WhenAll(activeUsernames.Select(async username =>
            {
                SlskdUserTransfers? userTransfers = await _apiClient.GetUserTransfersAsync(settings, username);
                if (userTransfers != null)
                    ProcessUserTransfers(definitionId, settings, userTransfers, currentIdSet, destinationConfig);
            }));
        }
        else
        {
            List<SlskdUserTransfers> all = await _apiClient.GetAllTransfersAsync(settings);
            foreach (SlskdUserTransfers user in all)
                ProcessUserTransfers(definitionId, settings, user, currentIdSet, destinationConfig);
        }

        _logger.Debug($"[def={definitionId}] Polled {activeUsernames.Count} users | Tracked: {currentIdSet.Count}");

        if (settings.Inclusive)
        {
            foreach (SlskdDownloadItem item in GetItemsForDef(definitionId)
                .Where(i => !currentIdSet.ContainsKey(i.ID) && i.ReleaseInfo.DownloadProtocol == null)
                .ToList())
            {
                _logger.Trace($"[def={definitionId}] Pruning inclusive item {item.ID} (gone from Slskd)");
                RemoveItemFromDict(definitionId, item.ID);
            }
        }
    }

    private void ProcessUserTransfers(
        int definitionId,
        SlskdProviderSettings settings,
        SlskdUserTransfers userTransfers,
        ConcurrentDictionary<string, bool> currentIdSet,
        SlskdDestinationConfig? destinationConfig)
    {
        foreach (SlskdDownloadDirectory dir in userTransfers.Directories)
        {
            string hash = SlskdDownloadItem.GetStableMD5Id(dir.Files?.Select(f => f.Filename) ?? []);
            currentIdSet.TryAdd(hash, true);

            SlskdDownloadItem? item = GetItem(definitionId, hash) ?? FindItemContainingFiles(definitionId, userTransfers.Username, dir);
            if (item == null)
            {
                _logger.Trace($"[def={definitionId}] Unknown item {hash}: checking history");
                DownloadHistory? history = _downloadHistoryService.GetLatestGrab(hash);

                if (history != null)
                    item = new SlskdDownloadItem(history.Release);
                else if (settings.Inclusive)
                    item = new SlskdDownloadItem(CreateReleaseInfoFromDirectory(userTransfers.Username, dir));

                if (item == null)
                    continue;

                SubscribeStateChanges(item, definitionId);
                AddItem(definitionId, item);
            }

            currentIdSet.TryAdd(item.ID, true);
            item.Username ??= userTransfers.Username;
            item.SlskdDownloadDirectory = dir;

            if (item.DerivedSubdirectory == null && destinationConfig?.UsesDefaultPattern == false &&
                dir.Files?.FirstOrDefault()?.Filename is { Length: > 0 } firstFile)
            {
                item.DerivedSubdirectory = SlskdPathResolver.ResolveSubdirectory(
                    destinationConfig,
                    userTransfers.Username,
                    firstFile,
                    item.BatchId,
                    item.BatchId != null ? item.ID : null);
            }

            MergeSplitDiscFolders(item, settings);
        }
    }

    private void MergeSplitDiscFolders(SlskdDownloadItem item, SlskdProviderSettings settings)
    {
        if (item.BatchId != null || item.DiscMergeScheduled || item.FileStates.Count == 0 ||
            item.FileStates.Values.Any(fs => fs.GetStatus() != DownloadItemStatus.Completed))
            return;

        List<string> parents = item.FileData
            .Select(f => f.Filename.Replace('/', '\\'))
            .Select(f => f.LastIndexOf('\\') is int i && i > 0 ? f[..i] : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (parents.Count < 2)
            return;

        HashSet<string> albums = parents.Select(SlskdTextProcessor.GetMergedDirectoryKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (albums.Count != 1 || albums.SetEquals(parents))
            return;

        item.DiscMergeScheduled = true;
        string album = albums.Single().Split('\\', '/').Last();
        OsPath root = _remotePathMappingService.RemapRemoteToLocal(settings.Host, new OsPath(settings.DownloadPath));
        OsPath albumPath = root + new OsPath(album);

        item.PostProcessTasks.Add(Task.Run(() =>
        {
            try
            {
                if (!_diskProvider.FolderExists(albumPath.FullPath))
                    _diskProvider.CreateFolder(albumPath.FullPath);

                foreach (string leaf in parents.Select(p => p.Split('\\', '/').Last()).Distinct(StringComparer.OrdinalIgnoreCase).Where(l => !l.Equals(album, StringComparison.OrdinalIgnoreCase)))
                {
                    OsPath discPath = root + new OsPath(leaf);
                    if (!_diskProvider.FolderExists(discPath.FullPath))
                        continue;
                    foreach (string file in _diskProvider.GetFiles(discPath.FullPath, true))
                    {
                        OsPath target = albumPath + new OsPath(Path.GetFileName(file));
                        if (!_diskProvider.FileExists(target.FullPath))
                            _diskProvider.MoveFile(file, target.FullPath);
                    }
                    if (_diskProvider.FolderEmpty(discPath.FullPath))
                        _diskProvider.DeleteFolder(discPath.FullPath, true);
                }
                item.ConfirmedSubdirectory = album;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Disc merge failed for {item.ID}; leaving files in place");
            }
        }));
    }

    private async Task PollEventsAsync(int definitionId, SlskdProviderSettings settings, int offset)
    {
        (List<SlskdEventRecord> events, _) = await _apiClient.GetEventsAsync(settings, offset, 50);
        if (events.Count == 0)
            return;

        foreach (SlskdEventRecord record in events)
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

        _lastEventOffsets[definitionId] = offset + events.Count;
    }

    private async Task HandleEventAsync(int definitionId, SlskdProviderSettings settings, SlskdEventRecord record)
    {
        if (string.IsNullOrEmpty(record.Data))
            return;

        if (record.Type == SlskdEventTypes.DownloadDirectoryComplete)
        {
            using JsonDocument doc = JsonDocument.Parse(record.Data);
            string remoteDir = doc.RootElement.TryGetProperty("remoteDirectoryName", out JsonElement rdn) ? rdn.GetString() ?? "" : "";
            string username = doc.RootElement.TryGetProperty("username", out JsonElement un) ? un.GetString() ?? "" : "";
            string? localDir = doc.RootElement.TryGetProperty("localDirectoryName", out JsonElement ldn) ? ldn.GetString() : null;

            SlskdDownloadItem? item = GetItemsForDef(definitionId)
                .FirstOrDefault(i => i.Username == username &&
                                     string.Equals(i.SlskdDownloadDirectory?.Directory, remoteDir, StringComparison.OrdinalIgnoreCase));

            if (item != null)
            {
                item.ConfirmedSubdirectory = SlskdPathResolver.MakeRelativeToDownloads(settings.DownloadPath, localDir)
                    ?? item.ConfirmedSubdirectory;

                _logger.Trace($"[def={definitionId}] Event DownloadDirectoryComplete: {remoteDir} by {username} -> {item.ConfirmedSubdirectory ?? "<unresolved>"}");

                SlskdUserTransfers? userTransfers = await _apiClient.GetUserTransfersAsync(settings, username);
                if (userTransfers != null)
                    ProcessUserTransfers(definitionId, settings, userTransfers, new ConcurrentDictionary<string, bool>(), settings.GetDestinationConfig());
            }
        }
        else if (record.Type == SlskdEventTypes.DownloadFileComplete && _logger.IsTraceEnabled)
        {
            using JsonDocument doc = JsonDocument.Parse(record.Data);
            if (doc.RootElement.TryGetProperty("transfer", out JsonElement transferEl))
            {
                string filename = transferEl.TryGetProperty("filename", out JsonElement fn) ? fn.GetString() ?? "" : "";
                string username = transferEl.TryGetProperty("username", out JsonElement un) ? un.GetString() ?? "" : "";
                _logger.Trace($"[def={definitionId}] Event DownloadFileComplete: {Path.GetFileName(filename)} by {username}");
            }
        }
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

    private async Task RemoveItemFilesAsync(SlskdDownloadItem item, SlskdProviderSettings settings)
    {
        List<SlskdDownloadFile> files = item.SlskdDownloadDirectory?.Files ?? [];
        if (files.Count == 0 || item.Username == null)
            return;

        await Task.WhenAll(files.Select(async file =>
        {
            if (SlskdFileState.GetStatus(file.State) != DownloadItemStatus.Completed)
            {
                await _apiClient.DeleteTransferAsync(settings, item.Username, file.Id);
                await Task.Delay(1000);
            }
            await _apiClient.DeleteTransferAsync(settings, item.Username, file.Id, remove: true);

            try
            {
                string fileName = Path.GetFileName(file.Filename.Replace('\\', '/'));
                string localFilePath = Path.Combine(GetLocalFolderPath(item, settings), fileName);

                if (_diskProvider.FileExists(localFilePath))
                {
                    _diskProvider.DeleteFile(localFilePath);
                    _logger.Debug($"Deleted local file: {localFilePath}");
                }
                else
                {
                    _logger.Trace($"Local file not found or path not accessible, skipping deletion: {Path.GetFileName(file.Filename)}");
                }
            }
            catch (Exception ex)
            {
                _logger.Trace(ex, $"Could not access local file for {Path.GetFileName(file.Filename)}: {ex.Message}");
            }

            _logger.Trace($"Removed transfer {file.Id}");
        }));
    }

    private static string? GetMultiDiscDestination(IEnumerable<string> filenames)
    {
        HashSet<string> parents = filenames
            .Select(f => { int i = f.LastIndexOf('\\'); return i > 0 ? f[..i] : null; })
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (parents.Count < 2)
            return null;

        HashSet<string> merged = parents
            .Select(SlskdTextProcessor.GetMergedDirectoryKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (merged.Count != 1)
            return null;

        string parent = merged.Single();
        int index = parent.LastIndexOfAny(['\\', '/']);
        string name = index >= 0 ? parent[(index + 1)..] : parent;

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private SlskdDownloadItem? FindItemContainingFiles(int definitionId, string username, SlskdDownloadDirectory dir)
    {
        List<string> filenames = dir.Files?.Select(f => f.Filename).OfType<string>().ToList() ?? [];
        if (filenames.Count == 0)
            return null;

        return GetItemsForDef(definitionId).FirstOrDefault(item =>
            (item.Username == null || string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase)) &&
            item.FileData.Count >= filenames.Count &&
            filenames.All(f => item.FileData.Any(d => string.Equals(d.Filename, f, StringComparison.OrdinalIgnoreCase))));
    }

    private string GetLocalFolderPath(SlskdDownloadItem item, SlskdProviderSettings settings) =>
        _remotePathMappingService
            .RemapRemoteToLocal(settings.Host, item.GetFullFolderPath(new OsPath(settings.DownloadPath)))
            .FullPath;

    private async Task CleanStaleDirectoriesAsync(string directoryPath, string localPath, SlskdProviderSettings settings)
    {
        try
        {
            await Task.Delay(1000);

            List<SlskdUserTransfers> all = await _apiClient.GetAllTransfersAsync(settings);
            bool hasRemaining = all.SelectMany(u => u.Directories)
                .Any(d => d.Directory.Equals(directoryPath, StringComparison.OrdinalIgnoreCase));

            if (hasRemaining)
            {
                _logger.Trace($"Directory {directoryPath} still has active downloads: skipping cleanup");
                return;
            }

            if (_diskProvider.FolderExists(localPath))
            {
                _logger.Debug($"Removing stale directory: {localPath}");
                _diskProvider.DeleteFolder(localPath, true);

                string? parent = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(parent) && _diskProvider.FolderExists(parent) && _diskProvider.FolderEmpty(parent))
                {
                    _logger.Info($"Removing empty parent directory: {parent}");
                    _diskProvider.DeleteFolder(parent, true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Error cleaning stale directories for path: {directoryPath}");
        }
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

    private static string ExtractUsernameFromPath(string path)
    {
        string[] parts = path.TrimEnd('/').Split('/');
        return Uri.UnescapeDataString(parts[^1]);
    }

    private static List<(string Filename, long Size)> ParseFilesFromSource(string source)
    {
        using JsonDocument doc = JsonDocument.Parse(source);
        return doc.RootElement.EnumerateArray()
            .Select(el => (
                Filename: el.TryGetProperty("Filename", out JsonElement fn) ? fn.GetString() ?? "" : "",
                Size: el.TryGetProperty("Size", out JsonElement sz) ? sz.GetInt64() : 0L
            ))
            .ToList();
    }

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
