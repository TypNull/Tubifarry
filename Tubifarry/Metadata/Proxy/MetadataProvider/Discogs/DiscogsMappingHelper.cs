using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using System.Text;
using System.Text.RegularExpressions;
using Tubifarry.Core.Replacements;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Discogs
{
    public static partial class DiscogsMappingHelper
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
        /// Analyzes a tracklist to determine the media/discs present.
        /// </summary>
        private static List<Medium> DetermineMedia(List<DiscogsTrack>? tracklist, string defaultFormat = "Digital Media")
        {
            if (tracklist == null || tracklist.Count == 0)
                return [new() { Format = defaultFormat, Name = defaultFormat, Number = 1 }];

            HashSet<int> discNumbers = FilterTracklist(tracklist)
                .Select(t => ParseTrackPosition(t.Position, 1).DiscNumber)
                .ToHashSet();

            if (discNumbers.Count == 0)
                return [new() { Format = defaultFormat, Name = defaultFormat, Number = 1 }];

            return discNumbers.Order()
                .Select(n => new Medium
                {
                    Format = defaultFormat,
                    Name = $"{defaultFormat} {n}",
                    Number = n
                })
                .ToList();
        }

        /// <summary>
        /// Parses a Discogs position string to extract disc and track numbers.
        /// Handles: 1-1, CD1-1, A1, AA1, a/aa, 1.1, 3.a, etc.
        /// </summary>
        private static DiscogsTrackPosition ParseTrackPosition(string? position, int sequentialNumber)
        {
            if (string.IsNullOrWhiteSpace(position))
                return new DiscogsTrackPosition(1, sequentialNumber);

            Match match = PositionPattern().Match(position);
            if (!match.Success)
                return new DiscogsTrackPosition(1, sequentialNumber);

            string letters = match.Groups["letters"].Value;
            string first = match.Groups["first"].Value;
            string sep = match.Groups["sep"].Value;
            string second = match.Groups["second"].Value;

            // Multi-value format: "1-1", "CD1-1", "1.1" → (disc, track)
            if (!string.IsNullOrEmpty(sep) && int.TryParse(first, out int disc) && int.TryParse(second, out int track))
            {
                // Sub-track exception: "3.a" keeps track 3
                if (sep == "." && !char.IsDigit(second[0]))
                    return new DiscogsTrackPosition(1, disc);

                return new DiscogsTrackPosition(disc, track);
            }

            // Letter-based formats: "A1", "AA1", "a", "aa"
            if (!string.IsNullOrEmpty(letters))
            {
                bool hasTrack = int.TryParse(first, out int trackNum);

                // Lowercase repetition: "a"=1, "aa"=2, "aaa"=3
                if (char.IsLower(letters[0]) && !hasTrack)
                    return new DiscogsTrackPosition(1, letters.Length);

                // Vinyl with track: "A1", "AA1", "AB2"
                if (hasTrack)
                {
                    // Box set continuation: "AA", "AB", "BA" = discs 14+
                    if (letters.Length == 2 && char.IsUpper(letters[0]))
                    {
                        int boxDisc = 13 + ((letters[0] - 'A') * 13) + (letters[1] - 'A') / 2 + 1;
                        return new DiscogsTrackPosition(boxDisc, trackNum);
                    }

                    // Standard vinyl: "A1", "B2"
                    return new DiscogsTrackPosition(1, trackNum);
                }

                // Letters only: "A", "AA" → use sequential
                return new DiscogsTrackPosition(1, sequentialNumber);
            }

            // Simple number: "1", "2", "3"
            return int.TryParse(first, out int simple)
                ? new DiscogsTrackPosition(1, simple)
                : new DiscogsTrackPosition(1, sequentialNumber);
        }

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
        public static void MapAlbumTypes(DiscogsRelease release, Album album)
        {
            List<string>? formatDescriptions = release.Formats?.SelectMany(f => f.Descriptions ?? Enumerable.Empty<string>()).ToList();
            List<string?>? physicalFormats = release.Formats?.Select(f => f.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            List<DiscogsTrack> filteredTracks = FilterTracklist(release.Tracklist);
            int trackCount = filteredTracks.Count;
            int totalDuration = filteredTracks.Sum(t => ParseDuration(t.Duration ?? "0"));

            // Tier 1: Standard format description matching
            AlbumMapper.MapAlbumTypes(formatDescriptions, album);
            if (album.AlbumType != "Album" || formatDescriptions?.Any(d => d.Equals("album", StringComparison.OrdinalIgnoreCase)) == true)
                return;

            // Tier 2: Physical format patterns
            string? inferredType = InferTypeFromPhysicalFormat(physicalFormats!, formatDescriptions);
            if (inferredType != null)
            {
                album.AlbumType = inferredType;
                return;
            }

            // Tier 3: Track count/duration heuristics
            album.AlbumType = InferTypeFromMetadata(trackCount, totalDuration, album.Title);
        }

        public static void MapAlbumTypes(DiscogsArtistRelease release, Album album)
        {
            List<string> formatDescriptions = (release.Format ?? string.Empty).Split(',').Append(release.Type!).Select(f => f.Trim()).ToList();

            // Tier 1: Standard format description matching
            AlbumMapper.MapAlbumTypes(formatDescriptions, album);
            if (album.AlbumType != "Album" || formatDescriptions.Any(d => d.Equals("album", StringComparison.OrdinalIgnoreCase)))
                return;

            // Tier 2: Physical format patterns
            List<string> physicalFormats = (release.Format ?? string.Empty).Split(',').Select(f => f.Trim()).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            string? inferredType = InferTypeFromPhysicalFormat(physicalFormats!, formatDescriptions);
            if (inferredType != null)
                album.AlbumType = inferredType;
        }

        private static string? InferTypeFromPhysicalFormat(List<string?>? physicalFormats, List<string>? formatDescriptions)
        {
            if (physicalFormats == null || !physicalFormats.Any())
                return null;

            List<string> allFormats = physicalFormats.Concat(formatDescriptions ?? Enumerable.Empty<string>()).Select(f => f?.ToLowerInvariant() ?? string.Empty).ToList();

            if (allFormats.Any(f => f.Contains("7\"") || f.Contains("7 inch")))
                return "Single";

            if (allFormats.Any(f => f.Contains("12\"") || f.Contains("12 inch")))
            {
                if (allFormats.Any(f => f.Contains("ep") || f.Contains("mini")))
                    return "EP";
                if (allFormats.Any(f => f.Contains("single sided") || f.Contains("promo")))
                    return "Single";
                return null;
            }

            if (allFormats.Any(f => f == "lp" || f.Contains("long play")))
                return "Album";

            return null;
        }

        private static string InferTypeFromMetadata(int trackCount, int totalDurationSeconds, string? title)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                string lowerTitle = title.ToLowerInvariant();
                if (lowerTitle.Contains(" ep") || lowerTitle.Contains("e.p.") || lowerTitle.EndsWith("ep"))
                    return "EP";
                if (lowerTitle.Contains("single") || (lowerTitle.Contains(" / ") && trackCount <= 3))
                    return "Single";
            }

            int durationMinutes = totalDurationSeconds / 60;

            if (trackCount <= 3)
                return durationMinutes < 15 ? "Single" : "EP";

            if (trackCount <= 7)
                return durationMinutes < 30 ? "EP" : "Album";

            return "Album";
        }

        /// <summary>
        /// Maps a DiscogsMasterRelease to an Album. Note that artist information is not set.
        /// </summary>
        public static Album MapAlbumFromMasterRelease(DiscogsMasterRelease masterRelease)
        {
            Album album = new()
            {
                ForeignAlbumId = "m" + masterRelease.Id + _identifier,
                Title = masterRelease.Title ?? string.Empty,
                ReleaseDate = ParseReleaseDate(masterRelease.Year),
                Genres = masterRelease.Genres != null || masterRelease.Styles != null ? new List<string>(masterRelease.Genres ?? Enumerable.Empty<string>()).Concat(masterRelease.Styles ?? Enumerable.Empty<string>()).ToList() : [],
                CleanTitle = masterRelease.Title.CleanArtistName() ?? string.Empty,
                Overview = "Found on Discogs",
                Images = masterRelease.Images?.Take(2).Select(img => MapImage(img, false)).Where(x => x != null).ToList() ?? new List<MediaCover>()!,
                Links = [new() { Url = masterRelease.ResourceUrl, Name = "Discogs" }],
                AlbumType = "Album",
                Ratings = new Ratings(),
            };

            AlbumRelease albumRelease = new()
            {
                ForeignReleaseId = "m" + masterRelease.Id + _identifier,
                Title = masterRelease.Title,
                Status = "Official",
                Media = DetermineMedia(masterRelease.Tracklist),
                ReleaseDate = ParseReleaseDate(masterRelease.Year),
            };

            album.AlbumReleases = new List<AlbumRelease> { albumRelease };
            album.AnyReleaseOk = true;

            album.SecondaryTypes = [AlbumMapper.SecondaryTypeMap["master"]];
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
                ForeignAlbumId = "r" + release.Id + _identifier,
                Title = release.Title,
                ReleaseDate = ParseReleaseDate(release),
                Genres = release.Genres != null || release.Styles != null ? new List<string>(release.Genres ?? Enumerable.Empty<string>()).Concat(release.Styles ?? Enumerable.Empty<string>()).ToList() : [],
                CleanTitle = release.Title.CleanArtistName() ?? string.Empty,
                Overview = release.Notes?.Trim(),
                Images = release.Images?.Take(2).Select(img => MapImage(img, false)).Where(x => x != null).ToList() ?? new List<MediaCover>()!,
                Links = [new() { Url = release.ResourceUrl, Name = "Discogs" }],
                Ratings = ComputeCommunityRating(release.Community),
                SecondaryTypes = [],
            };

            album.SecondaryTypes = [AlbumMapper.SecondaryTypeMap["release"]];
            MapAlbumTypes(release, album);
            List<SecondaryAlbumType> titleTypes = AlbumMapper.DetermineSecondaryTypesFromTitle(release.Title ?? string.Empty);
            album.SecondaryTypes.AddRange(titleTypes);
            album.SecondaryTypes = album.SecondaryTypes.DistinctBy(x => x.Id).ToList();

            string formatName = release.Formats?.FirstOrDefault()?.Name ?? "Digital Media";
            string mappedFormat = MapFormat(formatName);

            AlbumRelease albumRelease = new()
            {
                ForeignReleaseId = "r" + release.Id + _identifier,
                Title = release.Title ?? string.Empty,
                Status = release.Status ?? "Official",
                Media = DetermineMedia(release.Tracklist, mappedFormat),
                Label = release.Labels?.Select(l => l.Name).Where(l => !string.IsNullOrWhiteSpace(l)).ToList() ?? new List<string>()!,
                ReleaseDate = ParseReleaseDate(release),
                Country = !string.IsNullOrWhiteSpace(release.Country) ? [release.Country] : []
            };

            album.AlbumReleases = new List<AlbumRelease> { albumRelease };
            album.AnyReleaseOk = true;

            return album;
        }

        /// <summary>
        /// Maps a DiscogsTrack to a Track object using data from a master release.
        /// </summary>
        public static Track MapTrack(DiscogsTrack t, DiscogsMasterRelease masterRelease, Album album, AlbumRelease albumRelease, int sequentialTrackNumber)
        {
            DiscogsTrackPosition position = ParseTrackPosition(t.Position, sequentialTrackNumber);

            return new Track
            {
                ForeignTrackId = $"m{masterRelease.Id + _identifier}_{t.Position}",
                Title = t.Title,
                Duration = ParseDuration(t.Duration ?? "0") * 1000,
                TrackNumber = position.TrackNumber.ToString(),
                Explicit = false,
                AlbumReleaseId = album.Id,
                ArtistMetadataId = album.ArtistMetadataId,
                Ratings = new Ratings(),
                ForeignRecordingId = $"m{masterRelease.Id + _identifier}_{t.Position}",
                Album = album,
                ArtistMetadata = album.ArtistMetadata,
                Artist = album.Artist,
                AlbumId = album.Id,
                AlbumRelease = albumRelease,
                MediumNumber = position.DiscNumber,
                AbsoluteTrackNumber = position.TrackNumber
            };
        }

        /// <summary>
        /// Maps a Discogs track to a Track object.
        /// </summary>
        public static Track MapTrack(DiscogsTrack t, DiscogsRelease release, Album album, AlbumRelease albumRelease, int sequentialTrackNumber)
        {
            DiscogsTrackPosition position = ParseTrackPosition(t.Position, sequentialTrackNumber);

            return new Track
            {
                ForeignTrackId = $"r{release.Id + _identifier}_{t.Position}",
                Title = t.Title,
                Duration = ParseDuration(t.Duration ?? "0") * 1000,
                TrackNumber = position.TrackNumber.ToString(),
                Explicit = false,
                AlbumReleaseId = album.Id,
                ArtistMetadataId = album.ArtistMetadataId,
                Ratings = new Ratings(),
                ForeignRecordingId = $"r{release.Id + _identifier}_{t.Position}",
                Album = album,
                ArtistMetadata = album.ArtistMetadata,
                Artist = album.Artist,
                AlbumId = album.Id,
                AlbumRelease = albumRelease,
                MediumNumber = position.DiscNumber,
                AbsoluteTrackNumber = position.TrackNumber
            };
        }

        /// <summary>
        /// Maps a DiscogsArtistRelease to an Album. This mapping does not include the artist.
        /// </summary>
        public static Album MapAlbumFromArtistRelease(DiscogsArtistRelease release)
        {
            Album album = new()
            {
                ForeignAlbumId = (release.Type == "master" ? "m" : "r") + release.Id + _identifier,
                Title = release.Title,
                Overview = release.Role ?? "Found on Discogs",
                ReleaseDate = ParseReleaseDate(release.Year),
                CleanTitle = release.Title.CleanArtistName() ?? string.Empty,
                Ratings = new Ratings(),
                Genres = [release.Label ?? string.Empty],
                Images = [new() { Url = release.Thumb + $"?{FlexibleHttpDispatcher.UA_PARAM}={release.UserAgent}" }],
            };

            album.AlbumReleases = new List<AlbumRelease>()
            {
                new ()
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
                ForeignArtistId = "a" + discogsArtist.Id + _identifier,
                Aliases = discogsArtist.NameVariations ?? [],
                Images = discogsArtist.Images?.Select(img => MapImage(img, true)).ToList() ?? new List<MediaCover>()!,
                Ratings = new Ratings(),
                Links = discogsArtist.Urls?.Select(url => new Links { Url = url, Name = AlbumMapper.GetLinkNameFromUrl(url) }).ToList() ?? [],
                Type = discogsArtist.Role ?? string.Empty,
                Genres = [],
                Overview = BuildArtistOverview(discogsArtist),
                Members = discogsArtist.Members?.Select(member => MapDiscogsMember(member)).ToList() ?? [],
                Status = discogsArtist.Members?.Any(x => x.Active) == false ? ArtistStatusType.Ended : ArtistStatusType.Continuing,
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

        public static List<Track> MapTracks(object releaseForTracks, Album album, AlbumRelease albumRelease)
        {
            List<DiscogsTrack>? tracklist = releaseForTracks switch
            {
                DiscogsMasterRelease master => master.Tracklist,
                DiscogsRelease release => release.Tracklist,
                _ => null
            };

            return FilterTracklist(tracklist).Select((track, index) => releaseForTracks switch
            {
                DiscogsMasterRelease master => MapTrack(track, master, album, albumRelease, index + 1),
                DiscogsRelease release => MapTrack(track, release, album, albumRelease, index + 1),
                _ => throw new InvalidOperationException("Invalid release type")
            }).ToList();
        }

        private static List<DiscogsTrack> FilterTracklist(List<DiscogsTrack>? tracklist) => tracklist?
            .Where(t => !string.Equals(t.Type, "heading", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(t.Type, "index", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

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
                ForeignArtistId = "a" + searchItem.Id + _identifier,
                Overview = "Found on Discogs",
                Images = [new() { Url = searchItem.Thumb + $"?{FlexibleHttpDispatcher.UA_PARAM}={searchItem.UserAgent}", CoverType = MapCoverType("primary", true) }],
                Links = [new() { Url = searchItem.ResourceUrl, Name = "Discogs" }],
                Ratings = ComputeCommunityRating(searchItem.Community),
                Genres = searchItem.Genre,
            }
        };

        private static Member MapDiscogsMember(DiscogsMember discogsMember) => new() { Name = discogsMember.Name ?? string.Empty };

        public static Ratings ComputeCommunityRating(DiscogsCommunityInfo? communityInfo)
        {
            if (communityInfo?.Rating != null)
                return new Ratings() { Value = Math.Truncate(communityInfo.Rating.Average), Votes = communityInfo.Rating.Count };

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
            decimal roundedValue = Math.Truncate(computedValue * 100m);

            return new Ratings { Value = roundedValue, Votes = want + have };
        }

        // Regex: [format][letters][number][sep][number/letter]
        [GeneratedRegex(@"^(?:[A-Za-z]+(?=\d))?(?<letters>[A-Za-z]+)?(?<first>\d+)?(?<sep>[-\.])?(?<second>\d+|[a-z]+)?$", RegexOptions.Compiled)]
        private static partial Regex PositionPattern();
    }
}