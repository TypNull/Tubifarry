using System.Text.Json.Serialization;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Invidious
{
    public record InvidiousSearchResult(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("videoId")] string? VideoId,
        [property: JsonPropertyName("author")] string? Author,
        [property: JsonPropertyName("authorId")] string? AuthorId,
        [property: JsonPropertyName("lengthSeconds")] int LengthSeconds = 0,
        [property: JsonPropertyName("viewCount")] long ViewCount = 0,
        [property: JsonPropertyName("published")] long Published = 0,
        [property: JsonPropertyName("publishedText")] string? PublishedText = null,
        [property: JsonPropertyName("videoThumbnails")] List<InvidiousThumbnail>? VideoThumbnails = null,
        [property: JsonPropertyName("description")] string? Description = null,
        [property: JsonPropertyName("playlistId")] string? PlaylistId = null,
        [property: JsonPropertyName("playlistThumbnail")] string? PlaylistThumbnail = null,
        [property: JsonPropertyName("videoCount")] int VideoCount = 0,
        [property: JsonPropertyName("videos")] List<InvidiousPlaylistVideo>? Videos = null)
    {
        [JsonIgnore]
        public string? BestThumbnailUrl => VideoThumbnails?.OrderByDescending(t => t.Width * t.Height).FirstOrDefault()?.Url;

        [JsonIgnore]
        public InvidiousThumbnail? BestThumbnail => VideoThumbnails?.OrderByDescending(t => t.Width * t.Height).FirstOrDefault();
    }

    public record InvidiousThumbnail(
        [property: JsonPropertyName("quality")] string? Quality,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height);

    public record InvidiousPlaylistVideo(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("videoId")] string VideoId,
        [property: JsonPropertyName("lengthSeconds")] int LengthSeconds = 0,
        [property: JsonPropertyName("videoThumbnails")] List<InvidiousThumbnail>? VideoThumbnails = null);

    public record InvidiousVideoInfo(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("videoId")] string VideoId,
        [property: JsonPropertyName("author")] string? Author,
        [property: JsonPropertyName("authorId")] string? AuthorId,
        [property: JsonPropertyName("lengthSeconds")] int LengthSeconds = 0,
        [property: JsonPropertyName("published")] long Published = 0,
        [property: JsonPropertyName("keywords")] List<string>? Keywords = null,
        [property: JsonPropertyName("genre")] string? Genre = null,
        [property: JsonPropertyName("videoThumbnails")] List<InvidiousThumbnail>? VideoThumbnails = null,
        [property: JsonPropertyName("adaptiveFormats")] List<InvidiousAdaptiveFormat>? AdaptiveFormats = null,
        [property: JsonPropertyName("formatStreams")] List<InvidiousFormatStream>? FormatStreams = null,
        [property: JsonPropertyName("musicTracks")] List<InvidiousMusicTrack>? MusicTracks = null,
        [property: JsonPropertyName("description")] string? Description = null)
    {
        [JsonIgnore]
        public string? BestThumbnailUrl => VideoThumbnails?.OrderByDescending(t => t.Width * t.Height).FirstOrDefault()?.Url;

        [JsonIgnore]
        public InvidiousAdaptiveFormat? BestAudioStream => AdaptiveFormats?
            .Where(f => f.Type?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(f => int.TryParse(f.Bitrate, out int b) ? b : 0)
            .FirstOrDefault();
    }

    public record InvidiousAdaptiveFormat(
        [property: JsonPropertyName("index"), JsonConverter(typeof(StringConverter))] string? Index,
        [property: JsonPropertyName("bitrate"), JsonConverter(typeof(StringConverter))] string? Bitrate,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("itag"), JsonConverter(typeof(StringConverter))] string? Itag,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("clen"), JsonConverter(typeof(StringConverter))] string? Clen,
        [property: JsonPropertyName("container")] string? Container,
        [property: JsonPropertyName("encoding")] string? Encoding,
        [property: JsonPropertyName("audioQuality")] string? AudioQuality,
        [property: JsonPropertyName("audioSampleRate"), JsonConverter(typeof(StringConverter))] string? AudioSampleRate,
        [property: JsonPropertyName("audioChannels"), JsonConverter(typeof(StringConverter))] string? AudioChannels)
    {
        [JsonIgnore]
        public bool IsAudio => Type?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;

        [JsonIgnore]
        public int BitrateKbps => int.TryParse(Bitrate, out int b) ? b / 1000 : 0;

        [JsonIgnore]
        public long ContentLength => long.TryParse(Clen, out long c) ? c : 0;
    }

    public record InvidiousFormatStream(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("itag"), JsonConverter(typeof(StringConverter))] string? Itag,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("quality")] string? Quality,
        [property: JsonPropertyName("container")] string? Container,
        [property: JsonPropertyName("encoding")] string? Encoding);

    public record InvidiousMusicTrack(
        [property: JsonPropertyName("song")] string? Song,
        [property: JsonPropertyName("artist")] string? Artist,
        [property: JsonPropertyName("album")] string? Album,
        [property: JsonPropertyName("license")] string? License);

    public record InvidiousPlaylistInfo(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("playlistId")] string PlaylistId,
        [property: JsonPropertyName("author")] string? Author,
        [property: JsonPropertyName("authorId")] string? AuthorId,
        [property: JsonPropertyName("videoCount")] int VideoCount = 0,
        [property: JsonPropertyName("viewCount")] long ViewCount = 0,
        [property: JsonPropertyName("videos")] List<InvidiousPlaylistVideoDetail>? Videos = null);

    public record InvidiousPlaylistVideoDetail(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("videoId")] string VideoId,
        [property: JsonPropertyName("author")] string? Author,
        [property: JsonPropertyName("authorId")] string? AuthorId,
        [property: JsonPropertyName("lengthSeconds")] int LengthSeconds = 0,
        [property: JsonPropertyName("index")] int Index = 0,
        [property: JsonPropertyName("videoThumbnails")] List<InvidiousThumbnail>? VideoThumbnails = null);

    public record InvidiousRequestData(
        [property: JsonPropertyName("baseUrl")] string BaseUrl,
        [property: JsonPropertyName("proxyVideos")] bool ProxyVideos);

    public record InvidiousStatsResponse(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("software")] InvidiousSoftwareInfo? Software);

    public record InvidiousSoftwareInfo(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("version")] string? Version);
}
