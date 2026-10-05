using NzbDrone.Core.MediaFiles;

namespace Tubifarry.Metadata.Beets
{
    public enum BeetsAlbumFolderProblem
    {
        None,
        ArtistFolder,
        OutsideArtistFolder,
        ForeignFiles
    }

    public sealed record BeetsAlbumFolder(string? Folder, BeetsAlbumFolderProblem Problem, int ForeignFiles = 0)
    {
        private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public static BeetsAlbumFolder Resolve(IEnumerable<string> albumFiles, string? artistFolder)
        {
            List<string> files = albumFiles.Distinct(PathComparer).ToList();
            string? albumFolder = GetCommonFolder(files);

            if (albumFolder != null && !string.IsNullOrWhiteSpace(artistFolder) && IsSameFolder(albumFolder, artistFolder))
                return new BeetsAlbumFolder(albumFolder, BeetsAlbumFolderProblem.ArtistFolder);

            if (albumFolder == null || string.IsNullOrWhiteSpace(artistFolder) || !Path.IsPathRooted(albumFolder) || !IsStrictlyInside(albumFolder, artistFolder))
                return new BeetsAlbumFolder(albumFolder, BeetsAlbumFolderProblem.OutsideArtistFolder);

            HashSet<string> knownFiles = new(files, PathComparer);
            int foreignFiles = Directory.Exists(albumFolder)
                ? Directory.EnumerateFiles(albumFolder, "*", SearchOption.AllDirectories).Count(file => MediaFileExtensions.Extensions.Contains(Path.GetExtension(file)) && !knownFiles.Contains(file))
                : 0;

            return foreignFiles > 0
                ? new BeetsAlbumFolder(albumFolder, BeetsAlbumFolderProblem.ForeignFiles, foreignFiles)
                : new BeetsAlbumFolder(albumFolder, BeetsAlbumFolderProblem.None);
        }

        private static bool IsSameFolder(string folder, string other) =>
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)), PathComparison);

        private static bool IsStrictlyInside(string folder, string parent)
        {
            string normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            string normalizedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

            return normalizedFolder.StartsWith(normalizedParent + Path.DirectorySeparatorChar, PathComparison);
        }

        private static string? GetCommonFolder(IEnumerable<string> trackPaths)
        {
            List<string[]> folders = trackPaths
                .Select(Path.GetDirectoryName)
                .Where(folder => !string.IsNullOrEmpty(folder))
                .Distinct(StringComparer.Ordinal)
                .Select(folder => folder!.Split(Path.DirectorySeparatorChar))
                .ToList();

            if (folders.Count == 0)
                return null;

            int length = folders.Min(segments => segments.Length);
            int common = 0;
            while (common < length && folders.All(segments => string.Equals(segments[common], folders[0][common], PathComparison)))
                common++;

            string folder = string.Join(Path.DirectorySeparatorChar, folders[0][..common]);
            return string.IsNullOrEmpty(folder) ? null : folder;
        }
    }
}
