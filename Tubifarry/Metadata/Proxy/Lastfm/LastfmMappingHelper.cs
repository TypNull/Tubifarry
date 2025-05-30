using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using Tubifarry.Metadata.Proxy.Core;

namespace Tubifarry.Metadata.Proxy.Lastfm
{
    public static class LastfmMappingHelper
    {
        private const string _identifier = "@lastfm";

        /// <summary>
        /// Maps a Last.fm album to a Lidarr album model.
        /// </summary>
        public static Album MapAlbumFromLastfmAlbum(LastfmAlbum lastfmAlbum, Artist? artist = null)
        {
            Album album = new()
            {
                ForeignAlbumId = $"{lastfmAlbum.ArtistName}::{lastfmAlbum.Name}{_identifier}",
                Title = lastfmAlbum.Name ?? string.Empty,
                CleanTitle = lastfmAlbum.Name.CleanArtistName(), // Use CleanArtistName instead of CleanAlbumTitle, as lidar utilizes it too.
                Links = new List<Links>(),
                Genres = lastfmAlbum.Tags?.Tag?.Select(g => g.Name).ToList() ?? new List<string>(),
                SecondaryTypes = new List<SecondaryAlbumType>(),
                AnyReleaseOk = true,
                Ratings = ComputeLastfmRating(lastfmAlbum.Listeners, lastfmAlbum.PlayCount)
            };

            if (lastfmAlbum.Wiki != null)
            {
                album.Overview = !string.IsNullOrEmpty(lastfmAlbum.Wiki.Summary) ?
                    lastfmAlbum.Wiki.Summary : "Found on Last.fm";
            }
            else
            {
                List<string> overviewParts = new();
                if (lastfmAlbum.PlayCount != 0)
                    overviewParts.Add($"Playcount: {lastfmAlbum.PlayCount}");
                if (!string.IsNullOrEmpty(lastfmAlbum.Listeners))
                    overviewParts.Add($"Listeners: {lastfmAlbum.Listeners}");
                album.Overview = overviewParts.Any() ?
                    string.Join(" • ", overviewParts) : "Found on Last.fm";
            }

            album.Links.Add(new Links { Url = lastfmAlbum.Url, Name = "Last.fm" });
            AddMusicBrainzLink(album.Links, lastfmAlbum.MBID, false);

            // Map images
            album.Images = MapImages(lastfmAlbum.Images, lastfmAlbum.UserAgent, false);

            AlbumRelease albumRelease = new()
            {
                ForeignReleaseId = album.ForeignAlbumId,
                Title = lastfmAlbum.Name,
                Media = new List<Medium> {
                    new() {
                        Format = "Digital Media",
                        Name = "Digital Media",
                        Number = 1
                    }
                },
                Album = album,
                Status = "Official"
            };

            if (artist != null)
            {
                album.Artist = artist;
                album.ArtistMetadata = artist.Metadata;
                album.ArtistMetadataId = artist.ArtistMetadataId;
            }
            if (lastfmAlbum.Tracks?.Tracks?.Count > 0 && artist != null)
            {
                int totalDuration = 0;
                albumRelease.Tracks = new List<Track>();

                for (int i = 0; i < lastfmAlbum.Tracks.Tracks.Count; i++)
                {
                    LastfmTrack lastfmTrack = lastfmAlbum.Tracks.Tracks[i];
                    Track track = MapTrack(lastfmTrack, album, albumRelease, artist, i + 1);
                    albumRelease.Tracks.Value.Add(track);
                    totalDuration += track.Duration;
                }
                albumRelease.Duration = totalDuration;
                albumRelease.TrackCount = lastfmAlbum.Tracks.Tracks.Count;
            }
            else
            {
                albumRelease.Tracks = new List<Track>();
                albumRelease.TrackCount = 0;
                albumRelease.Duration = 0;
            }

            if (albumRelease.TrackCount > 3)
            {
                album.AlbumType = "Album";
            }
            else if (albumRelease.TrackCount > 1)
            {
                bool isSingle = true;
                for (int i = 0; i < albumRelease.TrackCount - 1 && isSingle; i++)
                    for (int j = i + 1; j < albumRelease.TrackCount && isSingle; j++)
                        if (FuzzySharp.Fuzz.Ratio(albumRelease.Tracks.Value[i].Title, albumRelease.Tracks.Value[j].Title) < 80)
                        {
                            isSingle = false;
                            break;
                        }

                album.AlbumType = isSingle ? "Single" : "Album";
            }
            else if (albumRelease.TrackCount == 1)
            {
                album.AlbumType = "Single";
            }
            else
            {
                album.AlbumType = "Unknown";
            }

            album.SecondaryTypes = AlbumMapper.DetermineSecondaryTypesFromTitle(album.Title);
            album.AlbumReleases = new LazyLoaded<List<AlbumRelease>>(new List<AlbumRelease> { albumRelease });
            return album;
        }

        /// <summary>
        /// Maps a Last.fm artist to a Lidarr artist model.
        /// </summary>
        public static Artist MapArtistFromLastfmArtist(LastfmArtist lastfmArtist)
        {
            ArtistMetadata metadata = new()
            {
                ForeignArtistId = lastfmArtist.Name + _identifier,
                Name = lastfmArtist.Name ?? string.Empty,
                Links = new List<Links>
                {
                    new() { Url = lastfmArtist.Url, Name = "Last.fm" }
                },
                Genres = lastfmArtist.Tags?.Tag?.Select(t => t.Name).ToList() ?? new List<string>(),
                Members = new List<Member>(),
                Aliases = lastfmArtist.Similar?.Artists?.Where(a => !string.IsNullOrEmpty(a.Name))
                    .Select(a => a.Name).Take(5).ToList() ?? new List<string>(),
                Status = ArtistStatusType.Continuing,
                Type = string.Empty
            };

            if (lastfmArtist.Bio != null)
            {
                metadata.Overview = !string.IsNullOrEmpty(lastfmArtist.Bio.Summary) ?
                    lastfmArtist.Bio.Summary : "Found on Last.fm";
            }
            else
            {
                List<string> overviewParts = new();
                if (lastfmArtist.Stats != null)
                {
                    if (lastfmArtist.Stats.PlayCount != 0)
                        overviewParts.Add($"Playcount: {lastfmArtist.Stats.PlayCount}");
                    if (!string.IsNullOrEmpty(lastfmArtist.Stats.Listeners))
                        overviewParts.Add($"Listeners: {lastfmArtist.Stats.Listeners}");
                }
                metadata.Overview = overviewParts.Any() ?
                    string.Join(" • ", overviewParts) : "Found on Last.fm";
            }

            metadata.Ratings = ComputeLastfmRating(lastfmArtist.Stats?.Listeners ?? "0", lastfmArtist.Stats?.PlayCount ?? 0);

            AddMusicBrainzLink(metadata.Links, lastfmArtist.MBID, true);

            metadata.Images = MapImages(lastfmArtist.Images, lastfmArtist.UserAgent, true);

            return new()
            {
                ForeignArtistId = metadata.ForeignArtistId,
                Name = lastfmArtist.Name,
                SortName = lastfmArtist.Name,
                CleanName = lastfmArtist.Name.CleanArtistName(),
                Metadata = new LazyLoaded<ArtistMetadata>(metadata)
            };
        }

        /// <summary>
        /// Maps a Last.fm top album to a Lidarr album model.
        /// </summary>
        public static Album MapAlbumFromLastfmTopAlbum(LastfmTopAlbum topAlbum, Artist artist)
        {
            Album album = new()
            {
                ForeignAlbumId = $"{artist.ForeignArtistId.Replace(_identifier, "")}::{topAlbum.Name}{_identifier}",
                Title = topAlbum.Name ?? string.Empty,
                CleanTitle = topAlbum.Name.CleanArtistName(),
                Links = new List<Links> { new() { Url = topAlbum.Url, Name = "Last.fm" } },
                AlbumType = "Album",
                Ratings = new(),
                SecondaryTypes = new List<SecondaryAlbumType>(),
                Overview = $"Found on Last.fm • Playcount: {topAlbum.PlayCount}"
            };

            AlbumRelease albumRelease = new()
            {
                ForeignReleaseId = $"{topAlbum.ArtistName}::{topAlbum.Name}{_identifier}",
                Title = topAlbum.Name,
                Album = album,
                Status = "Official",
                Tracks = new List<Track>(),
            };

            if (artist != null)
            {
                album.Artist = artist;
                album.ArtistMetadata = artist.Metadata;
                album.ArtistMetadataId = artist.ArtistMetadataId;
            }

            album.SecondaryTypes = AlbumMapper.DetermineSecondaryTypesFromTitle(album.Title);

            album.AlbumReleases = new LazyLoaded<List<AlbumRelease>>(new List<AlbumRelease> { albumRelease });
            return album;
        }

        /// <summary>
        /// Maps a Last.fm track to a Lidarr track model.
        /// </summary>
        public static Track MapTrack(LastfmTrack lastfmTrack, Album album, AlbumRelease albumRelease, Artist artist, int trackPosition) => new()
        {
            ForeignRecordingId = $"{artist.Name}::{album.Title}::{lastfmTrack.Name}{_identifier}",
            ForeignTrackId = $"{artist.Name}::{album.Title}::{lastfmTrack.Name}{_identifier}",
            Title = lastfmTrack.Name,
            Duration = (int)TimeSpan.FromSeconds(lastfmTrack.Duration ?? 0).TotalMilliseconds,
            TrackNumber = trackPosition.ToString(),
            AbsoluteTrackNumber = trackPosition,
            Explicit = false,
            MediumNumber = albumRelease.Media.FirstOrDefault()?.Number ?? 1,
            Album = album,
            AlbumId = album.Id,
            AlbumRelease = albumRelease,
            AlbumReleaseId = albumRelease.Id,
            ArtistMetadata = artist?.Metadata,
            ArtistMetadataId = artist?.ArtistMetadataId ?? 0,
            Artist = artist,
            Ratings = new Ratings()
        };

        /// <summary>
        /// Merges album information if an existing album is found.
        /// </summary>
        public static Album MergeAlbums(Album existingAlbum, Album mappedAlbum)
        {
            if (existingAlbum == null)
                return mappedAlbum;
            DateTime? existingReleaseDate = existingAlbum.ReleaseDate;
            List<MediaCover> existingImages = existingAlbum.Images.ToList();

            existingAlbum.UseMetadataFrom(mappedAlbum);
            if (existingReleaseDate > DateTime.MinValue)
                existingAlbum.ReleaseDate = existingReleaseDate;

            if (existingImages.Count > 0)
                existingAlbum.Images = existingImages;

            existingAlbum.Artist = mappedAlbum.Artist ?? existingAlbum.Artist;
            existingAlbum.ArtistMetadata = mappedAlbum.ArtistMetadata ?? existingAlbum.ArtistMetadata;
            existingAlbum.AlbumReleases = mappedAlbum.AlbumReleases ?? existingAlbum.AlbumReleases;

            return existingAlbum;
        }

        /// <summary>
        /// Maps Last.fm images to MediaCover objects.
        /// </summary>
        private static List<MediaCover> MapImages(List<LastfmImage>? images, string userAgent, bool isArtist)
        {
            return images?
                .Where(i => !string.IsNullOrEmpty(i.Url))
                .Select(i => new MediaCover
                {
                    Url = i.Url,
                    CoverType = MapCoverType(i.Size + $"{FlexibleHttpDispatcher.UA_PARAM}={userAgent}", isArtist)
                })
                .ToList() ?? new List<MediaCover>();
        }

        /// <summary>
        /// Adds a MusicBrainz link to the links collection if MBID is available.
        /// </summary>
        private static void AddMusicBrainzLink(List<Links> links, string? mbid, bool isArtist)
        {
            if (!string.IsNullOrEmpty(mbid))
            {
                links.Add(new Links
                {
                    Url = $"https://musicbrainz.org/{(isArtist ? "artist" : "release")}/{mbid}",
                    Name = "MusicBrainz"
                });
            }
        }

        /// <summary>
        /// Maps Last.fm image sizes to appropriate MediaCoverTypes.
        /// </summary>
        private static MediaCoverTypes MapCoverType(string size, bool isArtist)
        {
            if (isArtist)
            {
                return size?.ToLowerInvariant() switch
                {
                    "mega" or "extralarge" => MediaCoverTypes.Poster,
                    "large" => MediaCoverTypes.Poster,
                    "medium" => MediaCoverTypes.Headshot,
                    "small" => MediaCoverTypes.Logo,
                    _ => MediaCoverTypes.Poster
                };
            }
            else
            {
                return size?.ToLowerInvariant() switch
                {
                    "mega" or "extralarge" => MediaCoverTypes.Cover,
                    "large" => MediaCoverTypes.Fanart,
                    _ => MediaCoverTypes.Unknown,
                };
            }
        }

        /// <summary>
        /// Computes rating information based on Last.fm listeners and playcount data
        /// </summary>
        public static Ratings ComputeLastfmRating(string listenersStr, int playcount)
        {
            if (!int.TryParse(listenersStr, out int listeners) || playcount == 0)
                return new Ratings { Value = 0m, Votes = 0 };

            if (listeners == 0)
                return new Ratings { Value = 0m, Votes = 0 };

            double ratingValue = Math.Log(Math.Max(10, listeners), 10) * 2;
            decimal rating = Math.Min(10m, Math.Max(0m, (decimal)ratingValue));
            rating = Math.Round(rating, 1);
            return new Ratings { Value = rating, Votes = playcount };
        }
    }
}