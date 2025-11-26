using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using Tubifarry.Core.Replacements;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Deezer
{
    public static class DeezerMappingHelper
    {
        private const string _identifier = "@deezer";

        /// <summary>
        /// Enhanced mapping of Deezer album to internal Album model with comprehensive data handling.
        /// </summary>
        public static Album MapAlbumFromDeezerAlbum(DeezerAlbum dAlbum, Artist? artist = null)
        {
            Album album = new()
            {
                ForeignAlbumId = dAlbum.Id + _identifier,
                Title = dAlbum.Title ?? string.Empty,
                ReleaseDate = dAlbum.ReleaseDate,
                CleanTitle = dAlbum.Title.CleanArtistName(),
                Links = [],
                Genres = dAlbum.Genres?.Data?.Select(g => g.Name).ToList() ?? [],
                AlbumType = AlbumMapper.PrimaryTypeMap.TryGetValue(dAlbum.RecordType.ToLowerInvariant(), out string? mappedType) ? mappedType : "Album",
                SecondaryTypes = [],
                Ratings = new Ratings { Votes = dAlbum.Fans, Value = Math.Min(dAlbum.Fans > 0 ? (decimal)(dAlbum.Fans / 1000.0) : 0, 0) },
                AnyReleaseOk = true,
            };

            List<string> overviewParts = [];
            if (!string.IsNullOrWhiteSpace(dAlbum.Label)) overviewParts.Add($"Label: {dAlbum.Label}");
            if (dAlbum.ReleaseDate != DateTime.MinValue)
                overviewParts.Add($"Released: {dAlbum.ReleaseDate:yyyy-MM-dd}");
            if (dAlbum.NumberOfTracks > 0) overviewParts.Add($"{dAlbum.NumberOfTracks} tracks");
            if (!string.IsNullOrWhiteSpace(dAlbum.UPC)) overviewParts.Add($"UPC: {dAlbum.UPC}");
            album.Overview = overviewParts.Count != 0 ? string.Join(" • ", overviewParts) : "Found on Deezer";

            album.Images = [];
            foreach (string? url in new[] { dAlbum.CoverMedium, dAlbum.CoverBig })
                if (!string.IsNullOrEmpty(url)) album.Images.Add(new MediaCover(MediaCoverTypes.Cover, url + $"?{FlexibleHttpDispatcher.UA_PARAM}={dAlbum.UserAgent}"));

            album.Links.Add(new Links { Url = dAlbum.Link, Name = "Deezer" });
            album.Links.Add(new Links { Url = dAlbum.Share, Name = "Deezer Share" });
            if (!string.IsNullOrEmpty(dAlbum.UPC))
                album.Links.Add(new Links { Url = $"upc:{dAlbum.UPC}", Name = "UPC" });

            List<DeezerTrack> tracks = dAlbum.Tracks?.Data ?? [];
            List<int> diskNumbers = [.. tracks.Select(t => t.DiskNumber).Distinct().Order()];
            if (diskNumbers.Count == 0)
                diskNumbers.Add(1);

            AlbumRelease albumRelease = new()
            {
                ForeignReleaseId = dAlbum.Id + _identifier,
                Title = dAlbum.Title,
                ReleaseDate = dAlbum.ReleaseDate,
                Duration = dAlbum.Duration * 1000,
                Media = diskNumbers.ConvertAll(d => new Medium
                {
                    Format = "Digital Media",
                    Name = $"Disk {d}",
                    Number = 1
                }),
                Album = album,
                TrackCount = dAlbum.NumberOfTracks,
                Label = !string.IsNullOrWhiteSpace(dAlbum.Label) ? [dAlbum.Label] : [],
                Status = "Official"
            };

            if (artist != null || dAlbum.Artist != null)
            {
                artist ??= MapArtistFromDeezerArtist(dAlbum.Artist);
                album.Artist = artist;
                album.ArtistMetadata = artist.Metadata;
                album.ArtistMetadataId = artist.ArtistMetadataId;
            }
            tracks = tracks.Select((x, index) => x with { TrackPosition = index + 1 }).ToList();
            albumRelease.Tracks = tracks.ConvertAll(dTrack => MapTrack(dTrack, album, albumRelease, artist!)) ?? [];

            if (dAlbum.Contributors?.Count > 1)
                album.SecondaryTypes.Add(SecondaryAlbumType.Compilation);
            List<SecondaryAlbumType> titleTypes = AlbumMapper.DetermineSecondaryTypesFromTitle(dAlbum.Title ?? string.Empty);
            album.SecondaryTypes.AddRange(titleTypes);

            album.AlbumReleases = new LazyLoaded<List<AlbumRelease>>([albumRelease]);
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
                Links =
                [
                    new() { Url = dArtist.Link, Name = "Deezer" },
                    new() { Url = dArtist.Share, Name = "Deezer Share" }
                ],
                Genres = [],
                Members = [],
                Aliases = [],
                Status = ArtistStatusType.Continuing,
                Type = string.Empty,
                Ratings = new Ratings()
            };

            artist.Metadata = new LazyLoaded<ArtistMetadata>(metadata);
            return artist;
        }

        private static List<MediaCover> GetArtistImages(DeezerArtist artist)
        {
            List<MediaCover> images = [];
            foreach (string? url in new[] { artist.PictureMedium, artist.PictureBig })
                if (!string.IsNullOrEmpty(url)) images.Add(new MediaCover(MediaCoverTypes.Poster, url + $"?{FlexibleHttpDispatcher.UA_PARAM}={artist.UserAgent}"));
            return images;
        }

        /// <summary>
        /// Enhanced track mapping with ISRC and explicit content handling.
        /// </summary>
        public static Track MapTrack(DeezerTrack dTrack, Album album, AlbumRelease albumRelease, Artist artist) => new()
        {
            ForeignTrackId = $"{dTrack.Id}{_identifier}",
            ForeignRecordingId = $"{dTrack.Id}{_identifier}",
            Title = dTrack.Title,
            Duration = dTrack.Duration * 1000,
            TrackNumber = dTrack.TrackPosition.ToString(),
            Explicit = dTrack.ExplicitContentLyrics is (int)ExplicitContent.Explicit or (int)ExplicitContent.PartiallyExplicit,
            Album = album,
            AbsoluteTrackNumber = dTrack.TrackPosition,
            ArtistMetadata = album.ArtistMetadata,
            AlbumRelease = albumRelease,
            Artist = artist,
            MediumNumber = albumRelease.Media.FirstOrDefault()?.Number ?? 1,
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