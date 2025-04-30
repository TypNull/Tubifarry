using System.Text.Json.Serialization;

namespace Tubifarry.Download.Clients.Lucida.Models
{
    /// <summary>
    /// Response from the download initiation endpoint
    /// </summary>
    public record DownloadResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("handoff")]
        public string? Handoff { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("stats")]
        public StatsInfo? Stats { get; set; }

        [JsonPropertyName("fromExternal")]
        public bool FromExternal { get; set; }

        [JsonPropertyName("server")]
        public string? Server { get; set; }
    }

    /// <summary>
    /// Stats information in download response
    /// </summary>
    public record StatsInfo
    {
        [JsonPropertyName("service")]
        public string? Service { get; set; }

        [JsonPropertyName("account")]
        public string? Account { get; set; }
    }

    /// <summary>
    /// Request body for download initiation
    /// </summary>
    public record DownloadRequest
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("metadata")]
        public bool Metadata { get; set; } = true;

        [JsonPropertyName("compat")]
        public bool Compat { get; set; } = false;

        [JsonPropertyName("private")]
        public bool Private { get; set; } = true;

        [JsonPropertyName("handoff")]
        public bool Handoff { get; set; } = true;

        [JsonPropertyName("account")]
        public AccountInfo Account { get; set; } = new();

        [JsonPropertyName("upload")]
        public UploadInfo Upload { get; set; } = new();

        [JsonPropertyName("downscale")]
        public string Downscale { get; set; } = "original";

        [JsonPropertyName("token")]
        public TokenInfo TokenData { get; set; } = new();

        // Factory methods for creating common requests
        public static DownloadRequest CreateStandard(string url) =>
            new() { Url = url };

        public static DownloadRequest CreateWithTokens(string url, string primaryToken, string fallbackToken, long expiry) =>
            new()
            {
                Url = url,
                TokenData = new()
                {
                    Primary = primaryToken,
                    Secondary = fallbackToken,
                    Expiry = expiry
                }
            };
    }

    /// <summary>
    /// Account information for request
    /// </summary>
    public record AccountInfo
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "country";

        [JsonPropertyName("id")]
        public string Id { get; set; } = "auto";
    }

    /// <summary>
    /// Upload information for request
    /// </summary>
    public record UploadInfo
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        [JsonPropertyName("service")]
        public string Service { get; set; } = "pixeldrain";
    }

    /// <summary>
    /// Token information for authentication
    /// </summary>
    public record TokenInfo
    {
        [JsonPropertyName("primary")]
        public string? Primary { get; set; }

        [JsonPropertyName("secondary")]
        public string? Secondary { get; set; }

        [JsonPropertyName("expiry")]
        public long Expiry { get; set; }
    }

    /// <summary>
    /// Result of a download operation
    /// </summary>
    public record DownloadResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("filePath")]
        public string? FilePath { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        // Factory methods for common results
        public static DownloadResult Successful(string filePath) =>
            new() { Success = true, FilePath = filePath };

        public static DownloadResult Failed(string errorMessage) =>
            new() { Success = false, ErrorMessage = errorMessage };
    }

    /// <summary>
    /// Information about a track in an album
    /// </summary>
    public record TrackInfo
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("primaryToken")]
        public string? PrimaryToken { get; set; }

        [JsonPropertyName("fallbackToken")]
        public string? FallbackToken { get; set; }

        [JsonPropertyName("tokenExpiry")]
        public long TokenExpiry { get; set; }

        [JsonPropertyName("trackNumber")]
        public int TrackNumber { get; set; }

        /// <summary>
        /// Token validation helper
        /// </summary>
        [JsonIgnore]
        public bool HasValidTokens => !string.IsNullOrEmpty(PrimaryToken) && !string.IsNullOrEmpty(FallbackToken);
    }

    /// <summary>
    /// Information about an album and its tracks
    /// </summary>
    public record AlbumInfo
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("artist")]
        public string Artist { get; set; } = string.Empty;

        [JsonPropertyName("tracks")]
        public List<TrackInfo> Tracks { get; set; } = new List<TrackInfo>();

        // Default constructor for compatibility with existing code
        public AlbumInfo() { }

        // Main constructor for creating properly initialized records
        public AlbumInfo(string title, string artist, IEnumerable<TrackInfo>? tracks = null)
        {
            Title = title;
            Artist = artist;
            Tracks = tracks?.ToList() ?? new List<TrackInfo>();
        }

        // Helper properties
        [JsonIgnore]
        public int TrackCount => Tracks.Count;

        [JsonIgnore]
        public bool IsValid => !string.IsNullOrEmpty(Title) && !string.IsNullOrEmpty(Artist) && Tracks.Any();
    }

    /// <summary>
    /// Status response for a download
    /// </summary>
    public record StatusResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}