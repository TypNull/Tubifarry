using System.Net;
using System.Text.Json.Serialization;

namespace Tubifarry.Notifications.FlareSolverr;

/// <summary>
/// Represents a cookie returned from FlareSolverr
/// </summary>
public record FlareCookie(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("path")] string Path = "/",
    [property: JsonPropertyName("expires")] int Expiry = 0,
    [property: JsonPropertyName("httpOnly")] bool HttpOnly = false,
    [property: JsonPropertyName("secure")] bool Secure = false,
    [property: JsonPropertyName("sameSite")] string? SameSite = null)
{
    /// <summary>
    /// Converts to System.Net.Cookie
    /// </summary>
    public Cookie ToCookie() => new(Name, Value, Path, Domain)
    {
        HttpOnly = HttpOnly,
        Secure = Secure
    };

    /// <summary>
    /// Converts to Cookie header value
    /// </summary>
    public string ToHeaderValue() => $"{Name}={Value}";
}

/// <summary>
/// Base request for FlareSolverr API
/// </summary>
public record FlareRequest(
    [property: JsonPropertyName("cmd")] string Command,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("maxTimeout")] int MaxTimeout = 60000);

/// <summary>
/// Solution data from FlareSolverr
/// </summary>
public record FlareSolution(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("cookies")] FlareCookie[] Cookies,
    [property: JsonPropertyName("userAgent")] string? UserAgent = null,
    [property: JsonPropertyName("response")] string? Response = null)
{
    /// <summary>
    /// Checks if the solution contains valid cookies
    /// </summary>
    public bool HasValidCookies => Cookies?.Length > 0;
}

/// <summary>
/// Response from FlareSolverr API
/// </summary>
public record FlareResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("solution")] FlareSolution? Solution = null,
    [property: JsonPropertyName("startTimestamp")] long StartTimestamp = 0,
    [property: JsonPropertyName("endTimestamp")] long EndTimestamp = 0,
    [property: JsonPropertyName("version")] string? Version = null)
{
    /// <summary>
    /// Checks if the request was successful
    /// </summary>
    public bool IsSuccess => Status.Equals("ok", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the duration of the request in milliseconds
    /// </summary>
    public long DurationMs => EndTimestamp - StartTimestamp;
}

/// <summary>
/// FlareSolverr index/health check response
/// </summary>
public record FlareIndexResponse(
    [property: JsonPropertyName("msg")] string Message,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("userAgent")] string? UserAgent = null);