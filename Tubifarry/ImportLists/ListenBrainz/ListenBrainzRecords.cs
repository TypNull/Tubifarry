using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tubifarry.ImportLists.ListenBrainz
{
    // User Statistics Models
    public record ArtistStatsResponse(
        [property: JsonPropertyName("payload")] ArtistStatsPayload? Payload);

    public record ArtistStatsPayload(
        [property: JsonPropertyName("artists")] IReadOnlyList<ArtistStat>? Artists,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("total_artist_count")] int TotalArtistCount,
        [property: JsonPropertyName("user_id")] string? UserId);

    public record ArtistStat(
        [property: JsonPropertyName("artist_mbid")] string? ArtistMbid,
        [property: JsonPropertyName("artist_name")] string? ArtistName,
        [property: JsonPropertyName("listen_count")] int ListenCount);

    public record ReleaseStatsResponse(
        [property: JsonPropertyName("payload")] ReleaseStatsPayload? Payload);

    public record ReleaseStatsPayload(
        [property: JsonPropertyName("releases")] IReadOnlyList<ReleaseStat>? Releases,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("total_release_count")] int TotalReleaseCount,
        [property: JsonPropertyName("user_id")] string? UserId);

    public record ReleaseStat(
        [property: JsonPropertyName("artist_mbids")] IReadOnlyList<string>? ArtistMbids,
        [property: JsonPropertyName("artist_name")] string? ArtistName,
        [property: JsonPropertyName("release_mbid")] string? ReleaseMbid,
        [property: JsonPropertyName("release_name")] string? ReleaseName,
        [property: JsonPropertyName("listen_count")] int ListenCount);

    public record ReleaseGroupStatsResponse(
        [property: JsonPropertyName("payload")] ReleaseGroupStatsPayload? Payload);

    public record ReleaseGroupStatsPayload(
        [property: JsonPropertyName("release_groups")] IReadOnlyList<ReleaseGroupStat>? ReleaseGroups,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("total_release_group_count")] int TotalReleaseGroupCount,
        [property: JsonPropertyName("user_id")] string? UserId);

    public record ReleaseGroupStat(
        [property: JsonPropertyName("artist_mbids")] IReadOnlyList<string>? ArtistMbids,
        [property: JsonPropertyName("artist_name")] string? ArtistName,
        [property: JsonPropertyName("release_group_mbid")] string? ReleaseGroupMbid,
        [property: JsonPropertyName("release_group_name")] string? ReleaseGroupName,
        [property: JsonPropertyName("listen_count")] int ListenCount);

    // Collaborative Filtering Recommendations Models
    public record RecordingRecommendationResponse(
        [property: JsonPropertyName("payload")] RecordingRecommendationPayload? Payload);

    public record RecordingRecommendationPayload(
        [property: JsonPropertyName("mbids")] IReadOnlyList<RecordingRecommendation>? Mbids,
        [property: JsonPropertyName("user_name")] string? UserName,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("total_mbid_count")] int TotalMbidCount);

    public record RecordingRecommendation(
        [property: JsonPropertyName("recording_mbid")] string? RecordingMbid,
        [property: JsonPropertyName("score")] double Score);

    // Playlist Models
    public record PlaylistsResponse(
        [property: JsonPropertyName("playlists")] IReadOnlyList<PlaylistInfo>? Playlists);

    public record PlaylistInfo(
        [property: JsonPropertyName("playlist")] PlaylistData? Playlist);

    public record PlaylistData(
        [property: JsonPropertyName("identifier")] string? Identifier,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("extension")] Dictionary<string, JsonElement>? Extension);

    public record PlaylistResponse(
        [property: JsonPropertyName("playlist")] PlaylistResponseData? Playlist);

    public record PlaylistResponseData(
        [property: JsonPropertyName("track")] IReadOnlyList<TrackData>? Tracks);

    public record TrackData(
        [property: JsonPropertyName("album")] string? Album,
        [property: JsonPropertyName("creator")] string? Creator,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("identifier")] IReadOnlyList<string>? Identifier,
        [property: JsonPropertyName("extension")] Dictionary<string, JsonElement>? Extension);
}
