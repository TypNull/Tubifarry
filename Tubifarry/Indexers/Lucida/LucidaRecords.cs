using Newtonsoft.Json;

namespace Tubifarry.Indexers.Lucida
{
    public record LucidaRequestData(string ServiceValue, string BaseUrl, string CountryCode, bool IsSingle);

    public record LucidaSearchResults([property: JsonProperty("results")] LucidaResultsContainer Results);

    public record LucidaResultsContainer(
        [property: JsonProperty("success")] bool Success,
        [property: JsonProperty("results")] LucidaResultsData Results);

    public record LucidaResultsData(
        [property: JsonProperty("query")] string Query,
        [property: JsonProperty("albums")] List<LucidaAlbum> Albums,
        [property: JsonProperty("tracks")] List<LucidaTrack> Tracks);

    public record LucidaAlbum(
        [property: JsonProperty("id")] string Id,
        [property: JsonProperty("title")] string Title,
        [property: JsonProperty("url")] string Url,
        [property: JsonProperty("releaseDate")] string ReleaseDate,
        [property: JsonProperty("trackCount")] int TrackCount,
        [property: JsonProperty("coverArtwork")] List<LucidaArtwork> CoverArtworks,
        [property: JsonProperty("artists")] List<LucidaArtist> Artists);

    public record LucidaTrack(
        [property: JsonProperty("id")] string Id,
        [property: JsonProperty("title")] string Title,
        [property: JsonProperty("url")] string Url,
        [property: JsonProperty("durationMs")] long DurationMs,
        [property: JsonProperty("artists")] List<LucidaArtist> Artists,
        [property: JsonProperty("album")] LucidaAlbumReference Album);

    public record LucidaAlbumReference(
        [property: JsonProperty("id")] string Id,
        [property: JsonProperty("title")] string Title,
        [property: JsonProperty("url")] string Url);

    public record LucidaArtist(
        [property: JsonProperty("id")] string Id,
        [property: JsonProperty("name")] string Name,
        [property: JsonProperty("url")] string Url);

    public record LucidaArtwork(
        [property: JsonProperty("url")] string Url,
        [property: JsonProperty("width")] int Width,
        [property: JsonProperty("height")] int Height);

    public record ServiceCountry(
        [property: JsonProperty("code")] string Code,
        [property: JsonProperty("label")] string Name
    );

    internal record CountryResponse(
        [property: JsonProperty("success")] bool Success,
        [property: JsonProperty("countries")] List<ServiceCountry> Countries
    );
}