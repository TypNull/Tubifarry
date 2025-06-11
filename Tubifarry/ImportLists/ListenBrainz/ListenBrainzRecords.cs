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
        [property: JsonPropertyName("range")] string? Range,
        [property: JsonPropertyName("last_updated")] long LastUpdated,
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("from_ts")] long FromTs,
        [property: JsonPropertyName("to_ts")] long ToTs);

    public record ArtistStat(
        [property: JsonPropertyName("artist_mbids")] IReadOnlyList<string>? ArtistMbids,
        [property: JsonPropertyName("artist_name")] string? ArtistName,
        [property: JsonPropertyName("listen_count")] int ListenCount);

    public record ReleaseStatsResponse(
        [property: JsonPropertyName("payload")] ReleaseStatsPayload? Payload);

    public record ReleaseStatsPayload(
        [property: JsonPropertyName("releases")] IReadOnlyList<ReleaseStat>? Releases,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("total_release_count")] int TotalReleaseCount,
        [property: JsonPropertyName("range")] string? Range,
        [property: JsonPropertyName("last_updated")] long LastUpdated,
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("from_ts")] long FromTs,
        [property: JsonPropertyName("to_ts")] long ToTs);

    public record ReleaseStat(
        [property: JsonPropertyName("artist_mbids")] IReadOnlyList<string>? ArtistMbids,
        [property: JsonPropertyName("artist_name")] string? ArtistName,
        [property: JsonPropertyName("release_name")] string? ReleaseName,
        [property: JsonPropertyName("listen_count")] int ListenCount);

    public record ReleaseGroupStatsResponse(
        [property: JsonPropertyName("payload")] ReleaseGroupStatsPayload? Payload);

    public record ReleaseGroupStatsPayload(
        [property: JsonPropertyName("release_groups")] IReadOnlyList<ReleaseGroupStat>? ReleaseGroups,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("total_release_group_count")] int TotalReleaseGroupCount,
        [property: JsonPropertyName("range")] string? Range,
        [property: JsonPropertyName("last_updated")] long LastUpdated,
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("from_ts")] long FromTs,
        [property: JsonPropertyName("to_ts")] long ToTs);

    public record ReleaseGroupStat(
        [property: JsonPropertyName("artist_mbids")] IReadOnlyList<string>? ArtistMbids,
        [property: JsonPropertyName("artist_name")] string? ArtistName,
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

    // MusicBrainz Lookup Models
    public record MusicBrainzRecordingResponse(
        [property: JsonPropertyName("artist-credit")] IReadOnlyList<MusicBrainzArtistCredit>? ArtistCredits);

    public record MusicBrainzArtistCredit(
        [property: JsonPropertyName("artist")] MusicBrainzArtist? Artist);

    public record MusicBrainzArtist(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name);

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
        [property: JsonPropertyName("tracks")] IReadOnlyList<TrackData>? Tracks);

    public record TrackData(
        [property: JsonPropertyName("extension")] Dictionary<string, JsonElement>? Extension);

    // Recommendation Playlists Models
    public record RecommendationPlaylistsResponse(
        [property: JsonPropertyName("playlists")] IReadOnlyList<RecommendationPlaylistInfo>? Playlists);

    public record RecommendationPlaylistInfo(
        [property: JsonPropertyName("playlist")] RecommendationPlaylistData? Playlist);

    public record RecommendationPlaylistData(
        [property: JsonPropertyName("identifier")] string? Identifier,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("extension")] Dictionary<string, JsonElement>? Extension);
}