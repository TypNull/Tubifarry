using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using System.Text;

namespace Tubifarry.Metadata.Proxy.DiscogsProxy
{
    public static class DiscogsMappingHelper
    {
        private const string _identifier = "@discogs";

        private static readonly Dictionary<string, string> FormatMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Vinyl"] = "Vinyl",
            ["LP"] = "Vinyl",
            ["12\" Vinyl"] = "Vinyl",
            ["7\" Vinyl"] = "Vinyl",
            ["Cassette"] = "Cassette",
            ["Cass"] = "Cassette",
            ["CD"] = "CD",
            ["CDr"] = "CD-R",
            ["DVD"] = "DVD",
            ["Blu-ray"] = "Blu-ray",
            ["SACD"] = "SACD",
            ["Reel-To-Reel"] = "Reel to Reel",
            ["8-Track"] = "8-Track Cartridge",
            ["Flexi-disc"] = "Flexi Disc",
            ["Shellac"] = "Shellac",
            ["DAT"] = "DAT",
            ["MiniDisc"] = "MiniDisc",
            ["All Media"] = "Mixed Media",
            ["Box Set"] = "Box Set",
            ["Lathe Cut"] = "Lathe Cut",
            ["Acetate"] = "Acetate"
        };

        private static string MapFormat(string discogsFormat)
        {
            if (string.IsNullOrWhiteSpace(discogsFormat))
                return "Digital Media";
            return FormatMap.TryGetValue(discogsFormat.Trim(), out string? mappedFormat) ? mappedFormat : "Digital Media";
        }

        /// <summary>
        /// Parses a release date from a Discogs release.
        /// </summary>
        public static DateTime? ParseReleaseDate(DiscogsRelease release)
        {
            if (DateTime.TryParse(release.Released, out DateTime parsedDate))
                return parsedDate;
            return ParseReleaseDate(release.Year);
        }

        /// <summary>
        /// Parses a release date from a Discogs artist release.
        /// </summary>
        public static DateTime? ParseReleaseDate(int? year) => year > 0 ? new DateTime(year ?? 0, 1, 1) : null;

        /// <summary>
        /// Parses a duration string (e.g., "3:45") into seconds.
        /// </summary>
        public static int ParseDuration(string duration)
        {
            if (string.IsNullOrWhiteSpace(duration))
                return 0;

            string[] parts = duration.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int m) && int.TryParse(parts[1], out int s))
                return (m * 60) + s;

            return 0;
        }

        /// <summary>
        /// Maps a Discogs image to a MediaCover object.
        /// </summary>
        public static MediaCover? MapImage(DiscogsImage img, bool isArtist) => new()
        {
            Url = img.Uri,
            RemoteUrl = img.Uri,
            CoverType = MapCoverType(img.Type, isArtist)
        };

        /// <summary>
        /// Maps a Discogs image type to a MediaCoverTypes enum.
        /// </summary>
        public static MediaCoverTypes MapCoverType(string? type, bool isArtist)
        {
            if (isArtist)
            {
                return type?.ToLowerInvariant() switch
                {
                    "primary" or "avatar" => MediaCoverTypes.Poster,
                    "banner" => MediaCoverTypes.Banner,
                    "background" => MediaCoverTypes.Headshot,
                    _ => MediaCoverTypes.Poster
                };
            }

            return type?.ToLowerInvariant() switch
            {
                "primary" => MediaCoverTypes.Cover,
                "secondary" => MediaCoverTypes.Cover,
                _ => MediaCoverTypes.Unknown
            };
        }

        /// <summary>
        /// Maps Discogs format descriptions to album types.
        /// </summary>
        public static void MapAlbumTypes(DiscogsRelease release, Album album) => AlbumMapper.MapAlbumTypes(release.Formats?.SelectMany(f => f.Descriptions ?? Enumerable.Empty<string>()), album);

        public static void MapAlbumTypes(DiscogsArtistRelease release, Album album) => AlbumMapper.MapAlbumTypes((release.Format ?? string.Empty).Split(',').Append(release.Type!).Select(f => f.Trim()), album);

        /// <summary>
        /// Maps a DiscogsMasterRelease to an Album. Note that artist information is not set.
        /// </summary>
        /// <summary>
        /// Maps a DiscogsMasterRelease to an Album. Note that artist information is not set.
        /// </summary>
        public static Album MapAlbumFromMasterRelease(DiscogsMasterRelease masterRelease)
        {
            Album album = new()
            {
                ForeignAlbumId = masterRelease.Id + _identifier,
                Title = masterRelease.Title ?? string.Empty,
                ReleaseDate = ParseReleaseDate(masterRelease.Year),
                Genres = masterRelease.Genres != null || masterRelease.Styles != null ? new List<string>(masterRelease.Genres ?? Enumerable.Empty<string>()).Concat(masterRelease.Styles ?? Enumerable.Empty<string>()).ToList() : new List<string>(),
                CleanTitle = Parser.NormalizeTitle(masterRelease.Title ?? string.Empty),
                Overview = "Found on Discogs",
                Images = masterRelease.Images?.Take(2).Select(img => MapImage(img, false)).Where(x => x != null).ToList() ?? new List<MediaCover>()!,
                Links = new List<Links> { new() { Url = masterRelease.ResourceUrl, Name = "Discogs" } },
                AlbumType = "Album",
                Ratings = new Ratings(),
            };

            AlbumRelease albumRelease = new()
            {
                ForeignReleaseId = masterRelease.Id + _identifier,
                Title = masterRelease.Title,
                Status = "Official",
                Media = new List<Medium> { new() { Format = "Digital Media", Name = "Digital Media", Number = 1 } },
                ReleaseDate = ParseReleaseDate(masterRelease.Year),
            };

            album.AlbumReleases = new List<AlbumRelease> { albumRelease };
            album.AnyReleaseOk = true;

            album.SecondaryTypes = new List<SecondaryAlbumType> { AlbumMapper.SecondaryTypeMap["master"] };
            List<SecondaryAlbumType> titleTypes = AlbumMapper.DetermineSecondaryTypesFromTitle(masterRelease.Title ?? string.Empty);
            album.SecondaryTypes.AddRange(titleTypes);
            if (album.SecondaryTypes.Count == 1)
                album.SecondaryTypes.Add(SecondaryAlbumType.Studio);

            if (!string.IsNullOrEmpty(masterRelease.MainReleaseUrl))
                album.Links.Add(new Links { Url = masterRelease.MainReleaseUrl, Name = "Main Release" });
            if (!string.IsNullOrEmpty(masterRelease.VersionsUrl))
                album.Links.Add(new Links { Url = masterRelease.VersionsUrl, Name = "Versions" });

            return album;
        }

        /// <summary>
        /// Maps a detailed DiscogsRelease to an Album. Note that artist information is not set.
        /// </summary>
        public static Album MapAlbumFromRelease(DiscogsRelease release)
        {
            Album album = new()
            {
                ForeignAlbumId = release.Id + _identifier,
                Title = release.Title,
                ReleaseDate = ParseReleaseDate(release),
                Genres = release.Genres != null || release.Styles != null ? new List<string>(release.Genres ?? Enumerable.Empty<string>()).Concat(release.Styles ?? Enumerable.Empty<string>()).ToList() : new List<string>(),
                CleanTitle = Parser.NormalizeTitle(release.Title ?? string.Empty),
                Overview = release.Notes?.Trim(),
                Images = release.Images?.Take(2).Select(img => MapImage(img, false)).Where(x => x != null).ToList() ?? new List<MediaCover>()!,
                Links = new List<Links> { new() { Url = release.ResourceUrl, Name = "Discogs" } },
                Ratings = ComputeCommunityRating(release.Community),
                SecondaryTypes = new(),
            };

            album.SecondaryTypes = new List<SecondaryAlbumType> { AlbumMapper.SecondaryTypeMap["release"] };
            MapAlbumTypes(release, album);
            List<SecondaryAlbumType> titleTypes = AlbumMapper.DetermineSecondaryTypesFromTitle(release.Title ?? string.Empty);
            album.SecondaryTypes.AddRange(titleTypes);
            album.SecondaryTypes = album.SecondaryTypes.DistinctBy(x => x.Id).ToList();

            AlbumRelease albumRelease = new()
            {
                ForeignReleaseId = release.Id + _identifier,
                Title = release.Title ?? string.Empty,
                Status = release.Status ?? "Official",
                Media = release.Formats?.Select(f => new Medium
                {
                    Format = MapFormat(f.Name ?? string.Empty),
                    Name = MapFormat(f.Name ?? string.Empty),
                    Number = int.TryParse(f.Qty, out int number) ? number : 1,
                }).ToList() ?? new List<Medium> { new() { Format = "Digital Media", Name = "Digital Media", Number = 1 } },
                Label = release.Labels?.Select(l => l.Name).Where(l => !string.IsNullOrWhiteSpace(l)).ToList() ?? new List<string>()!,
                ReleaseDate = ParseReleaseDate(release),
                Country = !string.IsNullOrWhiteSpace(release.Country) ? new List<string> { release.Country } : new List<string>()
            };
            album.AlbumReleases = new List<AlbumRelease> { albumRelease };
            album.AnyReleaseOk = true;

            return album;
        }

        /// <summary>
        /// Maps a DiscogsTrack to a Track object using data from a master release.
        /// </summary>
        public static Track MapTrack(DiscogsTrack t, DiscogsMasterRelease masterRelease, Album album, AlbumRelease albumRelease)
        {
            string digits = new(t.Position?.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(digits) || !int.TryParse(digits, out int absoluteNumber))
                absoluteNumber = 1;

            return new Track
            {
                ForeignTrackId = $"{masterRelease.Id + _identifier}_{t.Position}",
                Title = t.Title,
                Duration = ParseDuration(t.Duration ?? "0") * 1000,
                TrackNumber = absoluteNumber.ToString(),
                Explicit = false,
                AlbumReleaseId = album.Id,
                ArtistMetadataId = album.ArtistMetadataId,
                Ratings = new Ratings(),
                ForeignRecordingId = $"{masterRelease.Id + _identifier}_{t.Position}",
                Album = album,
                ArtistMetadata = album.ArtistMetadata,
                Artist = album.Artist,
                AlbumId = album.Id,
                AlbumRelease = albumRelease,
                MediumNumber = albumRelease.Media.FirstOrDefault()?.Number ?? 1,
                AbsoluteTrackNumber = absoluteNumber
            };
        }

        /// <summary>
        /// Maps a Discogs track to a Track object.
        /// </summary>
        public static Track MapTrack(DiscogsTrack t, DiscogsRelease release, Album album, AlbumRelease albumRelease)
        {
            string digits = new(t.Position?.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(digits) || !int.TryParse(digits, out int absoluteNumber))
                absoluteNumber = 1;

            return new Track
            {
                ForeignTrackId = $"{release.Id + _identifier}_{t.Position}",
                Title = t.Title,
                Duration = ParseDuration(t.Duration ?? "0") * 1000,
                TrackNumber = absoluteNumber.ToString(),
                Explicit = false,
                AlbumReleaseId = album.Id,
                ArtistMetadataId = album.ArtistMetadataId,
                Ratings = new Ratings(),
                ForeignRecordingId = $"{release.Id + _identifier}_{t.Position}",
                Album = album,
                ArtistMetadata = album.ArtistMetadata,
                Artist = album.Artist,
                AlbumId = album.Id,
                AlbumRelease = albumRelease,
                MediumNumber = albumRelease.Media.FirstOrDefault()?.Number ?? 1,
                AbsoluteTrackNumber = absoluteNumber
            };
        }

        /// <summary>
        /// Maps a DiscogsArtistRelease to an Album. This mapping does not include the artist.
        /// </summary>
        public static Album MapAlbumFromArtistRelease(DiscogsArtistRelease release)
        {
            Album album = new()
            {
                ForeignAlbumId = release.Id + _identifier,
                Title = release.Title,
                Overview = release.Role ?? "Found on Discogs",
                ReleaseDate = ParseReleaseDate(release.Year),
                CleanTitle = Parser.NormalizeTitle(release.Title ?? string.Empty),
                Ratings = new Ratings(),
                Genres = new List<string> { release.Label ?? string.Empty },
                Images = new List<MediaCover> { new() { Url = release.Thumb } },
            };

            album.AlbumReleases = new List<AlbumRelease>()
            {
                new()
                {
                    Status = "Official",
                    Album = album,
                    Title = release.Title,
                    Tracks = new List<Track>(),
                    ForeignReleaseId = release.Id + _identifier
                }
            };

            MapAlbumTypes(release, album);
            List<SecondaryAlbumType> titleTypes = AlbumMapper.DetermineSecondaryTypesFromTitle(release.Title ?? string.Empty);
            album.SecondaryTypes.AddRange(titleTypes);
            if (album.SecondaryTypes.Count == 1)
                album.SecondaryTypes.Add(SecondaryAlbumType.Studio);
            return album;
        }


        /// <summary>
        /// Maps a DiscogsArtist to an Artist.
        /// </summary>
        public static Artist MapArtistFromDiscogsArtist(DiscogsArtist discogsArtist) => new()
        {
            Metadata = new ArtistMetadata()
            {
                Name = discogsArtist.Name ?? string.Empty,
                ForeignArtistId = discogsArtist.Id + _identifier,
                Aliases = discogsArtist.NameVariations ?? new List<string>(),
                Images = discogsArtist.Images?.Select(img => MapImage(img, true)).ToList() ?? new List<MediaCover>()!,
                Ratings = new Ratings(),
                Links = discogsArtist.Urls?.Select(url => new Links { Url = url, Name = AlbumMapper.GetLinkNameFromUrl(url) }).ToList() ?? new List<Links>(),
                Type = discogsArtist.Role ?? string.Empty,
                Genres = new List<string>(),
                Overview = BuildArtistOverview(discogsArtist),
                Members = discogsArtist.Members?.Select(member => MapDiscogsMember(member)).ToList() ?? new List<Member>(),
                Status = (discogsArtist.Members?.Any(x => x.Active) == false ? ArtistStatusType.Ended : ArtistStatusType.Continuing),
            },
            Name = discogsArtist.Name,
            CleanName = discogsArtist.Name.CleanArtistName()
        };

        public static Album MergeAlbums(Album existingAlbum, Album mappedAlbum)
        {
            if (existingAlbum == null)
                return mappedAlbum;

            existingAlbum.UseMetadataFrom(mappedAlbum);
            existingAlbum.Artist = mappedAlbum.Artist ?? existingAlbum.Artist;
            existingAlbum.ArtistMetadata = mappedAlbum.ArtistMetadata ?? existingAlbum.ArtistMetadata;
            existingAlbum.AlbumReleases = mappedAlbum.AlbumReleases ?? existingAlbum.AlbumReleases;
            return existingAlbum;
        }

        public static List<Track> MapTracks(object releaseForTracks, Album album, AlbumRelease albumRelease) => releaseForTracks switch
        {
            DiscogsMasterRelease master => master.Tracklist?.Select(t => MapTrack(t, master, album, albumRelease)).ToList() ?? new List<Track>(),
            DiscogsRelease release => release.Tracklist?.Select(t => MapTrack(t, release, album, albumRelease)).ToList() ?? new List<Track>(),
            _ => new List<Track>()
        };

        /// <summary>
        /// Creates a concise artist overview using DiscogsArtist data.
        /// </summary>
        private static string BuildArtistOverview(DiscogsArtist discogsArtist)
        {
            StringBuilder overview = new();

            if (!string.IsNullOrEmpty(discogsArtist.Profile))
                overview.AppendLine(discogsArtist.Profile);

            if (!string.IsNullOrEmpty(discogsArtist.Role) || !string.IsNullOrEmpty(discogsArtist.Join))
                overview.AppendLine().AppendLine("Role and Involvement:").AppendLine($"- Role: {discogsArtist.Role ?? "Not specified"}").AppendLine($"- Joined: {discogsArtist.Join ?? "Not specified"}");

            if (discogsArtist.NameVariations?.Any() == true)
            {
                overview.AppendLine().AppendLine("Name Variations:");
                foreach (string variation in discogsArtist.NameVariations)
                    overview.AppendLine($"- {variation}");
            }

            if (!string.IsNullOrEmpty(discogsArtist.DataQuality))
                overview.AppendLine().AppendLine($"Data Quality: {discogsArtist.DataQuality}");
            return overview.ToString().Trim();
        }

        /// <summary>
        /// Maps a DiscogsSearchItem to an Artist.
        /// </summary>
        public static Artist MapArtistFromSearchItem(DiscogsSearchItem searchItem) => new()
        {
            Metadata = new ArtistMetadata()
            {
                Name = searchItem.Title ?? string.Empty,
                ForeignArtistId = searchItem.Id + _identifier,
                Overview = "Found on Discogs",
                Images = new List<MediaCover> { new() { Url = searchItem.Thumb, CoverType = MapCoverType("primary", true) } },
                Links = new List<Links> { new() { Url = searchItem.ResourceUrl, Name = "Discogs" } },
                Ratings = ComputeCommunityRating(searchItem.Community),
                Genres = searchItem.Genre,
            }
        };

        private static Member MapDiscogsMember(DiscogsMember discogsMember) => new() { Name = discogsMember.Name ?? string.Empty };

        public static Ratings ComputeCommunityRating(DiscogsCommunityInfo? communityInfo)
        {
            if (communityInfo?.Rating != null)
                return new Ratings() { Value = communityInfo.Rating.Average, Votes = communityInfo.Rating.Count };

            int want = communityInfo?.Want ?? 0;
            int have = communityInfo?.Have ?? 0;

            if (want == 0 && have == 0)
                return new Ratings { Value = 0m, Votes = 0 };

            decimal smoothWant = want + 1;
            decimal smoothHave = have + 1;

            decimal ratio = smoothWant / smoothHave;
            decimal normalizedRatio = ratio / (ratio + 1);
            decimal proportion = smoothWant / (smoothWant + smoothHave);

            decimal computedValue = (0.7m * normalizedRatio) + (0.3m * proportion);

            return new Ratings { Value = computedValue * 100m, Votes = want + have };
        }
    }
}