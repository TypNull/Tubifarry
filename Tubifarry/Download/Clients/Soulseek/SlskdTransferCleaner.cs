using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.RemotePathMappings;
using Tubifarry.Download.Clients.Soulseek.Models;

namespace Tubifarry.Download.Clients.Soulseek;

public class SlskdTransferCleaner(ISlskdApiClient apiClient, SlskdLocalFiles localFiles, IRemotePathMappingService remotePathMappingService, IDiskProvider diskProvider, Logger logger)
{
    private readonly ISlskdApiClient _apiClient = apiClient;
    private readonly SlskdLocalFiles _localFiles = localFiles;
    private readonly IRemotePathMappingService _remotePathMappingService = remotePathMappingService;
    private readonly IDiskProvider _diskProvider = diskProvider;
    private readonly Logger _logger = logger;

    public async Task RemoveAsync(SlskdDownloadItem item, IReadOnlyCollection<SlskdDownloadItem> otherItems, SlskdProviderSettings settings)
    {
        try
        {
            await RemoveItemFilesAsync(item, settings);

            if (settings.CleanStaleDirectories && !string.IsNullOrEmpty(item.SlskdDownloadDirectory?.Directory))
                await CleanStaleDirectoriesAsync(item, otherItems, settings);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Removing slskd download {item.ID} failed");
        }
    }

    private async Task RemoveItemFilesAsync(SlskdDownloadItem item, SlskdProviderSettings settings)
    {
        List<SlskdDownloadFile> files = item.SlskdDownloadDirectory?.Files ?? [];
        if (files.Count == 0 || item.Username == null)
            return;

        try
        {
            await Task.WhenAll(item.PostProcessTasks.ToList());
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, $"Post-processing of {item.ID} failed before removal");
        }

        await Task.WhenAll(files.Select(async file =>
        {
            bool completed = SlskdFileState.GetStatus(file.State) == DownloadItemStatus.Completed;
            try
            {
                if (!completed)
                {
                    await _apiClient.DeleteTransferAsync(settings, item.Username, file.Id);
                    await Task.Delay(1000);
                }
                await _apiClient.DeleteTransferAsync(settings, item.Username, file.Id, remove: true);
                _logger.Trace($"Removed transfer {file.Id}");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Removing slskd transfer {file.Id} ({Path.GetFileName(file.Filename)}) failed");
            }

            if (!completed)
                return;

            try
            {
                string? localFilePath = _localFiles.Resolve(item, _localFiles.GetRoot(settings), _localFiles.GetFolders(item, settings, file), file);

                if (localFilePath != null)
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
        }));
    }

    private string GetLocalFolderPath(SlskdDownloadItem item, SlskdProviderSettings settings) =>
        _remotePathMappingService
            .RemapRemoteToLocal(settings.Host, item.GetFullFolderPath(new OsPath(settings.DownloadPath)))
            .FullPath;

    private async Task CleanStaleDirectoriesAsync(SlskdDownloadItem item, IReadOnlyCollection<SlskdDownloadItem> otherItems, SlskdProviderSettings settings)
    {
        string directoryPath = item.SlskdDownloadDirectory?.Directory ?? string.Empty;
        try
        {
            OsPath root = _localFiles.GetRoot(settings);
            string localPath = GetLocalFolderPath(item, settings);
            if (!SlskdLocalFiles.IsUnderRoot(localPath, root))
            {
                _logger.Debug($"Not cleaning {localPath}: it is not inside the download folder");
                return;
            }

            string normalized = SlskdLocalFiles.NormalizeFullPath(localPath);
            if (otherItems.Any(i => UsesFolder(i, normalized, settings)))
            {
                _logger.Debug($"Not cleaning {localPath}: other downloads still use it");
                return;
            }

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
                HashSet<string> names = SlskdGrabMatcher.ToNameSet(item.FileData.Select(f => SlskdFolderNaming.GetFileName(f.Filename))
                    .Concat(item.LocalFiles.Values.Select(SlskdFolderNaming.GetFileName)));
                List<string> foreign = _diskProvider.GetFiles(localPath, true)
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .Where(file => !names.Contains(file) && !names.Any(name => SlskdLocalFiles.IsRenamedCopy(file, name)))
                    .ToList();

                if (foreign.Count > 0)
                {
                    _logger.Debug($"Not cleaning {localPath}: it contains {foreign.Count} files that do not belong to {item.ID}");
                    return;
                }

                _logger.Debug($"Removing stale directory: {localPath}");
                _diskProvider.DeleteFolder(localPath, true);

                string? parent = Path.GetDirectoryName(normalized);

                if (!string.IsNullOrEmpty(parent)
                    && _diskProvider.FolderExists(parent)
                    && _diskProvider.FolderEmpty(parent)
                    && SlskdLocalFiles.IsUnderRoot(parent, root))
                {
                    _logger.Info($"Removing empty parent directory: {parent}");
                    _diskProvider.DeleteFolder(parent, false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Error cleaning stale directories for path: {directoryPath}");
        }
    }

    private bool UsesFolder(SlskdDownloadItem other, string folder, SlskdProviderSettings settings)
    {
        try
        {
            string otherFolder = SlskdLocalFiles.NormalizeFullPath(GetLocalFolderPath(other, settings));
            return string.Equals(otherFolder, folder, StringComparison.OrdinalIgnoreCase) ||
                otherFolder.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;
        }
    }
}
