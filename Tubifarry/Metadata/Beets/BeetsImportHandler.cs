using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace Tubifarry.Metadata.Beets
{
    public sealed class BeetsImportHandler(IMetadataFactory metadataFactory, IMediaFileService mediaFileService, IBeetsService beetsService, Logger logger) : IHandle<AlbumImportedEvent>
    {
        private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public void Handle(AlbumImportedEvent message)
        {
            BeetsSettings? settings = metadataFactory.GetEnabledBeetsSettings();

            if (settings == null)
                return;

            List<string> albumFiles = mediaFileService.GetFilesByAlbum(message.Album.Id)
                .Select(f => f.Path)
                .Concat(message.ImportedTracks.Select(t => t.Path))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToList();

            string? artistFolder = message.Artist.Path;
            string? albumFolder = GetCommonFolder(albumFiles);

            if (albumFolder != null && !string.IsNullOrWhiteSpace(artistFolder) && IsSameFolder(albumFolder, artistFolder))
            {
                logger.Info("Skipping beets for '{0}': its files are stored directly in the artist folder. beets needs one folder per album, enable album folders in Lidarr's track naming settings.", message.Album.Title);
                return;
            }

            if (albumFolder == null || string.IsNullOrWhiteSpace(artistFolder) || !Path.IsPathRooted(albumFolder) || !IsStrictlyInside(albumFolder, artistFolder))
            {
                logger.Debug("No album folder inside the artist folder was found for '{0}', skipping beets", message.Album.Title);
                return;
            }

            HashSet<string> knownFiles = new(albumFiles, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            int foreignFiles = Directory.EnumerateFiles(albumFolder, "*", SearchOption.AllDirectories)
                .Count(file => MediaFileExtensions.Extensions.Contains(Path.GetExtension(file)) && !knownFiles.Contains(file));

            if (foreignFiles > 0)
            {
                logger.Warn("Skipping beets for '{0}': '{1}' also holds {2} audio file(s) of other albums. Use a separate folder per album in Lidarr's naming settings.", message.Album.Title, albumFolder, foreignFiles);
                return;
            }

            beetsService.Enqueue(albumFolder, message.Album.Title, settings);
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
