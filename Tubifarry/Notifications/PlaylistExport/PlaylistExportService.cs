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
    IHandleAsync<CommandExecutedEvent>,
    IHandleAsync<ProviderAddedEvent<IImportList>>,
    IHandleAsync<ProviderUpdatedEvent<IImportList>>,
    IHandleAsync<ProviderDeletedEvent<IImportList>>
{
    private const string SnapshotKey = "playlistExport.snapshots";
    private const string LastGeneratedKey = "playlistExport.lastGenerated";

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

    /// <summary>
    /// Regenerates off the command heartbeat, the only periodic hook a plugin has.
    /// </summary>
    /// <remarks>
    /// A plugin cannot register a scheduled task: TaskManager builds its list from a fixed
    /// set of command types on startup and deletes any stored task outside it.
    ///
    /// CommandExecutedEvent rather than ImportListSyncCompleteEvent, which looks like the
    /// natural hook but is not published when a sync has nothing to process, and a sync has
    /// nothing to process whenever every list is inside its refresh interval. This one is
    /// published from a finally for every command, so RefreshMonitoredDownloads alone gives
    /// a tick a minute. The minimum interval is what makes that affordable, and it is
    /// claimed before the work starts so two ticks cannot generate at once.
    /// </remarks>
    public void HandleAsync(CommandExecutedEvent message)
    {
        foreach (PlaylistExportNotification notification in _notificationFactory.Value
            .GetAvailableProviders().OfType<PlaylistExportNotification>())
        {
            GeneratePlaylists((PlaylistExportSettings)notification.Definition.Settings);
        }
    }
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
        int order = 7;

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
        if (!DueForGeneration(settings))
            return;

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

            PlaylistSnapshot? snapshot = GetOrRefreshSnapshot(listId, allLists, snapshots);
            if (snapshot == null)
            {
                _logger.Warn($"No snapshot for list {listId}: the fetch returned nothing.");
                continue;
            }

            // One file per source playlist. Items from a list that does not name a
            // playlist all land under the list's own name, which is one file for the
            // whole list, as before.
            Dictionary<string, List<TrackFile>> byPlaylist = [];

            foreach (PlaylistItem item in snapshot.Items)
            {
                string playlistName = string.IsNullOrWhiteSpace(item.PlaylistName)
                    ? snapshot.ListName
                    : item.PlaylistName;

                if (!byPlaylist.TryGetValue(playlistName, out List<TrackFile>? files))
                    byPlaylist[playlistName] = files = [];

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

            foreach ((string playlistName, List<TrackFile> files) in byPlaylist)
                WriteM3u8(outputPath, playlistName, files, settings.UseRelativePaths);
        }
    }

    /// <summary>
    /// Returns the stored snapshot for a list, fetching one first when it is absent or
    /// older than the list's own refresh interval.
    /// </summary>
    /// <remarks>
    /// Nothing else in the pipeline writes snapshots, and the export runs on album
    /// import, which is not a fetch. Without this the store stays empty and no list
    /// ever produces a file. The interval bounds it, so a burst of imports does not
    /// become one upstream fetch per album.
    /// </remarks>
    private PlaylistSnapshot? GetOrRefreshSnapshot(
        int listId,
        List<IImportList> allLists,
        Dictionary<int, PlaylistSnapshot> snapshots)
    {
        snapshots.TryGetValue(listId, out PlaylistSnapshot? snapshot);

        IImportList? list = allLists.FirstOrDefault(l => l.Definition.Id == listId);
        if (list == null)
        {
            _logger.Warn($"Import list ID {listId} not found");
            return snapshot;
        }

        if (snapshot != null && DateTime.UtcNow - snapshot.FetchedAt < list.MinRefreshInterval)
            return snapshot;

        FetchAndStore(listId);
        return GetSnapshots().GetValueOrDefault(listId) ?? snapshot;
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

    // No BOM: Encoding.UTF8 puts three bytes in front of #EXTM3U, so the first
    // line stops matching the header a strict m3u reader looks for.
    private static readonly UTF8Encoding _utf8NoBom = new(false);

    private void WriteM3u8(string outputPath, string listName, List<TrackFile> files, bool useRelative)
    {
        List<TrackFile> present = files.Where(f => File.Exists(f.Path)).ToList();
        if (present.Count == 0)
        {
            // A playlist whose tracks are all missing locally would otherwise be
            // written as a header and imported as an empty playlist.
            _logger.Debug($"Skipping '{listName}': no local files for any of its {files.Count} track(s)");
            return;
        }

        string filename = SanitizeFilename(listName) + ".m3u8";
        string fullPath = Path.Combine(outputPath, filename);

        List<string> lines = ["#EXTM3U", $"#PLAYLIST:{listName}"];

        foreach (TrackFile tf in present)
        {
            string displayName = Path.GetFileNameWithoutExtension(tf.Path);
            string trackPath = useRelative
                ? Path.GetRelativePath(outputPath, tf.Path)
                : tf.Path;
            lines.Add($"#EXTINF:-1,{displayName}");
            lines.Add(trackPath);
        }

        File.WriteAllLines(fullPath, lines, _utf8NoBom);
        _logger.Info($"Written {present.Count} track(s) to '{fullPath}'");
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

    private bool DueForGeneration(PlaylistExportSettings settings)
    {
        long lastTicks = _pluginSettings.GetValue<long>(LastGeneratedKey);
        DateTime last = lastTicks == 0 ? DateTime.MinValue : new DateTime(lastTicks, DateTimeKind.Utc);
        TimeSpan interval = TimeSpan.FromHours(Math.Max(settings.MinimumInterval, 0));

        if (DateTime.UtcNow - last < interval)
            return false;

        _pluginSettings.SetValue(LastGeneratedKey, DateTime.UtcNow.Ticks);
        return true;
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
