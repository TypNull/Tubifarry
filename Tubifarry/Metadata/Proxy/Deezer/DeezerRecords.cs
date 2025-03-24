using System.Text.Json.Serialization;
using Tubifarry.Metadata.Proxy.Core;

namespace Tubifarry.Metadata.Proxy.Deezer
{
    public enum ExplicitContent
    {
        NotExplicit = 0,
        Explicit = 1,
        Unknown = 2,
        Edited = 3,
        PartiallyExplicit = 4,
        PartiallyUnknown = 5,
        NoAdviceAvailable = 6,
        PartiallyNoAdviceAvailable = 7
    }

    public record DeezerAlbum(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("upc")] string? UPC,
        [property: JsonPropertyName("link")] string Link,
        [property: JsonPropertyName("share")] string Share,
        [property: JsonPropertyName("cover")] string Cover,
        [property: JsonPropertyName("cover_small")] string CoverSmall,
        [property: JsonPropertyName("cover_medium")] string CoverMedium,
        [property: JsonPropertyName("cover_big")] string CoverBig,
        [property: JsonPropertyName("cover_xl")] string CoverXL,
        [property: JsonPropertyName("md5_image")] string? Md5Image,
        [property: JsonPropertyName("genre_id")] int GenreId,
        [property: JsonPropertyName("genres")] DeezerGenresWrapper? Genres,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("nb_tracks")] int NumberOfTracks,
        [property: JsonPropertyName("duration")] int Duration,
        [property: JsonPropertyName("fans")] int Fans,
        [property: JsonPropertyName("release_date")] DateTime ReleaseDate,
        [property: JsonPropertyName("record_type")] string RecordType,
        [property: JsonPropertyName("available")] bool Available,
        [property: JsonPropertyName("alternative")] DeezerAlbum? Alternative,
        [property: JsonPropertyName("tracklist")] string Tracklist,
        [property: JsonPropertyName("explicit_lyrics")] bool ExplicitLyrics,
        [property: JsonPropertyName("explicit_content_lyrics")] ExplicitContent ExplicitContentLyrics,
        [property: JsonPropertyName("explicit_content_cover")] ExplicitContent ExplicitContentCover,
        [property: JsonPropertyName("contributors")] List<DeezerArtist>? Contributors,
        [property: JsonPropertyName("fallback")] DeezerFallbackAlbum? Fallback,
        [property: JsonPropertyName("artist")] DeezerArtist Artist,
        [property: JsonPropertyName("tracks")] DeezerTrackWrapper Tracks
    ) : MappingAgent;

    public record DeezerFallbackAlbum(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("status")] string Status
    ) : MappingAgent;

    public record DeezerArtist(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("link")] string Link,
        [property: JsonPropertyName("share")] string Share,
        [property: JsonPropertyName("picture")] string? Picture,
        [property: JsonPropertyName("picture_small")] string? PictureSmall,
        [property: JsonPropertyName("picture_medium")] string? PictureMedium,
        [property: JsonPropertyName("picture_big")] string? PictureBig,
        [property: JsonPropertyName("picture_xl")] string? PictureXL,
        [property: JsonPropertyName("nb_album")] int NbAlbum,
        [property: JsonPropertyName("nb_fan")] int NbFan,
        [property: JsonPropertyName("radio")] bool Radio,
        [property: JsonPropertyName("tracklist")] string Tracklist,
        [property: JsonPropertyName("position")] int? Position
    ) : MappingAgent;

    public record DeezerChart(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("tracks")] List<DeezerTrack>? Tracks,
        [property: JsonPropertyName("albums")] List<DeezerAlbum>? Albums,
        [property: JsonPropertyName("artists")] List<DeezerArtist>? Artists,
        [property: JsonPropertyName("playlists")] List<DeezerPlaylist>? Playlists,
        [property: JsonPropertyName("podcasts")] List<DeezerPodcast>? Podcasts,
        [property: JsonPropertyName("position")] int? Position
    ) : MappingAgent;

    public record DeezerEditorial(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("picture")] string Picture,
        [property: JsonPropertyName("picture_small")] string PictureSmall,
        [property: JsonPropertyName("picture_medium")] string PictureMedium,
        [property: JsonPropertyName("picture_big")] string PictureBig,
        [property: JsonPropertyName("picture_xl")] string PictureXL
    ) : MappingAgent;

    public record DeezerEpisode(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("available")] bool Available,
        [property: JsonPropertyName("release_date")] DateTime ReleaseDate,
        [property: JsonPropertyName("duration")] int Duration,
        [property: JsonPropertyName("link")] string Link,
        [property: JsonPropertyName("share")] string Share,
        [property: JsonPropertyName("picture")] string Picture,
        [property: JsonPropertyName("picture_small")] string PictureSmall,
        [property: JsonPropertyName("picture_medium")] string PictureMedium,
        [property: JsonPropertyName("picture_big")] string PictureBig,
        [property: JsonPropertyName("picture_xl")] string PictureXL,
        [property: JsonPropertyName("podcast")] DeezerPodcast Podcast,
        [property: JsonPropertyName("track_token")] string TrackToken
    ) : MappingAgent;

    public record DeezerGenresWrapper(
        [property: JsonPropertyName("data")] List<DeezerGenre>? Data
    ) : MappingAgent;

    public record DeezerGenre(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("picture")] string Picture,
        [property: JsonPropertyName("type")] string Type
    ) : MappingAgent;

    public record DeezerPlaylist(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("duration")] int Duration,
        [property: JsonPropertyName("public")] bool Public,
        [property: JsonPropertyName("is_loved_track")] bool IsLovedTrack,
        [property: JsonPropertyName("collaborative")] bool Collaborative,
        [property: JsonPropertyName("nb_tracks")] int NbTracks,
        [property: JsonPropertyName("unseen_track_count")] int UnseenTrackCount,
        [property: JsonPropertyName("fans")] int Fans,
        [property: JsonPropertyName("link")] string Link,
        [property: JsonPropertyName("share")] string Share,
        [property: JsonPropertyName("picture")] string? Picture,
        [property: JsonPropertyName("picture_small")] string? PictureSmall,
        [property: JsonPropertyName("picture_medium")] string? PictureMedium,
        [property: JsonPropertyName("picture_big")] string? PictureBig,
        [property: JsonPropertyName("picture_xl")] string? PictureXL,
        [property: JsonPropertyName("checksum")] string Checksum,
        [property: JsonPropertyName("creator")] DeezerUser Creator,
        [property: JsonPropertyName("tracks")] List<DeezerTrack>? Tracks
    ) : MappingAgent;

    public record DeezerOptions(
        [property: JsonPropertyName("streaming")] bool Streaming,
        [property: JsonPropertyName("streaming_duration")] int StreamingDuration,
        [property: JsonPropertyName("offline")] bool Offline,
        [property: JsonPropertyName("hq")] bool HQ,
        [property: JsonPropertyName("ads_display")] bool AdsDisplay,
        [property: JsonPropertyName("ads_audio")] bool AdsAudio,
        [property: JsonPropertyName("too_many_devices")] bool TooManyDevices,
        [property: JsonPropertyName("can_subscribe")] bool CanSubscribe,
        [property: JsonPropertyName("radio_skips")] int RadioSkips,
        [property: JsonPropertyName("lossless")] bool Lossless,
        [property: JsonPropertyName("preview")] bool Preview,
        [property: JsonPropertyName("radio")] bool Radio
    ) : MappingAgent;

    public record DeezerPodcast(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("available")] bool Available,
        [property: JsonPropertyName("fans")] int Fans,
        [property: JsonPropertyName("link")] string Link,
        [property: JsonPropertyName("share")] string Share,
        [property: JsonPropertyName("picture")] string Picture,
        [property: JsonPropertyName("picture_small")] string PictureSmall,
        [property: JsonPropertyName("picture_medium")] string PictureMedium,
        [property: JsonPropertyName("picture_big")] string PictureBig,
        [property: JsonPropertyName("picture_xl")] string PictureXL
    ) : MappingAgent;

    public record DeezerRadio(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("share")] string Share,
        [property: JsonPropertyName("picture")] string Picture,
        [property: JsonPropertyName("picture_small")] string PictureSmall,
        [property: JsonPropertyName("picture_medium")] string PictureMedium,
        [property: JsonPropertyName("picture_big")] string PictureBig,
        [property: JsonPropertyName("picture_xl")] string PictureXL,
        [property: JsonPropertyName("tracklist")] string Tracklist,
        [property: JsonPropertyName("md5_image")] string Md5Image
    ) : MappingAgent;

    public record DeezerTrackWrapper(
        [property: JsonPropertyName("data")] List<DeezerTrack>? Data
    ) : MappingAgent;

    public record DeezerTrack(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("readable")] bool Readable,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("title_short")] string TitleShort,
        [property: JsonPropertyName("title_version")] string TitleVersion,
        [property: JsonPropertyName("unseen")] bool Unseen,
        [property: JsonPropertyName("isrc")] string ISRC,
        [property: JsonPropertyName("link")] string Link,
        [property: JsonPropertyName("share")] string Share,
        [property: JsonPropertyName("duration")] int Duration,
        [property: JsonPropertyName("track_position")] int TrackPosition,
        [property: JsonPropertyName("disk_number")] int DiskNumber,
        [property: JsonPropertyName("rank")] int Rank,
        [property: JsonPropertyName("release_date")] DateTime ReleaseDate,
        [property: JsonPropertyName("explicit_lyrics")] bool ExplicitLyrics,
        [property: JsonPropertyName("explicit_content_lyrics")] int ExplicitContentLyrics,
        [property: JsonPropertyName("explicit_content_cover")] int ExplicitContentCover,
        [property: JsonPropertyName("preview")] string? Preview,
        [property: JsonPropertyName("bpm")] double BPM,
        [property: JsonPropertyName("gain")] double Gain,
        [property: JsonPropertyName("available_countries")] List<string>? AvailableCountries,
        [property: JsonPropertyName("alternative")] DeezerTrack? Alternative,
        [property: JsonPropertyName("contributors")] List<DeezerArtist>? Contributors,
        [property: JsonPropertyName("md5_image")] string? Md5Image,
        [property: JsonPropertyName("track_token")] string TrackToken,
        [property: JsonPropertyName("artist")] DeezerArtist Artist,
        [property: JsonPropertyName("album")] DeezerAlbum Album,
        [property: JsonPropertyName("time_add")] long? TimeAddedInPlaylist
    ) : MappingAgent;

    public record DeezerUser(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("lastname")] string? LastName,
        [property: JsonPropertyName("firstname")] string? FirstName,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("status")] int? Status,
        [property: JsonPropertyName("birthday")] DateTime? Birthday,
        [property: JsonPropertyName("inscription_date")] DateTime InscriptionDate,
        [property: JsonPropertyName("gender")] string? Gender,
        [property: JsonPropertyName("link")] string Link,
        [property: JsonPropertyName("picture")] string? Picture,
        [property: JsonPropertyName("picture_small")] string? PictureSmall,
        [property: JsonPropertyName("picture_medium")] string? PictureMedium,
        [property: JsonPropertyName("picture_big")] string? PictureBig,
        [property: JsonPropertyName("picture_xl")] string? PictureXL,
        [property: JsonPropertyName("country")] string Country,
        [property: JsonPropertyName("lang")] string? Lang,
        [property: JsonPropertyName("is_kid")] bool? IsKid,
        [property: JsonPropertyName("explicit_content_level")] string? ExplicitContentLevel,
        [property: JsonPropertyName("explicit_content_levels_available")] List<string>? ExplicitContentLevelsAvailable,
        [property: JsonPropertyName("tracklist")] string Tracklist,
        [property: JsonPropertyName("role")] string? Role
    ) : MappingAgent;

    /// <summary>
    /// Represents the advanced search parameters for the Deezer API.
    /// Only non-null fields are included in the query.
    /// </summary>
    public record DeezerSearchParameter(
        string? Query = null,
        string? Artist = null,
        string? Album = null,
        string? Track = null,
        string? Label = null,
        int? DurMin = null,
        int? DurMax = null,
        int? BpmMin = null,
        int? BpmMax = null)
    {
        private static readonly Dictionary<string, string> KeyMappings = new()
        {
            { nameof(Query), "q" },
            { nameof(Artist), "artist" },
            { nameof(Album), "album" },
            { nameof(Track), "track" },
            { nameof(Label), "label" },
            { nameof(DurMin), "dur_min" },
            { nameof(DurMax), "dur_max" },
            { nameof(BpmMin), "bpm_min" },
            { nameof(BpmMax), "bpm_max" }
        };

        public Dictionary<string, string> ToDictionary() =>
            GetType().GetProperties()
                .Where(prop => prop.PropertyType == typeof(string) || prop.PropertyType == typeof(int?))
                .Select(prop => (Key: KeyMappings[prop.Name], Value: prop.GetValue(this)))
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!.ToString()!);
    }

    public record DeezerSearchItem(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("readable")] bool Readable,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("title_short")] string TitleShort,
        [property: JsonPropertyName("title_version")] string TitleVersion,
        [property: JsonPropertyName("link")] string Link,
        [property: JsonPropertyName("duration")] int Duration,
        [property: JsonPropertyName("rank")] int Rank,
        [property: JsonPropertyName("explicit_lyrics")] bool ExplicitLyrics,
        [property: JsonPropertyName("explicit_content_lyrics")] int ExplicitContentLyrics,
        [property: JsonPropertyName("explicit_content_cover")] int ExplicitContentCover,
        [property: JsonPropertyName("preview")] string Preview,
        [property: JsonPropertyName("md5_image")] string Md5Image,
        [property: JsonPropertyName("artist")] DeezerArtist Artist,
        [property: JsonPropertyName("album")] DeezerAlbum Album,
        [property: JsonPropertyName("type")] string Type
    ) : MappingAgent;
}
