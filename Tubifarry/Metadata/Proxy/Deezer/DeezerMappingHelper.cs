using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;

namespace Tubifarry.Metadata.Proxy.Deezer
{
    public static class DeezerMappingHelper
    {
        private const string _identifier = "@deezer";
        private static readonly Dictionary<string, string> PrimaryTypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["album"] = "Album",
            ["broadcast"] = "Broadcast",
            ["ep"] = "EP",
            ["single"] = "Single"
        };

        private static readonly Dictionary<SecondaryAlbumType, List<string>> SecondaryTypeKeywords = new()
{
    { SecondaryAlbumType.Live, new List<string> { "live", "concert", "performance", "stage", " at " } },
    { SecondaryAlbumType.Remix, new List<string> { "remix", "remaster", "rework", "reimagined" } },
    { SecondaryAlbumType.Compilation, new List<string> { "greatest hits", "best of", "collection", "anthology" } },
    { SecondaryAlbumType.Soundtrack, new List<string> { "soundtrack", "ost", "original score" } },
    { SecondaryAlbumType.Spokenword, new List<string> { "spoken", "poetry", "lecture", "speech" } },
    { SecondaryAlbumType.Interview, new List<string> { "interview", "q&a", "conversation" } },
    { SecondaryAlbumType.Audiobook, new List<string> { "audiobook", " unabridged", " narration" } },
    { SecondaryAlbumType.Demo, new List<string> { "demo", "unreleased", "rough mix" } },
    { SecondaryAlbumType.Mixtape, new List<string> { "mixtape", "street", "underground" } },
    { SecondaryAlbumType.DJMix, new List<string> { "dj mix", " dj ", "set" } },
    { SecondaryAlbumType.Audiodrama, new List<string> { "audio drama", "radio play", "theater" } }
};

        private static List<SecondaryAlbumType> DetermineSecondaryTypesFromTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return new List<SecondaryAlbumType>();

            string cleanTitle = Parser.NormalizeTitle(title).ToLowerInvariant();
            List<SecondaryAlbumType> detectedTypes = new();

            foreach (KeyValuePair<SecondaryAlbumType, List<string>> kvp in SecondaryTypeKeywords)
            {
                if (kvp.Value.Any(keyword => cleanTitle.Contains(keyword)))
                {
                    detectedTypes.Add(kvp.Key);
                }
            }

            // Prioritize specific types over others
            if (detectedTypes.Contains(SecondaryAlbumType.Live) && detectedTypes.Contains(SecondaryAlbumType.Remix))
            {
                detectedTypes.Remove(SecondaryAlbumType.Remix);
            }

            return detectedTypes.Distinct().ToList();
        }

        /// <summary>
        /// Enhanced mapping of Deezer album to internal Album model with comprehensive data handling.
        /// </summary>
        public static Album MapAlbumFromDeezerAlbum(DeezerAlbum dAlbum)
        {
            Album album = new()
            {
                ForeignAlbumId = dAlbum.Id + _identifier,
                Title = dAlbum.Title ?? string.Empty,
                ReleaseDate = dAlbum.ReleaseDate,
                CleanTitle = Parser.NormalizeTitle(dAlbum.Title),
                Links = new List<Links>(),
                Genres = dAlbum.Genres?.Data?.Select(g => g.Name).ToList() ?? new List<string>(),
                AlbumType = PrimaryTypeMap.TryGetValue(dAlbum.RecordType.ToLowerInvariant(), out string? mappedType) ? mappedType : "Album",
                SecondaryTypes = new List<SecondaryAlbumType>(),
                Ratings = new Ratings { Votes = dAlbum.Fans, Value = Math.Min(dAlbum.Fans > 0 ? (decimal)(dAlbum.Fans / 1000.0) : 0, 0) },
                AnyReleaseOk = true,
            };

            // Enhanced Overview construction
            List<string> overviewParts = new();
            if (!string.IsNullOrWhiteSpace(dAlbum.Label)) overviewParts.Add($"Label: {dAlbum.Label}");
            if (dAlbum.ReleaseDate != DateTime.MinValue)
                overviewParts.Add($"Released: {dAlbum.ReleaseDate:yyyy-MM-dd}");
            if (dAlbum.NbTracks > 0) overviewParts.Add($"{dAlbum.NbTracks} tracks");
            if (!string.IsNullOrWhiteSpace(dAlbum.UPC)) overviewParts.Add($"UPC: {dAlbum.UPC}");
            album.Overview = overviewParts.Any() ? string.Join(" • ", overviewParts) : "Found on Deezer";

            album.Images = new List<MediaCover>();
            foreach (string? url in new[] { dAlbum.CoverBig, dAlbum.CoverMedium, dAlbum.CoverSmall })
                if (!string.IsNullOrEmpty(url)) album.Images.Add(new MediaCover(MediaCoverTypes.Cover, url));

            // Enhanced links with UPC
            album.Links.Add(new Links { Url = dAlbum.Link, Name = "Deezer" });
            album.Links.Add(new Links { Url = dAlbum.Share, Name = "Deezer Share" });
            if (!string.IsNullOrEmpty(dAlbum.UPC))
                album.Links.Add(new Links { Url = $"upc:{dAlbum.UPC}", Name = "UPC" });

            //List<DeezerTrack> tracks = dAlbum.Tracks?.Data ?? new List<DeezerTrack>();
            //List<int> diskNumbers = tracks.Select(t => t.DiskNumber).Distinct().OrderBy(d => d).ToList();
            //if (!diskNumbers.Any()) diskNumbers.Add(1);

            AlbumRelease albumRelease = new()
            {
                ForeignReleaseId = dAlbum.Id + _identifier,
                Title = dAlbum.Title,
                ReleaseDate = dAlbum.ReleaseDate,
                Duration = dAlbum.Duration * 1000,
                //Media = diskNumbers.ConvertAll(d => new Medium
                //{
                //    Format = "Digital Media",
                //    Name = $"Disk {d}",
                //    Number = d
                //}),
                Album = album,
                Tracks = new(),
                TrackCount = 0,
                Label = !string.IsNullOrWhiteSpace(dAlbum.Label) ? new List<string> { dAlbum.Label } : new List<string>(),
                Status = dAlbum.Available ? "Official" : "Pseudorelease"
            };

            // Secondary type detection
            if (dAlbum.Contributors?.Count > 1)
                album.SecondaryTypes.Add(SecondaryAlbumType.Compilation);

            List<SecondaryAlbumType> titleTypes = DetermineSecondaryTypesFromTitle(dAlbum.Title ?? string.Empty);
            album.SecondaryTypes.AddRange(titleTypes);

            album.AlbumReleases = new LazyLoaded<List<AlbumRelease>>(new List<AlbumRelease> { albumRelease });

            if (dAlbum.Artist == null)
                return album;

            album.Artist = MapArtistFromDeezerArtist(dAlbum.Artist);
            album.ArtistMetadata = album.ArtistMetadata;

            return album;
        }

        /// <summary>
        /// Enhanced artist mapping with multi-size image support.
        /// </summary>
        public static Artist MapArtistFromDeezerArtist(DeezerArtist dArtist)
        {
            Artist artist = new()
            {
                ForeignArtistId = dArtist.Id + _identifier,
                Name = dArtist.Name,
                SortName = dArtist.Name,
                CleanName = dArtist.Name.CleanArtistName()
            };

            ArtistMetadata metadata = new()
            {
                ForeignArtistId = dArtist.Id + _identifier,
                Name = dArtist.Name,
                Overview = $"Artist \"{dArtist.Name}\" found on Deezer{(dArtist.NbAlbum > 0 ? $" with {dArtist.NbAlbum} albums" : "")}{(dArtist.NbAlbum > 0 && dArtist.NbFan > 0 ? " and" : "")}{(dArtist.NbFan > 0 ? $" {dArtist.NbFan} fans" : "")}.",
                Images = GetArtistImages(dArtist),
                Links = new List<Links>
                {
                    new() { Url = dArtist.Link, Name = "Deezer" },
                    new() { Url = dArtist.Share, Name = "Deezer Share" }
                },
                Genres = new(),
                Members = new(),
                Aliases = new(),
                Status = ArtistStatusType.Continuing,
                Type = string.Empty,
                Ratings = new Ratings()
            };

            artist.Metadata = new LazyLoaded<ArtistMetadata>(metadata);
            return artist;
        }

        private static List<MediaCover> GetArtistImages(DeezerArtist artist)
        {
            List<MediaCover> images = new();
            foreach (string? url in new[] { artist.PictureBig, artist.Picture })
                if (!string.IsNullOrEmpty(url)) images.Add(new MediaCover(MediaCoverTypes.Poster, url));
            return images;
        }

        /// <summary>
        /// Enhanced track mapping with ISRC and explicit content handling.
        /// </summary>
        public static Track MapTrack(DeezerTrack dTrack, Album album, AlbumRelease albumRelease) => new()
        {
            ForeignTrackId = dTrack.Id + _identifier,
            Title = dTrack.Title,
            Duration = dTrack.Duration * 1000,
            TrackNumber = dTrack.TrackPosition.ToString(),
            Explicit = dTrack.ExplicitContentLyrics is (int)ExplicitContent.Explicit or (int)ExplicitContent.PartiallyExplicit,
            AlbumReleaseId = album.Id,
            AlbumId = album.Id,
            Album = album,
            AlbumRelease = albumRelease,
            Artist = MapArtistFromDeezerArtist(dTrack.Artist),
            MediumNumber = dTrack.DiskNumber,
            Ratings = new Ratings()
        };

        /// <summary>
        /// Merges album information if an existing album is found.
        /// </summary>
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
    }
}
