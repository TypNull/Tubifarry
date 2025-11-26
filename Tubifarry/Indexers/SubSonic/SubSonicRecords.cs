using System.Text.Json;
using System.Text.Json.Serialization;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.SubSonic
{
    /// <summary>
    /// Root wrapper for Subsonic API responses
    /// </summary>
    internal record SubSonicResponseWrapper(
        [property: JsonPropertyName("subsonic-response")] SubSonicResponse? SubsonicResponse);

    /// <summary>
    /// Main Subsonic API response structure
    /// </summary>
    internal record SubSonicResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("version")] string? Version = null,
        [property: JsonPropertyName("error")] SubSonicError? Error = null,
        [property: JsonPropertyName("searchResult3")] SubSonicSearchResponse? SearchResult3 = null);

    /// <summary>
    /// Ping response wrapper for connection testing
    /// </summary>
    internal record SubSonicPingResponse(
        [property: JsonPropertyName("subsonic-response")] SubSonicPingData? SubsonicResponse);

    /// <summary>
    /// Ping response data
    /// </summary>
    internal record SubSonicPingData(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("version")] string? Version = null,
        [property: JsonPropertyName("error")] SubSonicError? Error = null);

    /// <summary>
    /// Error details from Subsonic API
    /// </summary>
    internal record SubSonicError(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string Message);

    /// <summary>
    /// Search response from SubSonic API (search3 endpoint)
    /// </summary>
    internal record SubSonicSearchResponse(
        [property: JsonPropertyName("artist")] List<SubSonicSearchArtist>? Artists,
        [property: JsonPropertyName("album")] List<SubSonicSearchAlbum>? Albums,
        [property: JsonPropertyName("song")] List<SubSonicSearchSong>? Songs);

    /// <summary>
    /// Artist model from SubSonic API
    /// </summary>
    internal record SubSonicSearchArtist(
        [property: JsonPropertyName("id"), JsonConverter(typeof(StringConverter))] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("albumCount")] int AlbumCount = 0,
        [property: JsonPropertyName("coverArt")] string? CoverArt = null);

    /// <summary>
    /// Album model from SubSonic API (search results)
    /// </summary>
    internal record SubSonicSearchAlbum(
        [property: JsonPropertyName("id"), JsonConverter(typeof(StringConverter))] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("artist")] string Artist,
        [property: JsonPropertyName("artistId"), JsonConverter(typeof(StringConverter))] string? ArtistId = null,
        [property: JsonPropertyName("coverArt")] string? CoverArt = null,
        [property: JsonPropertyName("songCount")] int SongCount = 0,
        [property: JsonPropertyName("duration")] int Duration = 0,
        [property: JsonPropertyName("created"), JsonConverter(typeof(UnixTimestampConverter))] DateTime? Created = null,
        [property: JsonPropertyName("year")] int? Year = null,
        [property: JsonPropertyName("genre")] string? Genre = null)
    {
        [JsonIgnore]
        public string YearString => Year?.ToString() ?? Created?.Year.ToString() ?? DateTime.Now.Year.ToString();
    }

    /// <summary>
    /// Full album with songs from getAlbum endpoint
    /// </summary>
    internal record SubSonicAlbumFull(
        [property: JsonPropertyName("id"), JsonConverter(typeof(StringConverter))] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("artist")] string Artist,
        [property: JsonPropertyName("artistId"), JsonConverter(typeof(StringConverter))] string? ArtistId,
        [property: JsonPropertyName("coverArt")] string? CoverArt,
        [property: JsonPropertyName("songCount")] int SongCount,
        [property: JsonPropertyName("duration")] int Duration,
        [property: JsonPropertyName("created"), JsonConverter(typeof(UnixTimestampConverter))] DateTime? Created,
        [property: JsonPropertyName("year")] int? Year,
        [property: JsonPropertyName("genre")] string? Genre,
        [property: JsonPropertyName("song")] List<SubSonicSearchSong>? Songs)
    {
        [JsonIgnore]
        public string YearString => Year?.ToString() ?? Created?.Year.ToString() ?? DateTime.Now.Year.ToString();
    }

    /// <summary>
    /// getAlbum response wrapper
    /// </summary>
    internal record SubSonicAlbumResponseWrapper(
        [property: JsonPropertyName("subsonic-response")] SubSonicItemResponse? SubsonicResponse);

    /// <summary>
    /// getSong response wrapper
    /// </summary>
    internal record SubSonicSongResponseWrapper(
        [property: JsonPropertyName("subsonic-response")] SubSonicItemResponse? SubsonicResponse);


    /// <summary>
    /// getSong response data
    /// </summary>
    internal record SubSonicItemResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("version")] string? Version = null,
        [property: JsonPropertyName("error")] SubSonicError? Error = null,
        [property: JsonPropertyName("song")] SubSonicSearchSong? Song = null,
        [property: JsonPropertyName("album")] SubSonicAlbumFull? Album = null);

    /// <summary>
    /// Song/Track model from SubSonic API
    /// </summary>
    internal record SubSonicSearchSong(
        [property: JsonPropertyName("id"), JsonConverter(typeof(StringConverter))] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("artist")] string Artist,
        [property: JsonPropertyName("artistId"), JsonConverter(typeof(StringConverter))] string? ArtistId = null,
        [property: JsonPropertyName("album")] string? Album = null,
        [property: JsonPropertyName("albumId"), JsonConverter(typeof(StringConverter))] string? AlbumId = null,
        [property: JsonPropertyName("coverArt")] string? CoverArt = null,
        [property: JsonPropertyName("duration")] int Duration = 0,
        [property: JsonPropertyName("bitRate")] int BitRate = 0,
        [property: JsonPropertyName("track")] int? Track = null,
        [property: JsonPropertyName("year")] int? Year = null,
        [property: JsonPropertyName("genre")] string? Genre = null,
        [property: JsonPropertyName("size")] long Size = 0,
        [property: JsonPropertyName("suffix")] string? Suffix = null,
        [property: JsonPropertyName("contentType")] string? ContentType = null,
        [property: JsonPropertyName("path")] string? Path = null)
    {
        [JsonIgnore]
        public string DisplayAlbum => Album ?? "Unknown Album";
    }
}
