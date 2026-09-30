using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using Tubifarry.Download.Clients.Soulseek.Models;
using Tubifarry.Indexers.Soulseek;

namespace Tubifarry.Download.Clients.Soulseek;

public class SlskdFolderPostProcessor(SlskdLocalFiles localFiles, IDiskProvider diskProvider, Logger logger)
{
    private const int MaxRenameAttempts = 10;
    private static readonly TimeSpan _renameRetryDelay = TimeSpan.FromSeconds(3);

    private readonly SlskdLocalFiles _localFiles = localFiles;
    private readonly IDiskProvider _diskProvider = diskProvider;
    private readonly Logger _logger = logger;

    public void Process(SlskdDownloadItem item, SlskdProviderSettings settings)
    {
        if (item.FolderProcessingDisabled)
            return;

        MergeSplitDiscFolders(item, settings);
        NormalizeAlbumFolderName(item, settings);
    }

    private void MergeSplitDiscFolders(SlskdDownloadItem item, SlskdProviderSettings settings)
    {
        if (item.BatchId != null || item.DiscMergeScheduled || !IsCompleted(item))
            return;

        List<string> parents = SlskdFolderNaming.GetParentDirectories(item.FileData.Select(f => f.Filename));
        if (parents.Count < 2)
            return;

        HashSet<string> albums = parents.Select(SlskdTextProcessor.GetMergedDirectoryKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (albums.Count != 1 || albums.SetEquals(parents))
            return;

        string albumLeaf = SlskdFolderNaming.GetLeaf(albums.Single());
        string album = SlskdFolderNaming.SanitizeFolderName(albumLeaf);
        if (album.Length == 0)
            return;

        OsPath root = _localFiles.GetRoot(settings);
        if (SlskdFolderNaming.Combine(root, album) is not OsPath albumPath)
            return;

        List<(string Leaf, string Name, SlskdDownloadFile File, List<OsPath> Folders)> discFiles = SlskdLocalFiles.GetCompletedFiles(item)
            .Select(f => (Leaf: SlskdFolderNaming.GetLeaf(SlskdFolderNaming.GetParentDirectory(f.Filename)), Name: SlskdFolderNaming.GetFileName(f.Filename), File: f))
            .Where(d => d.Leaf.Length > 0 && !d.Leaf.Equals(albumLeaf, StringComparison.OrdinalIgnoreCase) && SlskdFolderNaming.IsSafeSegment(d.Name))
            .Select(d => (d.Leaf, d.Name, d.File, Folders: _localFiles.GetFolders(item, settings, d.File).Prepend(root + new OsPath(d.Leaf)).Distinct().ToList()))
            .ToList();

        item.DiscMergeScheduled = true;

        item.PostProcessTasks.Add(Task.Run(() =>
        {
            try
            {
                if (!SlskdLocalFiles.IsUnderRoot(albumPath.FullPath, root))
                    throw new InvalidOperationException($"'{albumPath.FullPath}' is outside the download folder");

                if (!_diskProvider.FolderExists(albumPath.FullPath))
                    _diskProvider.CreateFolder(albumPath.FullPath);

                List<string> leftOver = [];
                List<string> missing = [];
                int placed = 0;
                foreach ((string leaf, string name, SlskdDownloadFile file, List<OsPath> folders) in discFiles)
                {
                    string prefixedName = $"{leaf} - {name}";
                    OsPath plain = albumPath + new OsPath(name);
                    OsPath prefixed = albumPath + new OsPath(prefixedName);
                    string? source = _localFiles.Resolve(item, root, folders, file);

                    if (source == null)
                    {
                        if (IsPresent(plain, file.Size) || IsPresent(prefixed, file.Size))
                            placed++;
                        else
                            missing.Add(name);
                        continue;
                    }

                    bool usePrefix = _diskProvider.FileExists(plain.FullPath);
                    OsPath target = usePrefix ? prefixed : plain;
                    if (_diskProvider.FileExists(target.FullPath) || !SlskdLocalFiles.IsUnderRoot(target.FullPath, root))
                    {
                        leftOver.Add(name);
                        continue;
                    }

                    _diskProvider.MoveFile(source, target.FullPath);
                    item.LocalFiles[file.Filename] = $"{album}/{(usePrefix ? prefixedName : name)}";
                    placed++;
                }

                foreach (string leaf in discFiles.Select(d => d.Leaf).Distinct(StringComparer.OrdinalIgnoreCase))
                    DeleteIfEmpty(root + new OsPath(leaf), root);

                if (missing.Count > 0)
                    _logger.Warn($"Disc merge into '{album}' for {item.ID}: {missing.Count} files no longer on disk ({string.Join(", ", missing)})");

                if (leftOver.Count > 0)
                {
                    item.PostProcessError = $"Disc merge into '{album}' left {leftOver.Count} files behind: {string.Join(", ", leftOver)}";
                    _logger.Error($"{item.PostProcessError} ({item.ID})");
                    return;
                }

                if (placed == 0)
                {
                    DeleteIfEmpty(albumPath, root);
                    _logger.Warn($"Disc merge into '{album}' for {item.ID} found none of its files; leaving the download folder unchanged");
                    return;
                }

                item.ConfirmedSubdirectory = album;
                _logger.Debug($"Merged {placed} files of {item.ID} into '{album}'");
            }
            catch (Exception ex)
            {
                item.PostProcessError = $"Disc merge into '{album}' failed: {ex.Message}";
                _logger.Error(ex, $"Disc merge failed for {item.ID}");
            }
        }));
    }

    private void NormalizeAlbumFolderName(SlskdDownloadItem item, SlskdProviderSettings settings)
    {
        if (item.BatchId != null || item.DiscMergeScheduled || item.FolderRenameScheduled ||
            item.DerivedSubdirectory != null || !IsCompleted(item) ||
            item.SlskdDownloadDirectory?.Directory is not { Length: > 0 } directory)
            return;

        string leaf = SlskdFolderNaming.GetLeaf(directory);
        if (leaf.Length == 0 || (item.ConfirmedSubdirectory != null && !item.ConfirmedSubdirectory.Equals(leaf, StringComparison.OrdinalIgnoreCase)))
            return;

        string? renamed = SlskdFolderNaming.GetDownloadDestination(item.FileData.Select(f => f.Filename), item.ReleaseInfo.Artist, item.ReleaseInfo.Album);
        if (renamed == null || renamed.Equals(leaf, StringComparison.OrdinalIgnoreCase))
            return;

        OsPath root = _localFiles.GetRoot(settings);
        if (SlskdFolderNaming.Combine(root, leaf) is not OsPath source || SlskdFolderNaming.Combine(root, renamed) is not OsPath target)
            return;

        item.FolderRenameScheduled = true;
        List<(SlskdDownloadFile File, string Name, List<OsPath> Folders)> files = SlskdLocalFiles.GetCompletedFiles(item)
            .Select(f => (File: f, Name: SlskdFolderNaming.GetFileName(f.Filename)))
            .Where(f => SlskdFolderNaming.IsSafeSegment(f.Name))
            .Select(f => (f.File, f.Name, Folders: _localFiles.GetFolders(item, settings, f.File).Prepend(source).Distinct().ToList()))
            .ToList();

        item.PostProcessTasks.Add(Task.Run(async () =>
        {
            try
            {
                List<(SlskdDownloadFile File, string Name, string? Source)> resolved;
                for (int attempt = 1; ; attempt++)
                {
                    resolved = files.Select(f => (f.File, f.Name, Source: _localFiles.Resolve(item, root, f.Folders, f.File))).ToList();
                    List<string> missing = resolved
                        .Where(r => r.Source == null && !IsPresent(target + new OsPath(r.Name), r.File.Size))
                        .Select(r => r.Name)
                        .ToList();

                    if (missing.Count == 0)
                        break;

                    if (attempt >= MaxRenameAttempts)
                    {
                        if (missing.Count == resolved.Count)
                        {
                            _logger.Warn($"Folder normalization for {item.ID} gave up after {attempt} attempts; none of its files were found. Leaving files in '{leaf}'");
                            return;
                        }

                        _logger.Warn($"Folder normalization for {item.ID}: {missing.Count} files not found after {attempt} attempts ({string.Join(", ", missing)}); moving the rest");
                        break;
                    }

                    await Task.Delay(_renameRetryDelay);
                }

                if (!SlskdLocalFiles.IsUnderRoot(target.FullPath, root))
                    throw new InvalidOperationException($"'{target.FullPath}' is outside the download folder");

                if (!_diskProvider.FolderExists(target.FullPath))
                    _diskProvider.CreateFolder(target.FullPath);

                List<string> collisions = [];
                int moved = 0;
                foreach ((SlskdDownloadFile file, string name, string? from) in resolved)
                {
                    OsPath to = target + new OsPath(name);
                    if (from == null || !SlskdLocalFiles.IsUnderRoot(to.FullPath, root))
                        continue;

                    if (_diskProvider.FileExists(to.FullPath))
                    {
                        if (!IsPresent(to, file.Size))
                            collisions.Add(name);
                        continue;
                    }

                    _diskProvider.MoveFile(from, to.FullPath);
                    item.LocalFiles[file.Filename] = $"{renamed}/{name}";
                    moved++;
                }

                DeleteIfEmpty(source, root);

                if (collisions.Count > 0)
                {
                    item.PostProcessError = $"Moving files to '{renamed}' failed: {collisions.Count} different files with the same name already exist ({string.Join(", ", collisions)})";
                    _logger.Error($"{item.PostProcessError} ({item.ID})");
                    return;
                }

                item.ConfirmedSubdirectory = renamed;
                _logger.Debug($"Moved {moved} files of {item.ID}: '{leaf}' -> '{renamed}'");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Folder normalization failed for {item.ID}; leaving files in place");
            }
        }));
    }

    private void DeleteIfEmpty(OsPath folder, OsPath root)
    {
        if (SlskdLocalFiles.IsUnderRoot(folder.FullPath, root) && _diskProvider.FolderExists(folder.FullPath) && _diskProvider.FolderEmpty(folder.FullPath))
            _diskProvider.DeleteFolder(folder.FullPath, false);
    }

    private bool IsPresent(OsPath path, long size) => _diskProvider.FileExists(path.FullPath) && _localFiles.HasSize(path.FullPath, size, true);

    private static bool IsCompleted(SlskdDownloadItem item) =>
        item.FileStates.Count > 0 && item.FileStates.Values.All(fs => fs.GetStatus() == DownloadItemStatus.Completed);
}
