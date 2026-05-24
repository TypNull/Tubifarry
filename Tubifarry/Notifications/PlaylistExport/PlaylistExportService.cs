using Lidarr.Http.ClientSchema;
using NLog;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider.Events;
using System.Text;
using System.Text.RegularExpressions;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Notifications.PlaylistExport;

public interface IPlaylistExportService
{
    void RefreshSchema();
    void FetchAndStore(int listId);
    void GeneratePlaylists(PlaylistExportSettings settings);
    string? DetectCommonMusicPath();
}

public sealed partial class PlaylistExportService : IPlaylistExportService,
    IHandleAsync<ApplicationStartedEvent>,
    IHandleAsync<ProviderAddedEvent<IImportList>>,
    IHandleAsync<ProviderUpdatedEvent<IImportList>>,
    IHandleAsync<ProviderDeletedEvent<IImportList>>
{
    private const string SnapshotKey = "playlistExport.snapshots";

    private readonly IImportListFactory _importListFactory;
    private readonly IFetchAndParseImportList _fetchAndParse;
    private readonly IPluginSettings _pluginSettings;
    private readonly IArtistService _artistService;
    private readonly IAlbumService _albumService;
    private readonly ITrackService _trackService;
    private readonly IMediaFileService _mediaFileService;
    private readonly Lazy<INotificationFactory> _notificationFactory;
    private readonly Logger _logger;

    public PlaylistExportService(
        IImportListFactory importListFactory,
        IFetchAndParseImportList fetchAndParse,
        IPluginSettings pluginSettings,
        IArtistService artistService,
        IAlbumService albumService,
        ITrackService trackService,
        IMediaFileService mediaFileService,
        Lazy<INotificationFactory> notificationFactory,
        Logger logger)
    {
        _importListFactory = importListFactory;
        _fetchAndParse = fetchAndParse;
        _pluginSettings = pluginSettings;
        _artistService = artistService;
        _albumService = albumService;
        _trackService = trackService;
        _mediaFileService = mediaFileService;
        _notificationFactory = notificationFactory;
        _logger = logger;
    }

    public void HandleAsync(ApplicationStartedEvent message) => RefreshSchema();
    public void HandleAsync(ProviderAddedEvent<IImportList> message) => RefreshSchema();
    public void HandleAsync(ProviderUpdatedEvent<IImportList> message) => RefreshSchema();

    public void HandleAsync(ProviderDeletedEvent<IImportList> message)
    {
        Dictionary<int, PlaylistSnapshot> snapshots = GetSnapshots();

        if (snapshots.Remove(message.ProviderId, out PlaylistSnapshot? deleted))
        {
            SaveSnapshots(snapshots);

            foreach (INotification n in _notificationFactory.Value.GetAvailableProviders()
                .OfType<PlaylistExportNotification>())
            {
                PlaylistExportSettings s = (PlaylistExportSettings)n.Definition.Settings;
                if (!s.CleanupOnRemove || string.IsNullOrEmpty(s.OutputPath))
                    continue;

                string m3u8Path = Path.Combine(s.OutputPath, $"{SanitizeFilename(deleted.ListName)}.m3u8");
                if (File.Exists(m3u8Path))
                {
                    _logger.Debug($"Deleting {m3u8Path} (import list removed)");
                    File.Delete(m3u8Path);
                }
            }
        }

        RefreshSchema();
    }

    public void RefreshSchema()
    {
        List<IImportList> allLists = _importListFactory.GetAvailableProviders();
        int order = 6;

        List<FieldMapping> dynamicMappings = [];
        foreach (IImportList l in allLists)
        {
            string key = $"list_{l.Definition.Id}";
            dynamicMappings.Add(new FieldMapping
            {
                Field = new Field
                {
                    Name = key,
                    Label = l.Definition.Name,
                    Type = "checkbox",
                    HelpText = l is IPlaylistTrackSource
                        ? $"Supports track-level data, generates a track-specific playlist for '{l.Definition.Name}'"
                        : $"Album-level only, generates a playlist of all local tracks for '{l.Definition.Name}'",
                    Order = order++,
                },
                PropertyType = typeof(bool),
                GetterFunc = m => ((PlaylistExportSettings)m).GetBoolState(key),
                SetterFunc = (m, v) => ((PlaylistExportSettings)m).SetBoolState(key, Convert.ToBoolean(v)),
            });
        }

        DynamicSchemaInjector.InjectDynamic<PlaylistExportSettings>(dynamicMappings, "list_");
        _logger.Debug($"Schema refreshed with {allLists.Count} import list(s)");
    }

    public string? DetectCommonMusicPath()
    {
        List<string> paths = _artistService.GetAllArtists()
            .Select(a => a.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        if (paths.Count == 0)
            return null;

        return FindCommonRoot(paths);
    }

    public void FetchAndStore(int listId)
    {
        IImportList? list = _importListFactory.GetAvailableProviders()
            .FirstOrDefault(l => l.Definition.Id == listId);

        if (list == null)
        {
            _logger.Warn($"Import list ID {listId} not found");
            return;
        }

        _logger.Debug($"Fetching items from '{list.Definition.Name}'");

        List<PlaylistItem> items = list is IPlaylistTrackSource trackSource
            ? trackSource.FetchTrackLevelItems()
            : FetchAlbumLevelItems(list);

        Dictionary<int, PlaylistSnapshot> snapshots = GetSnapshots();
        snapshots[listId] = new PlaylistSnapshot(list.Definition.Name, items, DateTime.UtcNow);
        SaveSnapshots(snapshots);

        _logger.Info($"Stored {items.Count} item(s) for '{list.Definition.Name}'");
    }

    public void GeneratePlaylists(PlaylistExportSettings settings)
    {
        string? outputPath = settings.AutoDetectOutputPath
            ? DetectCommonMusicPath()
            : settings.OutputPath;

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            _logger.Warn("OutputPath not configured and auto-detect found nothing, skipping generation");
            return;
        }

        List<int> selectedIds = settings.GetSelectedListIds().ToList();
        if (selectedIds.Count == 0) return;

        Dictionary<int, PlaylistSnapshot> snapshots = GetSnapshots();
        PlaylistTrackMode trackMode = settings.GetTrackMode();

        List<IImportList> allLists = _importListFactory.GetAvailableProviders();
        HashSet<int> trackSourceIds = allLists
            .Where(l => l is IPlaylistTrackSource)
            .Select(l => l.Definition.Id)
            .ToHashSet();

        List<Artist> allArtists = _artistService.GetAllArtists();
        Dictionary<string, Artist> artistByMbId = allArtists
            .ToDictionary(a => a.ForeignArtistId, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Artist> artistByName = allArtists
            .GroupBy(a => Normalize(a.Name))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        Dictionary<string, Album> albumByMbId = _albumService.GetAllAlbums()
            .ToDictionary(a => a.ForeignAlbumId, StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(outputPath);

        foreach (int listId in selectedIds)
        {
            if (trackMode == PlaylistTrackMode.TrackDataOnly && !trackSourceIds.Contains(listId))
            {
                _logger.Debug($"Skipping list {listId}: TrackDataOnly mode and list does not support track-level data");
                continue;
            }

            if (!snapshots.TryGetValue(listId, out PlaylistSnapshot? snapshot))
            {
                _logger.Warn($"No snapshot for list {listId}: fetch has not run yet for this list.");
                continue;
            }

            List<TrackFile> files = [];
            foreach (PlaylistItem item in snapshot.Items)
            {
                bool useTrackLevel = trackMode != PlaylistTrackMode.AlbumDataOnly
                    && (item.TrackTitle != null || item.ForeignRecordingId != null);

                if (!useTrackLevel)
                {
                    files.AddRange(GetAlbumOrArtistFiles(item, albumByMbId, artistByMbId, artistByName));
                    continue;
                }

                Artist? artist = ResolveArtist(item, artistByMbId, artistByName);
                if (artist == null) continue;

                if (item.ForeignRecordingId != null)
                {
                    Track? t = _trackService.GetTracksByArtist(artist.Id)
                        .FirstOrDefault(t => string.Equals(t.ForeignRecordingId, item.ForeignRecordingId,
                            StringComparison.OrdinalIgnoreCase) && t.TrackFileId > 0);
                    if (t != null)
                        files.Add(_mediaFileService.Get(t.TrackFileId));
                }
                else if (item.TrackTitle != null)
                {
                    IEnumerable<Track> candidates = item.AlbumMusicBrainzId != null
                        && albumByMbId.TryGetValue(item.AlbumMusicBrainzId, out Album? alb)
                            ? _trackService.GetTracksByAlbum(alb.Id)
                            : _trackService.GetTracksByArtist(artist.Id);

                    Track? t = candidates.FirstOrDefault(t =>
                        Normalize(t.Title) == Normalize(item.TrackTitle) && t.TrackFileId > 0);
                    if (t != null)
                        files.Add(_mediaFileService.Get(t.TrackFileId));
                }
            }

            WriteM3u8(outputPath, snapshot.ListName, files, settings.UseRelativePaths);
        }
    }

    private List<PlaylistItem> FetchAlbumLevelItems(IImportList list)
    {
        List<ImportListItemInfo> raw = _fetchAndParse.FetchSingleList((ImportListDefinition)list.Definition);

        return raw
            .Where(i => !string.IsNullOrEmpty(i.ArtistMusicBrainzId))
            .Select(i => new PlaylistItem(
                i.ArtistMusicBrainzId,
                string.IsNullOrEmpty(i.AlbumMusicBrainzId) ? null : i.AlbumMusicBrainzId,
                i.Artist ?? "",
                string.IsNullOrEmpty(i.Album) ? null : i.Album))
            .DistinctBy(i => (i.ArtistMusicBrainzId, i.AlbumMusicBrainzId))
            .ToList();
    }

    private IEnumerable<TrackFile> GetAlbumOrArtistFiles(
        PlaylistItem item,
        Dictionary<string, Album> albumByMbId,
        Dictionary<string, Artist> artistByMbId,
        Dictionary<string, Artist> artistByName)
    {
        if (item.AlbumMusicBrainzId != null
            && albumByMbId.TryGetValue(item.AlbumMusicBrainzId, out Album? album))
        {
            return _mediaFileService.GetFilesByAlbum(album.Id);
        }

        Artist? artist = ResolveArtist(item, artistByMbId, artistByName);
        if (artist != null)
            return _mediaFileService.GetFilesByArtist(artist.Id);

        return [];
    }

    private static Artist? ResolveArtist(
        PlaylistItem item,
        Dictionary<string, Artist> byMbId,
        Dictionary<string, Artist> byName)
    {
        if (!string.IsNullOrEmpty(item.ArtistMusicBrainzId)
            && byMbId.TryGetValue(item.ArtistMusicBrainzId, out Artist? a))
            return a;

        string key = Normalize(item.ArtistName);
        return string.IsNullOrEmpty(key) ? null : byName.GetValueOrDefault(key);
    }

    private static string Normalize(string? s) =>
        s == null ? "" : NormalizeRegex().Replace(s.ToLowerInvariant(), "");

    private void WriteM3u8(string outputPath, string listName, List<TrackFile> files, bool useRelative)
    {
        string filename = SanitizeFilename(listName) + ".m3u8";
        string fullPath = Path.Combine(outputPath, filename);

        List<string> lines = ["#EXTM3U", $"#PLAYLIST:{listName}"];

        foreach (TrackFile tf in files.Where(f => File.Exists(f.Path)))
        {
            string displayName = Path.GetFileNameWithoutExtension(tf.Path);
            string trackPath = useRelative
                ? Path.GetRelativePath(outputPath, tf.Path)
                : tf.Path;
            lines.Add($"#EXTINF:-1,{displayName}");
            lines.Add(trackPath);
        }

        File.WriteAllLines(fullPath, lines, Encoding.UTF8);
        _logger.Info($"Written {files.Count} track(s) to '{fullPath}'");
    }

    private static string? FindCommonRoot(List<string> paths)
    {
        if (paths.Count == 0) return null;
        if (paths.Count == 1) return Path.GetDirectoryName(paths[0]);

        List<string[]> segments = [.. paths.Select(p => Path.GetFullPath(p).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))];

        int minLen = segments.Min(s => s.Length);
        List<string> common = [];

        for (int i = 0; i < minLen; i++)
        {
            string seg = segments[0][i];
            if (segments.All(s => s[i].Equals(seg, StringComparison.OrdinalIgnoreCase)))
                common.Add(seg);
            else
                break;
        }

        if (common.Count == 0) return null;

        string root = string.Join(Path.DirectorySeparatorChar, common);
        return common[0].EndsWith(':') ? root + Path.DirectorySeparatorChar : root;
    }

    private Dictionary<int, PlaylistSnapshot> GetSnapshots() =>
        _pluginSettings.GetValue<Dictionary<int, PlaylistSnapshot>>(SnapshotKey) ?? [];

    private void SaveSnapshots(Dictionary<int, PlaylistSnapshot> snapshots) =>
        _pluginSettings.SetValue(SnapshotKey, snapshots);

    private static string SanitizeFilename(string name) =>
        Path.GetInvalidFileNameChars().Aggregate(name, (s, c) => s.Replace(c, '_'));

    [GeneratedRegex(@"[^\w]")]
    private static partial Regex NormalizeRegex();
}
