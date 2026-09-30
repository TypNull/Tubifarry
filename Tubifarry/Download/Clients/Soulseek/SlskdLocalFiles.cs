using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.RemotePathMappings;
using System.Text.RegularExpressions;
using Tubifarry.Download.Clients.Soulseek.Models;

namespace Tubifarry.Download.Clients.Soulseek;

public class SlskdLocalFiles(IRemotePathMappingService remotePathMappingService, IDiskProvider diskProvider)
{
    private readonly IRemotePathMappingService _remotePathMappingService = remotePathMappingService;
    private readonly IDiskProvider _diskProvider = diskProvider;

    public OsPath GetRoot(SlskdProviderSettings settings) =>
        _remotePathMappingService.RemapRemoteToLocal(settings.Host, new OsPath(settings.DownloadPath));

    public static string NormalizeFullPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static bool IsUnderRoot(string path, OsPath root) =>
        NormalizeFullPath(path).StartsWith(NormalizeFullPath(root.FullPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void Record(SlskdDownloadItem item, SlskdProviderSettings settings, string remoteFilename, string localFilename)
    {
        if (SlskdPathResolver.MakeRelativeToDownloads(settings.DownloadPath, localFilename) is { Length: > 0 } relative)
            item.LocalFiles.TryAdd(remoteFilename, relative);
    }

    public static IEnumerable<SlskdDownloadFile> GetCompletedFiles(SlskdDownloadItem item) =>
        item.FileStates.Values
            .Where(fs => fs.GetStatus() == DownloadItemStatus.Completed)
            .Select(fs => fs.File);

    public List<OsPath> GetFolders(SlskdDownloadItem item, SlskdProviderSettings settings, SlskdDownloadFile file)
    {
        OsPath root = GetRoot(settings);
        string leaf = SlskdFolderNaming.GetLeaf(SlskdFolderNaming.GetParentDirectory(file.Filename));
        string? patternFolder = settings.GetDestinationConfig() is { UsesDefaultPattern: false } config
            ? SlskdPathResolver.ResolveSubdirectory(config, item.Username ?? string.Empty, file.Filename, item.BatchId, item.BatchId != null ? item.ID : null)
            : null;

        List<OsPath?> folders =
        [
            SlskdFolderNaming.Combine(root, item.ConfirmedSubdirectory),
            SlskdFolderNaming.Combine(root, item.DerivedSubdirectory),
            SlskdFolderNaming.Combine(root, patternFolder),
            leaf.Length > 0 ? SlskdFolderNaming.Combine(root, leaf) : null,
        ];

        return folders.OfType<OsPath>().Distinct().ToList();
    }

    public string? Resolve(SlskdDownloadItem item, OsPath root, IEnumerable<OsPath> folders, SlskdDownloadFile file)
    {
        if (item.LocalFiles.TryGetValue(file.Filename, out string? relative) &&
            SlskdFolderNaming.Combine(root, relative) is OsPath recorded &&
            IsUnderRoot(recorded.FullPath, root) &&
            _diskProvider.FileExists(recorded.FullPath))
            return recorded.FullPath;

        string name = SlskdFolderNaming.GetFileName(file.Filename);
        if (!SlskdFolderNaming.IsSafeSegment(name))
            return null;

        foreach (OsPath folder in folders)
        {
            string expected = (folder + new OsPath(name)).FullPath;
            if (IsUnderRoot(expected, root) && _diskProvider.FileExists(expected) && HasSize(expected, file.Size, true))
                return expected;

            if (!_diskProvider.FolderExists(folder.FullPath))
                continue;

            List<string> candidates = _diskProvider.GetFiles(folder.FullPath, false)
                .Where(path => IsUnderRoot(path, root) && IsRenamedCopy(Path.GetFileName(path), name) && HasSize(path, file.Size, false))
                .ToList();

            if (candidates.Count == 1)
                return candidates[0];
        }

        return null;
    }

    public static bool IsRenamedCopy(string fileName, string expectedName) =>
        Regex.IsMatch(fileName, $"^{Regex.Escape(Path.GetFileNameWithoutExtension(expectedName))}_\\d+{Regex.Escape(Path.GetExtension(expectedName))}$", RegexOptions.IgnoreCase);

    public bool HasSize(string path, long size, bool allowUnknownSize)
    {
        if (size <= 0)
            return allowUnknownSize;

        try
        {
            return _diskProvider.GetFileSize(path) == size;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
