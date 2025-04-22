using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tubifarry.Indexers.Lucida
{
    #region JSON Converters

    /// <summary>
    /// Custom JSON converter that handles both string and numeric IDs, converting them to long
    /// </summary>
    public class FlexibleLongConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.Number => reader.GetInt64(),
                JsonTokenType.String => long.TryParse(reader.GetString(), out long result) ? result :
                    throw new JsonException($"Cannot convert string '{reader.GetString()}' to long"),
                _ => throw new JsonException($"Cannot convert token type {reader.TokenType} to long")
            };
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }

    /// <summary>
    /// Custom JSON converter for flexible float handling
    /// </summary>
    public class FlexibleFloatConverter : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.Number => (float)reader.GetDouble(),
                JsonTokenType.String => float.TryParse(reader.GetString(), out float result) ? result :
                    throw new JsonException($"Cannot convert string '{reader.GetString()}' to float"),
                _ => throw new JsonException($"Cannot convert token type {reader.TokenType} to float")
            };
        }

        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }

    #endregion

    #region Search & Parsing Models

    /// <summary>
    /// Request data passed through the search pipeline
    /// </summary>
    public record LucidaRequestData(
        [property: JsonPropertyName("serviceValue")] string ServiceValue,
        [property: JsonPropertyName("baseUrl")] string BaseUrl,
        [property: JsonPropertyName("countryCode")] string CountryCode,
        [property: JsonPropertyName("isSingle")] bool IsSingle);

    /// <summary>
    /// Search results wrapper
    /// </summary>
    public record LucidaSearchResults(
        [property: JsonPropertyName("results")] LucidaResultsContainer Results);

    /// <summary>
    /// Results container from search
    /// </summary>
    public record LucidaResultsContainer(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("results")] LucidaResultsData Results);

    /// <summary>
    /// Actual search results data
    /// </summary>
    public record LucidaResultsData(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("albums")] List<LucidaAlbum>? Albums = null,
        [property: JsonPropertyName("tracks")] List<LucidaTrack>? Tracks = null,
        [property: JsonPropertyName("artists")] List<LucidaArtist>? Artists = null);

    /// <summary>
    /// JavaScript data wrapper from page extraction
    /// </summary>
    public record LucidaDataWrapper(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("data")] DataContainer Data);

    /// <summary>
    /// Data container for JavaScript extracted data
    /// </summary>
    public record DataContainer(
        [property: JsonPropertyName("results")] LucidaResultsContainer Results,
        [property: JsonPropertyName("query")] string? Query = null,
        [property: JsonPropertyName("country")] string? Country = null,
        [property: JsonPropertyName("service")] string? Service = null,
        [property: JsonPropertyName("uses")] Dictionary<string, float>? Uses = null);

    #endregion

    #region Core Data Models

    /// <summary>
    /// Album model from search results
    /// </summary>
    public record LucidaAlbum(
        [property: JsonPropertyName("id"), JsonConverter(typeof(FlexibleLongConverter))] long Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("releaseDate")] string ReleaseDate,
        [property: JsonPropertyName("trackCount"), JsonConverter(typeof(FlexibleFloatConverter))] float TrackCount,
        [property: JsonPropertyName("coverArtwork")] List<LucidaArtwork>? CoverArtwork = null,
        [property: JsonPropertyName("artists")] List<LucidaArtist>? Artists = null,
        [property: JsonPropertyName("upc")] string? Upc = null,
        [property: JsonPropertyName("label")] string? Label = null,
        [property: JsonPropertyName("genre")] List<string>? Genre = null)
    {
        public LucidaAlbum() : this(0, "", "", "", 0) { }
    }

    /// <summary>
    /// Track model from search results
    /// </summary>
    public record LucidaTrack(
        [property: JsonPropertyName("id"), JsonConverter(typeof(FlexibleLongConverter))] long Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("durationMs")] long DurationMs,
        [property: JsonPropertyName("releaseDate")] string ReleaseDate,
        [property: JsonPropertyName("description")] string? Description = null,
        [property: JsonPropertyName("explicit")] bool Explicit = false,
        [property: JsonPropertyName("isrc")] string? Isrc = null,
        [property: JsonPropertyName("trackNumber"), JsonConverter(typeof(FlexibleFloatConverter))] float TrackNumber = 0,
        [property: JsonPropertyName("discNumber"), JsonConverter(typeof(FlexibleFloatConverter))] float DiscNumber = 1,
        [property: JsonPropertyName("coverArtwork")] List<LucidaArtwork>? CoverArtwork = null,
        [property: JsonPropertyName("artists")] List<LucidaArtist>? Artists = null,
        [property: JsonPropertyName("album")] LucidaAlbumReference? Album = null,
        [property: JsonPropertyName("genres")] List<string>? Genres = null)
    {
        public LucidaTrack() : this(0, "", "", 0, "") { }
    }

    /// <summary>
    /// Album reference within track info
    /// </summary>
    public record LucidaAlbumReference(
        [property: JsonPropertyName("id"), JsonConverter(typeof(FlexibleLongConverter))] long Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("coverArtwork")] List<LucidaArtwork>? CoverArtwork = null,
        [property: JsonPropertyName("artists")] List<LucidaArtist>? Artists = null,
        [property: JsonPropertyName("upc")] string? Upc = null,
        [property: JsonPropertyName("releaseDate")] string? ReleaseDate = null,
        [property: JsonPropertyName("label")] string? Label = null,
        [property: JsonPropertyName("genre")] List<string>? Genre = null)
    {
        public LucidaAlbumReference() : this(0, "", "") { }
    }

    /// <summary>
    /// Artist information
    /// </summary>
    public record LucidaArtist(
        [property: JsonPropertyName("id"), JsonConverter(typeof(FlexibleLongConverter))] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("pictures")] List<string>? Pictures = null)
    {
        public LucidaArtist() : this(0, "", "") { }
    }

    /// <summary>
    /// Artwork/cover image information
    /// </summary>
    public record LucidaArtwork(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("width"), JsonConverter(typeof(FlexibleFloatConverter))] float Width,
        [property: JsonPropertyName("height"), JsonConverter(typeof(FlexibleFloatConverter))] float Height)
    {
        public LucidaArtwork() : this("", 0, 0) { }

        [JsonIgnore]
        public int PixelCount => (int)(Width * Height);
    }

    /// <summary>
    /// Service country information
    /// </summary>
    public record ServiceCountry(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("label")] string Name)
    {
        public ServiceCountry() : this("", "") { }
    }

    /// <summary>
    /// Country API response
    /// </summary>
    public record CountryResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("countries")] List<ServiceCountry>? Countries = null)
    {
        public CountryResponse() : this(false) { }
    }

    #endregion

    #region JavaScript Data Response Models

    /// <summary>
    /// Unified info model for both tracks and albums from JavaScript data
    /// </summary>
    public record LucidaInfo(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("type")] string Type = "",
        [property: JsonPropertyName("id"), JsonConverter(typeof(FlexibleLongConverter))] long Id = 0,
        [property: JsonPropertyName("url")] string Url = "",
        [property: JsonPropertyName("title")] string Title = "",
        [property: JsonPropertyName("durationMs")] long DurationMs = 0,
        [property: JsonPropertyName("album")] LucidaAlbumRef? Album = null,
        [property: JsonPropertyName("isrc")] string? Isrc = null,
        [property: JsonPropertyName("copyright")] string? Copyright = null,
        [property: JsonPropertyName("trackNumber")] int TrackNumber = 0,
        [property: JsonPropertyName("discNumber")] int DiscNumber = 1,
        [property: JsonPropertyName("explicit")] bool Explicit = false,
        [property: JsonPropertyName("stats")] LucidaStats? Stats = null,
        [property: JsonPropertyName("upc")] string? Upc = null,
        [property: JsonPropertyName("trackCount")] int TrackCount = 0,
        [property: JsonPropertyName("discCount")] int DiscCount = 1,
        [property: JsonPropertyName("releaseDate")] string? ReleaseDate = null)
    {
        [JsonPropertyName("artists")]
        public LucidaArtistInfo[] Artists { get; init; } = Array.Empty<LucidaArtistInfo>();

        [JsonPropertyName("producers")]
        public string[] Producers { get; init; } = Array.Empty<string>();

        [JsonPropertyName("composers")]
        public string[] Composers { get; init; } = Array.Empty<string>();

        [JsonPropertyName("lyricists")]
        public string[] Lyricists { get; init; } = Array.Empty<string>();

        [JsonPropertyName("coverArtwork")]
        public LucidaArtworkInfo[] CoverArtwork { get; init; } = Array.Empty<LucidaArtworkInfo>();

        [JsonPropertyName("tracks")]
        public LucidaTrackInfo[] Tracks { get; init; } = Array.Empty<LucidaTrackInfo>();
    }

    /// <summary>
    /// Artist info from JavaScript data (different structure than search results)
    /// </summary>
    public record LucidaArtistInfo(
        [property: JsonPropertyName("id"), JsonConverter(typeof(FlexibleLongConverter))] long Id,
        [property: JsonPropertyName("name")] string Name = "",
        [property: JsonPropertyName("url")] string Url = "")
    {
        [JsonPropertyName("pictures")]
        public string[] Pictures { get; init; } = Array.Empty<string>();

        public LucidaArtistInfo() : this(0) { }
    };

    /// <summary>
    /// Artwork info from JavaScript data
    /// </summary>
    public record LucidaArtworkInfo(
        [property: JsonPropertyName("url")] string Url = "",
        [property: JsonPropertyName("width")] int Width = 0,
        [property: JsonPropertyName("height")] int Height = 0)
    {
        [JsonIgnore]
        public int PixelCount => Width * Height;
    };

    /// <summary>
    /// Album reference within track info
    /// </summary>
    public record LucidaAlbumRef(
        [property: JsonPropertyName("id"), JsonConverter(typeof(FlexibleLongConverter))] long Id,
        [property: JsonPropertyName("url")] string Url = "",
        [property: JsonPropertyName("title")] string Title = "",
        [property: JsonPropertyName("releaseDate")] string? ReleaseDate = null)
    {
        [JsonPropertyName("coverArtwork")]
        public LucidaArtworkInfo[] CoverArtwork { get; init; } = Array.Empty<LucidaArtworkInfo>();

        public LucidaAlbumRef() : this(0) { }
    };

    /// <summary>
    /// Track info within album
    /// </summary>
    public record LucidaTrackInfo(
        [property: JsonPropertyName("url")] string Url = "",
        [property: JsonPropertyName("id"), JsonConverter(typeof(FlexibleLongConverter))] long Id = 0,
        [property: JsonPropertyName("title")] string Title = "",
        [property: JsonPropertyName("durationMs")] long DurationMs = 0,
        [property: JsonPropertyName("isrc")] string? Isrc = null,
        [property: JsonPropertyName("copyright")] string? Copyright = null,
        [property: JsonPropertyName("trackNumber")] int TrackNumber = 0,
        [property: JsonPropertyName("discNumber")] int DiscNumber = 1,
        [property: JsonPropertyName("explicit")] bool Explicit = false,
        [property: JsonPropertyName("csrf")] string? Csrf = null,
        [property: JsonPropertyName("csrfFallback")] string? CsrfFallback = null)
    {
        [JsonPropertyName("artists")]
        public LucidaArtistInfo[] Artists { get; init; } = Array.Empty<LucidaArtistInfo>();

        [JsonPropertyName("producers")]
        public string[] Producers { get; init; } = Array.Empty<string>();

        [JsonPropertyName("composers")]
        public string[] Composers { get; init; } = Array.Empty<string>();

        [JsonPropertyName("lyricists")]
        public string[] Lyricists { get; init; } = Array.Empty<string>();
    };

    /// <summary>
    /// Statistics/metadata about the service
    /// </summary>
    public record LucidaStats(
        [property: JsonPropertyName("account")] string Account = "",
        [property: JsonPropertyName("country")] string Country = "",
        [property: JsonPropertyName("service")] string Service = "",
        [property: JsonPropertyName("cache")] bool Cache = false);

    #endregion

    #region Application Models

    /// <summary>
    /// Track model for application use
    /// </summary>
    public class LucidaTrackModel
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public List<LucidaArtist> Artists { get; set; } = new();
        public string? AlbumTitle { get; set; }
        public string? AlbumId { get; set; }
        public string? AlbumUrl { get; set; }
        public long DurationMs { get; set; }
        public int TrackNumber { get; set; }
        public int DiscNumber { get; set; } = 1;
        public bool IsExplicit { get; set; }
        public string? Isrc { get; set; }
        public string? ReleaseDate { get; set; }
        public string? Year { get; set; }
        public string? Copyright { get; set; }
        public string? CoverUrl { get; set; }
        public List<LucidaArtworkInfo> CoverArtworks { get; set; } = new();
        public List<string> Composers { get; set; } = new();
        public List<string> Producers { get; set; } = new();
        public List<string> Lyricists { get; set; } = new();

        // Service URLs
        public string? OriginalServiceUrl { get; set; }
        public string? DetailPageUrl { get; set; }
        public string? ServiceName { get; set; }
        public string? Url { get; set; }

        // Authentication tokens
        public string? PrimaryToken { get; set; }
        public string? FallbackToken { get; set; }
        public long TokenExpiry { get; set; }

        [JsonIgnore]
        public bool HasValidTokens => !string.IsNullOrEmpty(PrimaryToken) && !string.IsNullOrEmpty(FallbackToken);

        public string GetBestCoverArtUrl()
        {
            if (CoverArtworks.Count == 0)
                return CoverUrl ?? string.Empty;

            return CoverArtworks
                .Where(a => a.Width > 0 && a.Height > 0)
                .OrderByDescending(a => a.PixelCount)
                .FirstOrDefault()?.Url ?? CoverUrl ?? string.Empty;
        }

        public string FormatDuration()
        {
            if (DurationMs <= 0) return "0:00";
            TimeSpan timeSpan = TimeSpan.FromMilliseconds(DurationMs);
            return $"{(int)timeSpan.TotalMinutes}:{timeSpan.Seconds:D2}";
        }
    }

    /// <summary>
    /// Album model for application use
    /// </summary>
    public class LucidaAlbumModel
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public List<LucidaArtistInfo> Artists { get; set; } = new();
        public int TrackCount { get; set; }
        public int DiscCount { get; set; } = 1;
        public string? ReleaseDate { get; set; }
        public string? Year { get; set; }
        public string? Upc { get; set; }
        public string? Copyright { get; set; }
        public string? CoverUrl { get; set; }
        public List<LucidaArtworkInfo> CoverArtworks { get; set; } = new();
        public List<LucidaTrackModel> Tracks { get; set; } = new();

        // Service URLs
        public string? OriginalServiceUrl { get; set; }
        public string? DetailPageUrl { get; set; }
        public string? ServiceName { get; set; }

        // Authentication tokens
        public string? PrimaryToken { get; set; }
        public string? FallbackToken { get; set; }
        public long TokenExpiry { get; set; }

        [JsonIgnore]
        public bool HasValidTokens => !string.IsNullOrEmpty(PrimaryToken) && !string.IsNullOrEmpty(FallbackToken);

        [JsonIgnore]
        public bool IsValid => !string.IsNullOrEmpty(Title) && !string.IsNullOrEmpty(Artist) && Tracks.Count > 0;

        public string GetBestCoverArtUrl()
        {
            if (CoverArtworks.Count == 0)
                return CoverUrl ?? string.Empty;

            return CoverArtworks
                .Where(a => a.Width > 0 && a.Height > 0)
                .OrderByDescending(a => a.PixelCount)
                .FirstOrDefault()?.Url ?? CoverUrl ?? string.Empty;
        }

        public long GetTotalDurationMs() => Tracks.Sum(t => t.DurationMs);

        public string FormatTotalDuration()
        {
            long totalMs = GetTotalDurationMs();
            if (totalMs <= 0) return "0:00";

            TimeSpan timeSpan = TimeSpan.FromMilliseconds(totalMs);
            return timeSpan.TotalHours >= 1
                ? $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m"
                : $"{timeSpan.Minutes}m {timeSpan.Seconds}s";
        }
    }

    /// <summary>
    /// Authentication tokens
    /// </summary>
    public record LucidaTokens(string Primary, string Fallback, long Expiry)
    {
        public bool IsValid => !string.IsNullOrEmpty(Primary) && !string.IsNullOrEmpty(Fallback);
        public static LucidaTokens Empty => new(string.Empty, string.Empty, 0);
        public static LucidaTokens Create(string primary, string fallback, long? expiry = null) =>
            new(primary, fallback, expiry ?? DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
    }

    #endregion

    #region API Request/Response Models

    /// <summary>
    /// Request for download initiation
    /// </summary>
    public record LucidaDownloadRequestInfo(
        [property: JsonPropertyName("url")] string Url = "",
        [property: JsonPropertyName("metadata")] bool Metadata = true,
        [property: JsonPropertyName("compat")] bool Compat = false,
        [property: JsonPropertyName("private")] bool Private = true,
        [property: JsonPropertyName("handoff")] bool Handoff = true,
        [property: JsonPropertyName("downscale")] string Downscale = "original")
    {
        [JsonPropertyName("account")]
        public LucidaAccountInfo Account { get; init; } = new();

        [JsonPropertyName("upload")]
        public LucidaUploadInfo Upload { get; init; } = new();

        [JsonPropertyName("token")]
        public LucidaTokenData TokenData { get; init; } = new();

        public static LucidaDownloadRequestInfo CreateWithTokens(string url, string primaryToken, string fallbackToken, long expiry) =>
            new(Url: url)
            {
                TokenData = new(primaryToken, fallbackToken, expiry)
            };
    }

    /// <summary>
    /// Account info for requests
    /// </summary>
    public record LucidaAccountInfo(
        [property: JsonPropertyName("type")] string Type = "country",
        [property: JsonPropertyName("id")] string Id = "auto");

    /// <summary>
    /// Upload info for requests
    /// </summary>
    public record LucidaUploadInfo(
        [property: JsonPropertyName("enabled")] bool Enabled = false,
        [property: JsonPropertyName("service")] string Service = "pixeldrain");

    /// <summary>
    /// Token data for authentication
    /// </summary>
    public record LucidaTokenData(
        [property: JsonPropertyName("primary")] string? Primary = null,
        [property: JsonPropertyName("secondary")] string? Secondary = null,
        [property: JsonPropertyName("expiry")] long Expiry = 0);

    /// <summary>
    /// Response from download initiation
    /// </summary>
    public record LucidaDownloadResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("handoff")] string? Handoff = null,
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("server")] string? Server = null,
        [property: JsonPropertyName("error")] string? Error = null,
        [property: JsonPropertyName("stats")] LucidaStatsResponse? Stats = null,
        [property: JsonPropertyName("fromExternal")] bool FromExternal = false);

    /// <summary>
    /// Stats in download response
    /// </summary>
    public record LucidaStatsResponse(
        [property: JsonPropertyName("service")] string? Service = null,
        [property: JsonPropertyName("account")] string? Account = null);

    /// <summary>
    /// Status response for polling
    /// </summary>
    public record LucidaStatusResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("status")] string? Status = null,
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("error")] string? Error = null);

    #endregion
}