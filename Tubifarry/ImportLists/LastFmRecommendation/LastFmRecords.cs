using NzbDrone.Core.ImportLists.LastFm;
using System.Text.Json.Serialization;

namespace Tubifarry.ImportLists.LastFmRecommendation
{
    public record LastFmTopResponse(
        [property: JsonPropertyName("topartists")] LastFmArtistList? TopArtists,
        [property: JsonPropertyName("topalbums")] LastFmAlbumList? TopAlbums,
        [property: JsonPropertyName("toptracks")] LastFmTrackList? TopTracks
    );

    public record LastFmTrackList(
        [property: JsonPropertyName("track")] List<LastFmTrack> Track
    );

    public record LastFmTrack(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("duration")] int Duration,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("artist")] LastFmArtist Artist
    );

    public record LastFmSimilarArtistsResponse(
        [property: JsonPropertyName("similarartists")] LastFmArtistList? SimilarArtists
    );

    public record LastFmSimilarTracksResponse(
        [property: JsonPropertyName("similartracks")] LastFmTrackList? SimilarTracks
    );

    public record LastFmTopAlbumsResponse(
        [property: JsonPropertyName("topalbums")] LastFmAlbumList? TopAlbums
    );
}