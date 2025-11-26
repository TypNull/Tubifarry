using System.Text.Json.Serialization;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.DABMusic
{
    #region Search & API Response Models

    /// <summary>
    /// Request data passed through the search pipeline
    /// </summary>
    public record DABMusicRequestData(
        [property: JsonPropertyName("baseUrl")] string BaseUrl,
        [property: JsonPropertyName("searchType")] string SearchType,
        [property: JsonPropertyName("limit")] int Limit);

    /// <summary>
    /// Main search response from DABMusic API
    /// </summary>
    public record DABMusicSearchResponse(
        [property: JsonPropertyName("tracks")] List<DABMusicTrack>? Tracks,
        [property: JsonPropertyName("albums")] List<DABMusicAlbum>? Albums,
        [property: JsonPropertyName("pagination")] DABMusicPagination? Pagination);

    /// <summary>
    /// Pagination information from search responses
    /// </summary>
    public record DABMusicPagination(
        [property: JsonPropertyName("offset")] int Offset,
        [property: JsonPropertyName("limit")] int Limit,
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("hasMore")] bool HasMore,
        [property: JsonPropertyName("returned")] int Returned);

    /// <summary>
    /// Album details response wrapper
    /// </summary>
    public record DABMusicAlbumDetailsResponse(
        [property: JsonPropertyName("album")] DABMusicAlbum Album);

    /// <summary>
    /// Stream response containing download URL
    /// </summary>
    public record DABMusicStreamResponse(
        [property: JsonPropertyName("url")] string Url);

    #endregion Search & API Response Models

    #region Core Data Models

    /// <summary>
    /// Album model from DABMusic API
    /// </summary>
    public record DABMusicAlbum(
        [property: JsonPropertyName("id"), JsonConverter(typeof(StringConverter))] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("artist")] string Artist,
        [property: JsonPropertyName("artistId"), JsonConverter(typeof(StringConverter))] string ArtistId,
        [property: JsonPropertyName("cover")] string? Cover = null,
        [property: JsonPropertyName("releaseDate")] string? ReleaseDate = null,
        [property: JsonPropertyName("genre")] string? Genre = null,
        [property: JsonPropertyName("trackCount")] int TrackCount = 0,
        [property: JsonPropertyName("audioQuality")] DABMusicAudioQuality? AudioQuality = null,
        [property: JsonPropertyName("label")] string? Label = null,
        [property: JsonPropertyName("tracks")] List<DABMusicTrack>? Tracks = null)
    {
        [JsonIgnore]
        public string Year => ReleaseDate?.Length >= 4 ? ReleaseDate[..4] : "Unknown";
    }

    /// <summary>
    /// Track model from DABMusic API
    /// </summary>
    public record DABMusicTrack(
        [property: JsonPropertyName("id"), JsonConverter(typeof(StringConverter))] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("artist")] string Artist,
        [property: JsonPropertyName("artistId"), JsonConverter(typeof(StringConverter))] string ArtistId,
        [property: JsonPropertyName("albumTitle")] string? AlbumTitle = null,
        [property: JsonPropertyName("albumId"), JsonConverter(typeof(StringConverter))] string? AlbumId = null,
        [property: JsonPropertyName("albumCover")] string? Cover = null,
        [property: JsonPropertyName("releaseDate")] string? ReleaseDate = null,
        [property: JsonPropertyName("duration")] int Duration = 0,
        [property: JsonPropertyName("genre")] string? Genre = null,
        [property: JsonPropertyName("trackNumber")] int TrackNumber = 0,
        [property: JsonPropertyName("audioQuality")] DABMusicAudioQuality? AudioQuality = null,
        [property: JsonPropertyName("version")] string? Version = null,
        [property: JsonPropertyName("label")] string? Label = null,
        [property: JsonPropertyName("streamable")] bool Streamable = false,
        [property: JsonPropertyName("images")] DABMusicImages? Images = null)
    {
        [JsonIgnore]
        public string DisplayAlbum => AlbumTitle ?? "Unknown Album";

        [JsonIgnore]
        public string DurationFormatted => Duration > 0 ? TimeSpan.FromSeconds(Duration).ToString(@"mm\:ss") : "0:00";
    }

    /// <summary>
    /// Audio quality information
    /// </summary>
    public record DABMusicAudioQuality(
        [property: JsonPropertyName("maximumBitDepth")] int MaximumBitDepth,
        [property: JsonPropertyName("maximumSamplingRate")] double MaximumSamplingRate,
        [property: JsonPropertyName("isHiRes")] bool IsHiRes);

    /// <summary>
    /// Image URLs for different sizes
    /// </summary>
    public record DABMusicImages(
        [property: JsonPropertyName("small")] string? Small = null,
        [property: JsonPropertyName("thumbnail")] string? Thumbnail = null,
        [property: JsonPropertyName("large")] string? Large = null);

    #endregion Core Data Models
}