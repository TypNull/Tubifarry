using Dapper;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;

namespace Tubifarry.Metadata.Lyrics
{
    /// <summary>
    /// Repository helper for querying track files and managing lyric file database entries.
    /// Provides efficient batch querying to avoid loading millions of records into memory.
    /// </summary>
    public sealed class TrackFileRepositoryHelper : BasicRepository<TrackFile>,
         IHandleAsync<AlbumImportedEvent>,
         IHandleAsync<ArtistScannedEvent>
    {
        private readonly ITrackRepository _trackRepository;
        private readonly ILyricFileService _lyricFileService;
        private readonly IExtraFileService<OtherExtraFile> _otherExtraFileService;
        private readonly IMetadataFileService _metadataFileService;
        private readonly Logger _logger;

        public TrackFileRepositoryHelper(
            IMainDatabase database,
            IEventAggregator eventAggregator,
            ITrackRepository trackRepository,
            ILyricFileService lyricFileService,
            IExtraFileService<OtherExtraFile> otherExtraFileService,
            IMetadataFileService metadataFileService,
            Logger logger)
            : base(database, eventAggregator)
        {
            _trackRepository = trackRepository;
            _lyricFileService = lyricFileService;
            _otherExtraFileService = otherExtraFileService;
            _metadataFileService = metadataFileService;
            _logger = logger;
        }

        public int GetTracksWithoutLrcFilesCount()
        {
            try
            {
                SqlBuilder builder = TracksWithoutLrcQuery().SelectCount();
                SqlBuilder.Template template = builder.AddPageCountTemplate(typeof(TrackFile));

                using System.Data.IDbConnection conn = _database.OpenConnection();
                return conn.ExecuteScalar<int>(template.RawSql, template.Parameters);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error counting tracks without LRC files");
                return 0;
            }
        }

        public List<TrackFile> GetTracksWithoutLrcFilesBatch(int offset, int limit)
        {
            try
            {
                SqlBuilder builder = TracksWithoutLrcQuery()
                    .GroupBy<TrackFile>(tf => tf.Id)
                    .OrderBy($@"""TrackFiles"".""Id"" ASC LIMIT {limit} OFFSET {offset}");

                List<TrackFile> trackFiles = Query(builder);

                foreach (TrackFile trackFile in trackFiles)
                {
                    List<Track> tracks = _trackRepository.GetTracksByFileId(trackFile.Id);
                    trackFile.Tracks = new LazyLoaded<List<Track>>(tracks);

                    if (tracks.Count > 0 && tracks[0].Artist?.Value != null)
                        trackFile.Artist = new LazyLoaded<Artist>(tracks[0].Artist.Value);
                }

                return trackFiles;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error querying tracks without LRC files (offset: {offset}, limit: {limit})");
                return [];
            }
        }

        public LyricFile? CreateAndUpsertLyricFile(Artist artist, TrackFile trackFile, string relativePath)
        {
            try
            {
                DeleteByPath(_otherExtraFileService, artist.Id, relativePath, "OtherExtraFiles");
                DeleteByPath(_metadataFileService, artist.Id, relativePath, "MetadataFiles");

                List<LyricFile> existing = FindByPath(_lyricFileService, artist.Id, relativePath);
                if (existing.Count > 1)
                    _lyricFileService.DeleteMany(existing.Skip(1).Select(x => x.Id));

                LyricFile lyricFile = new()
                {
                    Id = existing.FirstOrDefault()?.Id ?? 0,
                    ArtistId = artist.Id,
                    TrackFileId = trackFile.Id,
                    AlbumId = trackFile.AlbumId,
                    RelativePath = relativePath,
                    Added = existing.FirstOrDefault()?.Added ?? DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow,
                    Extension = Path.GetExtension(relativePath) is { Length: > 0 } extension
                        ? extension
                        : LyricsHelper.LrcExtension
                };

                _lyricFileService.Upsert(lyricFile);
                return lyricFile;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to create and upsert lyric file: {relativePath}");
                return null;
            }
        }

        public void HandleAsync(AlbumImportedEvent message) => MigrateLyricRegistrations(message.Artist, message.ImportedTracks);

        public void HandleAsync(ArtistScannedEvent message) => MigrateLyricRegistrations(message.Artist, null);

        private void MigrateLyricRegistrations(Artist? artist, List<TrackFile>? importedTracks)
        {
            if (artist == null || artist.Id <= 0)
                return;

            try
            {
                List<ExtraFile> candidates = _metadataFileService.GetFilesByArtist(artist.Id).Cast<ExtraFile>()
                    .Concat(_otherExtraFileService.GetFilesByArtist(artist.Id))
                    .Where(LyricsHelper.IsLyricFile)
                    .ToList();

                if (candidates.Count == 0)
                    return;

                List<LyricFile> lyricFiles = _lyricFileService.GetFilesByArtist(artist.Id);

                foreach (ExtraFile extra in candidates)
                {
                    if (lyricFiles.Any(x => PathsEqual(x.RelativePath, extra.RelativePath)))
                    {
                        DeleteExtraFile(extra);
                        continue;
                    }

                    TrackFile? trackFile = ResolveTrackFile(artist, importedTracks, extra);
                    if (trackFile == null)
                        continue;

                    LyricFile? created = CreateAndUpsertLyricFile(artist, trackFile, extra.RelativePath);
                    if (created != null)
                        lyricFiles.Add(created);
                }

                _logger.Debug($"Migrated {candidates.Count} lyric registration(s) to LyricFiles for artist: {artist.Name}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to migrate lyric registrations for artist: {artist.Name}");
            }
        }

        private static TrackFile? ResolveTrackFile(Artist artist, List<TrackFile>? importedTracks, ExtraFile extra)
        {
            if (extra.TrackFileId > 0)
                return new TrackFile { Id = extra.TrackFileId.Value, AlbumId = extra.AlbumId ?? 0 };

            if (importedTracks == null || string.IsNullOrEmpty(artist.Path))
                return null;

            string basePath = Path.ChangeExtension(extra.RelativePath, null);

            return importedTracks.FirstOrDefault(tf =>
                tf.Id > 0 &&
                !string.IsNullOrEmpty(tf.Path) &&
                PathsEqual(Path.ChangeExtension(artist.Path.GetRelativePath(tf.Path), null), basePath));
        }

        private void DeleteExtraFile(ExtraFile extra)
        {
            if (extra is MetadataFile)
                _metadataFileService.Delete(extra.Id);
            else
                _otherExtraFileService.Delete(extra.Id);
        }

        private void DeleteByPath<T>(IExtraFileService<T> service, int artistId, string relativePath, string table)
            where T : ExtraFile, new()
        {
            List<T> rows = FindByPath(service, artistId, relativePath);
            if (rows.Count == 0)
                return;

            _logger.Debug($"Removing {rows.Count} conflicting {table} record(s) for lyric file: {relativePath}");
            service.DeleteMany(rows.Select(x => x.Id));
        }

        private static List<T> FindByPath<T>(IExtraFileService<T> service, int artistId, string relativePath)
            where T : ExtraFile, new() =>
            service.GetFilesByArtist(artistId)
                .Where(x => PathsEqual(x.RelativePath, relativePath))
                .OrderBy(x => x.Id)
                .ToList();

        private static bool PathsEqual(string? left, string? right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        private SqlBuilder TracksWithoutLrcQuery() => Builder()
            .Join<TrackFile, Track>((tf, t) => tf.Id == t.TrackFileId)
            .Join<Track, AlbumRelease>((t, r) => t.AlbumReleaseId == r.Id)
            .Join<AlbumRelease, Album>((r, a) => r.AlbumId == a.Id)
            .Join<Album, Artist>((album, artist) => album.ArtistMetadataId == artist.ArtistMetadataId)
            .LeftJoin(@"""LyricFiles"" ON ""TrackFiles"".""Id"" = ""LyricFiles"".""TrackFileId""")
            .Where<Artist>(a => a.Monitored == true)
            .Where<AlbumRelease>(r => r.Monitored == true)
            .Where(@"""LyricFiles"".""Id"" IS NULL");
    }
}
