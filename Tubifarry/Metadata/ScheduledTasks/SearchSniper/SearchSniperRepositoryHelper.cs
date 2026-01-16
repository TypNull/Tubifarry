using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Qualities;

namespace Tubifarry.Metadata.ScheduledTasks.SearchSniper
{
    public sealed class SearchSniperRepositoryHelper(
        IMainDatabase database,
        IEventAggregator eventAggregator,
        IArtistService artistService) : BasicRepository<Album>(database, eventAggregator)
    {
        private readonly IArtistService _artistService = artistService;

        public List<Album> GetCutoffUnmetAlbumsBatch(Dictionary<int, List<int>> profileCutoffs, int lastId, int limit)
        {
            if (profileCutoffs.Count == 0)
                return [];

            try
            {
                SqlBuilder builder = BuildCutoffUnmetQuery(profileCutoffs)
                    .Where($@"""Albums"".""Id"" > {lastId}")
                    .OrderBy($@"""Albums"".""Id"" ASC LIMIT {limit}");

                return PopulateArtists(Query(builder));
            }
            catch
            {
                return [];
            }
        }

        public List<Album> GetPartialAlbumsBatch(int lastId, int limit)
        {
            try
            {
                SqlBuilder builder = BuildMissingTracksQuery()
                    .Where($@"""Albums"".""Id"" > {lastId}")
                    .OrderBy($@"""Albums"".""Id"" ASC LIMIT {limit}");

                return PopulateArtists(Query(builder));
            }
            catch
            {
                return [];
            }
        }

        public (int minId, int maxId) GetPartialAlbumsIdRange()
        {
            try
            {
                SqlBuilder minBuilder = BuildMissingTracksQuery()
                    .OrderBy($@"""Albums"".""Id"" ASC LIMIT 1");
                List<Album> minResult = Query(minBuilder);

                if (minResult.Count == 0)
                    return (0, 0);

                SqlBuilder maxBuilder = BuildMissingTracksQuery()
                    .OrderBy($@"""Albums"".""Id"" DESC LIMIT 1");
                List<Album> maxResult = Query(maxBuilder);

                return (minResult[0].Id, maxResult.Count > 0 ? maxResult[0].Id : minResult[0].Id);
            }
            catch
            {
                return (0, 0);
            }
        }

        public (int minId, int maxId) GetCutoffUnmetAlbumsIdRange(Dictionary<int, List<int>> profileCutoffs)
        {
            if (profileCutoffs.Count == 0)
                return (0, 0);

            try
            {
                SqlBuilder minBuilder = BuildCutoffUnmetQuery(profileCutoffs)
                    .OrderBy($@"""Albums"".""Id"" ASC LIMIT 1");
                List<Album> minResult = Query(minBuilder);

                if (minResult.Count == 0)
                    return (0, 0);

                SqlBuilder maxBuilder = BuildCutoffUnmetQuery(profileCutoffs)
                    .OrderBy($@"""Albums"".""Id"" DESC LIMIT 1");
                List<Album> maxResult = Query(maxBuilder);

                return (minResult[0].Id, maxResult.Count > 0 ? maxResult[0].Id : minResult[0].Id);
            }
            catch
            {
                return (0, 0);
            }
        }

        private SqlBuilder BuildCutoffUnmetQuery(Dictionary<int, List<int>> profileCutoffs)
        {
            string whereClause = string.Join(" OR ", profileCutoffs.Select(kvp =>
                $"(\"Artists\".\"QualityProfileId\" = {kvp.Key} AND " +
                $"CAST(json_extract(\"TrackFiles\".\"Quality\", '$.quality.id') AS INTEGER) IN ({string.Join(",", kvp.Value)}))"
            ));

            return Builder()
                .Join<Album, Artist>((a, ar) => a.ArtistMetadataId == ar.ArtistMetadataId)
                .Join<Album, AlbumRelease>((a, r) => a.Id == r.AlbumId)
                .Join<AlbumRelease, Track>((r, t) => r.Id == t.AlbumReleaseId)
                .Join<Track, TrackFile>((t, f) => t.TrackFileId == f.Id)
                .Where<Album>(a => a.Monitored == true)
                .Where<Artist>(ar => ar.Monitored == true)
                .Where<AlbumRelease>(r => r.Monitored == true)
                .Where($"({whereClause})")
                .GroupBy<Album>(x => x.Id);
        }

        private SqlBuilder BuildMissingTracksQuery()
        {
            return Builder()
                .Join<Album, Artist>((a, ar) => a.ArtistMetadataId == ar.ArtistMetadataId)
                .Join<Album, AlbumRelease>((a, r) => a.Id == r.AlbumId)
                .Join<AlbumRelease, Track>((r, t) => r.Id == t.AlbumReleaseId)
                .LeftJoin<Track, TrackFile>((t, f) => t.TrackFileId == f.Id)
                .Where<Album>(a => a.Monitored == true)
                .Where<Artist>(ar => ar.Monitored == true)
                .Where<AlbumRelease>(r => r.Monitored == true)
                .GroupBy<Album>(a => a.Id)
                .GroupBy<Artist>(ar => ar.SortName)
                .Having("COUNT(DISTINCT \"Tracks\".\"Id\") > 0")
                .Having("SUM(CASE WHEN \"Tracks\".\"TrackFileId\" > 0 THEN 1 ELSE 0 END) > 0")
                .Having("SUM(CASE WHEN \"Tracks\".\"TrackFileId\" > 0 THEN 1 ELSE 0 END) < COUNT(DISTINCT \"Tracks\".\"Id\")");
        }

        public static Dictionary<int, List<int>> BuildProfileCutoffs(IEnumerable<QualityProfile> qualityProfiles)
        {
            Dictionary<int, List<int>> profileCutoffs = [];

            foreach (QualityProfile profile in qualityProfiles)
            {
                int cutoffIndex = profile.Items.FindIndex(x =>
                    (x.Quality?.Id == profile.Cutoff) || (x.Id == profile.Cutoff));

                if (cutoffIndex <= 0)
                    continue;

                List<int> qualityIds = [];

                foreach (QualityProfileQualityItem item in profile.Items.Take(cutoffIndex))
                {
                    if (item.Quality != null)
                        qualityIds.Add(item.Quality.Id);
                    else if (item.Items?.Any() == true)
                        qualityIds.AddRange(item.Items
                            .Where(i => i.Quality != null)
                            .Select(i => i.Quality!.Id));
                }

                if (qualityIds.Count != 0)
                    profileCutoffs[profile.Id] = qualityIds;
            }

            return profileCutoffs;
        }

        private List<Album> PopulateArtists(List<Album> albums)
        {
            if (albums.Count == 0)
                return albums;

            HashSet<int> neededMetadataIds = albums
                .Where(a => a.ArtistMetadataId > 0)
                .Select(a => a.ArtistMetadataId)
                .ToHashSet();

            if (neededMetadataIds.Count == 0)
                return albums;

            Dictionary<int, Artist> artistsByMetadataId = [];
            foreach (Artist artist in _artistService.GetAllArtists())
            {
                if (neededMetadataIds.Contains(artist.ArtistMetadataId))
                {
                    artistsByMetadataId[artist.ArtistMetadataId] = artist;
                    if (artistsByMetadataId.Count == neededMetadataIds.Count)
                        break;
                }
            }

            foreach (Album album in albums)
            {
                if (artistsByMetadataId.TryGetValue(album.ArtistMetadataId, out Artist? artist))
                    album.Artist = new LazyLoaded<Artist>(artist);
            }

            return albums;
        }

        public new SqlBuilder Builder() => base.Builder();

        public new List<Album> Query(SqlBuilder builder) => base.Query(builder);
    }
}
