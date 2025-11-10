using Dapper;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;

namespace Tubifarry.Metadata.Lyrics
{
    /// <summary>
    /// Repository helper for querying track files and managing lyric file database entries.
    /// Provides efficient batch querying to avoid loading millions of records into memory.
    /// </summary>
    public sealed class TrackFileRepositoryHelper : BasicRepository<TrackFile>
    {
        private readonly ITrackRepository _trackRepository;
        private readonly ILyricFileService _lyricFileService;
        private readonly Logger _logger;

        public TrackFileRepositoryHelper(
            IMainDatabase database,
            IEventAggregator eventAggregator,
            ITrackRepository trackRepository,
            ILyricFileService lyricFileService,
            Logger logger)
            : base(database, eventAggregator)
        {
            _trackRepository = trackRepository;
            _lyricFileService = lyricFileService;
            _logger = logger;
        }

        /// <summary>
        /// Gets the total count of track files without LRC files.
        /// </summary>
        public int GetTracksWithoutLrcFilesCount()
        {
            try
            {
                SqlBuilder builder = Builder()
                    .Join<TrackFile, Track>((tf, t) => tf.Id == t.TrackFileId)
                    .Join<Track, AlbumRelease>((t, r) => t.AlbumReleaseId == r.Id)
                    .Join<AlbumRelease, Album>((r, a) => r.AlbumId == a.Id)
                    .Join<Album, Artist>((album, artist) => album.ArtistMetadataId == artist.ArtistMetadataId)
                    .LeftJoin(@"""LyricFiles"" ON ""TrackFiles"".""Id"" = ""LyricFiles"".""TrackFileId""")
                    .Where<Artist>(a => a.Monitored == true)
                    .Where<AlbumRelease>(r => r.Monitored == true)
                    .Where(@"""LyricFiles"".""Id"" IS NULL")
                    .SelectCount();

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

        /// <summary>
        /// Queries track files without LRC files in batches using SQL LIMIT/OFFSET.
        /// </summary>
        /// <param name="offset">Starting position in the result set</param>
        /// <param name="limit">Maximum number of records to return</param>
        /// <returns>List of track files for this batch</returns>
        public List<TrackFile> GetTracksWithoutLrcFilesBatch(int offset, int limit)
        {
            try
            {
                // Build SQL query with LIMIT/OFFSET for efficient pagination
                SqlBuilder builder = Builder()
                    .Join<TrackFile, Track>((tf, t) => tf.Id == t.TrackFileId)
                    .Join<Track, AlbumRelease>((t, r) => t.AlbumReleaseId == r.Id)
                    .Join<AlbumRelease, Album>((r, a) => r.AlbumId == a.Id)
                    .Join<Album, Artist>((album, artist) => album.ArtistMetadataId == artist.ArtistMetadataId)
                    .LeftJoin(@"""LyricFiles"" ON ""TrackFiles"".""Id"" = ""LyricFiles"".""TrackFileId""")
                    .Where<Artist>(a => a.Monitored == true)
                    .Where<AlbumRelease>(r => r.Monitored == true)
                    .Where(@"""LyricFiles"".""Id"" IS NULL")
                    .GroupBy<TrackFile>(tf => tf.Id)
                    .OrderBy($@"""TrackFiles"".""Id"" ASC LIMIT {limit} OFFSET {offset}");

                List<TrackFile> trackFiles = Query(builder);

                foreach (TrackFile trackFile in trackFiles)
                {
                    List<Track> tracks = _trackRepository.GetTracksByFileId(trackFile.Id);
                    trackFile.Tracks = new LazyLoaded<List<Track>>(tracks);

                    if (tracks.Count > 0 && tracks[0].Artist?.Value != null)
                    {
                        trackFile.Artist = new LazyLoaded<Artist>(tracks[0].Artist.Value);
                    }
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
                LyricFile lyricFile = new()
                {
                    ArtistId = artist.Id,
                    TrackFileId = trackFile.Id,
                    AlbumId = trackFile.AlbumId,
                    RelativePath = relativePath,
                    Added = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow,
                    Extension = ".lrc"
                };

                _lyricFileService.Upsert(lyricFile);
                _logger.Debug($"Created and upserted lyric file to database: {relativePath}");

                return lyricFile;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to create and upsert lyric file: {relativePath}");
                return null;
            }
        }

        /// <summary>
        /// Exposes the base Builder method for advanced queries.
        /// </summary>
        public new SqlBuilder Builder() => base.Builder();

        /// <summary>
        /// Exposes the base Query method for advanced queries.
        /// </summary>
        public new List<TrackFile> Query(SqlBuilder builder) => base.Query(builder);
    }
}
