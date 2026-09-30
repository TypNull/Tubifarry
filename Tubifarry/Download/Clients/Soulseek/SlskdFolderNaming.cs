using NzbDrone.Common.Disk;
using Tubifarry.Indexers.Soulseek;

namespace Tubifarry.Download.Clients.Soulseek;

public static class SlskdFolderNaming
{
    public static bool IsSafeSegment(string segment) =>
        segment.Length > 0 && segment is not ("." or "..") && new OsPath(segment).Kind == OsPathKind.Unknown;

    public static OsPath? Combine(OsPath root, string? relative)
    {
        if (relative == null)
            return null;

        string[] segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return segments.All(IsSafeSegment)
            ? segments.Aggregate(root, (path, segment) => path + new OsPath(segment))
            : null;
    }

    public static string GetLeaf(string path) => path.Replace('/', '\\').TrimEnd('\\').Split('\\').Last() is var leaf && IsSafeSegment(leaf) ? leaf : string.Empty;

    public static string GetFileName(string? filename) => Path.GetFileName((filename ?? string.Empty).Replace('\\', '/'));

    public static string GetParentDirectory(string? filename) => SlskdTextProcessor.GetDirectoryFromFilename(filename?.Replace('/', '\\'));

    public static List<string> GetParentDirectories(IEnumerable<string?> filenames) =>
        filenames
            .Select(GetParentDirectory)
            .Where(parent => parent.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string SanitizeFolderName(string value) => string.Concat(value.Split(Path.GetInvalidFileNameChars())).Trim() is var name && IsSafeSegment(name) ? name : string.Empty;

    public static string? GetDownloadDestination(IEnumerable<string?> filenames, string? artist, string? album)
    {
        List<string> parents = GetParentDirectories(filenames);
        string? destination = GetMultiDiscDestination(parents);
        string? sourceLeaf = destination ?? (parents.Count == 1 ? GetLeaf(parents[0]) : null);
        if (sourceLeaf == null)
            return destination;

        if (GetSharedFolderDestination(sourceLeaf, artist, album) is string albumFolder)
            return albumFolder;

        return GetArtistPrefixedName(sourceLeaf, artist) ?? destination;
    }

    public static string? GetMultiDiscDestination(List<string> parents)
    {
        if (parents.Count < 2)
            return null;

        HashSet<string> merged = parents
            .Select(SlskdTextProcessor.GetMergedDirectoryKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (merged.Count != 1)
            return null;

        string name = SanitizeFolderName(GetLeaf(merged.Single()));
        return name.Length == 0 ? null : name;
    }

    private static string? GetSharedFolderDestination(string leaf, string? artist, string? album)
    {
        if (string.IsNullOrWhiteSpace(album))
            return null;

        string normAlbum = NormalizeName(album);
        if (normAlbum.Length < 3 || NormalizeName(leaf).Contains(normAlbum))
            return null;

        string sanitizedAlbum = SanitizeFolderName(album);
        string sanitizedArtist = string.IsNullOrWhiteSpace(artist) ? string.Empty : SanitizeFolderName(artist);
        if (sanitizedAlbum.Length == 0)
            return null;

        return sanitizedArtist.Length == 0 ? sanitizedAlbum : $"{sanitizedArtist} - {sanitizedAlbum}";
    }

    private static string? GetArtistPrefixedName(string leaf, string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(leaf))
            return null;

        string normLeaf = NormalizeName(leaf);
        string normArtist = NormalizeName(artist);
        if (normArtist.Length < 3 || normLeaf.Contains(normArtist))
            return null;

        string sanitizedArtist = SanitizeFolderName(artist);
        string sanitizedLeaf = SanitizeFolderName(leaf);
        return sanitizedArtist.Length == 0 || sanitizedLeaf.Length == 0 ? null : $"{sanitizedArtist} - {sanitizedLeaf}";
    }

    private static string NormalizeName(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
