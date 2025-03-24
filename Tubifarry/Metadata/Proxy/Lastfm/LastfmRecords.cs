using System.Text.Json.Serialization;
using Tubifarry.Metadata.Proxy.Core;

namespace Tubifarry.Metadata.Proxy.Lastfm
{
    public record LastfmErrorResponse(
       [property: JsonPropertyName("error")] int Error,
       [property: JsonPropertyName("message")] string Message
    );

    public record LastfmImage(
        [property: JsonPropertyName("#text")] string Url,
        [property: JsonPropertyName("size")] string Size
    );

    public record LastfmStats(
        [property: JsonPropertyName("listeners")] string Listeners,
        [property: JsonPropertyName("playcount"), JsonConverter(typeof(LastfmNumberConverter))] int PlayCount
    );

    public record LastfmTag(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("url")] string Url
    );

    public record LastfmTags(
        [property: JsonPropertyName("tag")] List<LastfmTag> Tag
    );

    public record LastfmBio(
        [property: JsonPropertyName("published")] string Published,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("content")] string Content
    );

    public record LastfmWiki(
        [property: JsonPropertyName("published")] string Published,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("content")] string Content
    );

    public record LastfmStreamable(
        [property: JsonPropertyName("#text")] string Text,
        [property: JsonPropertyName("fulltrack")] string Fulltrack
    );

    public record LastfmArtist(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("mbid")] string MBID,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("image")] List<LastfmImage> Images,
        [property: JsonPropertyName("streamable")] string Streamable,
        [property: JsonPropertyName("ontour")] string OnTour,
        [property: JsonPropertyName("stats")] LastfmStats Stats,
        [property: JsonPropertyName("similar")] LastfmSimilar Similar,
        [property: JsonPropertyName("tags"), JsonConverter(typeof(LastfmTagsConverter))] LastfmTags? Tags,
        [property: JsonPropertyName("bio")] LastfmBio Bio
    ) : MappingAgent;

    public record LastfmSimilar(
        [property: JsonPropertyName("artist")] List<LastfmArtist> Artists
    );

    public record LastfmArtistInfoResponse(
        [property: JsonPropertyName("artist")] LastfmArtist Artist
    );

    public record LastfmTrackArtist(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("mbid")] string MBID,
        [property: JsonPropertyName("url")] string Url
    );

    public record LastfmTrackAlbum(
        [property: JsonPropertyName("artist")] string Artist,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("mbid")] string MBID,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("image")] List<LastfmImage> Images
    );

    public record LastfmTrack(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("mbid")] string MBID,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("duration")] int? Duration,
        [property: JsonPropertyName("streamable")] LastfmStreamable Streamable,
        [property: JsonPropertyName("listeners")] string Listeners,
        [property: JsonPropertyName("playcount"), JsonConverter(typeof(LastfmNumberConverter))] int PlayCount,
        [property: JsonPropertyName("artist")] LastfmTrackArtist Artist,
        [property: JsonPropertyName("album")] LastfmTrackAlbum Album,
        [property: JsonPropertyName("toptags"), JsonConverter(typeof(LastfmTagsConverter))] LastfmTags? Tags,
        [property: JsonPropertyName("wiki")] LastfmWiki Wiki
    );

    public record LastfmTracks(
        [property: JsonPropertyName("track")] List<LastfmTrack> Tracks
    );

    public record LastfmTrackMatches(
        [property: JsonPropertyName("track")] List<LastfmTrack> Tracks
    );

    public record LastfmTrackInfoResponse(
        [property: JsonPropertyName("track")] LastfmTrack Track
    );

    public record LastfmAlbum(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("artist")] string ArtistName,
        [property: JsonPropertyName("mbid")] string MBID,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("image")] List<LastfmImage> Images,
        [property: JsonPropertyName("listeners")] string Listeners,
        [property: JsonPropertyName("playcount"), JsonConverter(typeof(LastfmNumberConverter))] int PlayCount,
        [property: JsonPropertyName("tracks"), JsonConverter(typeof(LastfmTracksConverter))] LastfmTracks Tracks,
        [property: JsonPropertyName("tags"), JsonConverter(typeof(LastfmTagsConverter))] LastfmTags? Tags,
        [property: JsonPropertyName("wiki")] LastfmWiki Wiki
    ) : MappingAgent;

    public record LastfmAlbumInfoResponse(
        [property: JsonPropertyName("album")] LastfmAlbum Album
    );

    public record LastfmSearchAttr(
        [property: JsonPropertyName("for")] string Query,
        [property: JsonPropertyName("totalResults")] string TotalResults,
        [property: JsonPropertyName("startPage")] string StartPage,
        [property: JsonPropertyName("itemsPerPage")] string ItemsPerPage
    );

    public record LastfmTopAlbum(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("playcount"), JsonConverter(typeof(LastfmNumberConverter))] int PlayCount,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("artist"), JsonConverter(typeof(LastfmArtistConverter))] string ArtistName,
        [property: JsonPropertyName("image")] List<LastfmImage> Images
    ) : MappingAgent;

    public record LastfmSearchParameter(
        string? Query = null,
        string? ArtistName = null,
        string? AlbumName = null,
        string? TrackName = null,
        int? Limit = null,
        int? Page = null
    )
    {
        public Dictionary<string, string> ToDictionary()
        {
            Dictionary<string, string> dict = new();

            if (!string.IsNullOrEmpty(Query))
                dict["q"] = Query;
            if (!string.IsNullOrEmpty(ArtistName))
                dict["artist"] = ArtistName;
            if (!string.IsNullOrEmpty(AlbumName))
                dict["album"] = AlbumName;
            if (!string.IsNullOrEmpty(TrackName))
                dict["track"] = TrackName;
            if (Limit.HasValue)
                dict["limit"] = Limit.Value.ToString();
            if (Page.HasValue)
                dict["page"] = Page.Value.ToString();

            return dict;
        }
    }
}