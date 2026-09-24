using Jint;
using Jint.Native;
using NLog;
using NzbDrone.Common.Instrumentation;
using System.Net;
using System.Text.Json;
using Tubifarry.Download.Base;
using Tubifarry.Indexers.Lucida;

namespace Tubifarry.Download.Clients.Lucida;

/// <summary>
/// Metadata extractor for Lucida pages
/// Uses System.Text.Json exclusively with Jint fallback for JavaScript execution
/// </summary>
public static partial class LucidaMetadataExtractor
{
    private static readonly Logger _logger = NzbDroneLogger.GetLogger(typeof(LucidaMetadataExtractor));

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<LucidaAlbumModel> ExtractAlbumMetadataAsync(BaseHttpClient httpClient, string url)
    {
        LucidaAlbumModel? album = null;

        try
        {
            album = await ExtractViaApiAsync(httpClient, url);
            if (album is { IsValid: true })
                _logger.Debug($"Native API returned valid album: {album.Title} ({album.Tracks.Count} tracks)");
        }
        catch (Exception ex)
        {
            _logger.Debug($"Native API metadata failed, falling back to HTML: {ex.Message}");
        }

        for (int attempt = 0; attempt < LucidaRetryPolicy.MaxAttempts && album is not { IsValid: true }; attempt++)
        {
            if (attempt > 0)
            {
                _logger.Debug($"Retrying HTML metadata extraction ({attempt}/{LucidaRetryPolicy.MaxAttempts - 1})");
                await LucidaRetryPolicy.DelayAsync(attempt);
            }

            try
            {
                album = await ExtractViaHtmlAsync(httpClient, url);
                if (album is { IsValid: true })
                    _logger.Debug($"HTML extraction returned valid album: {album.Title} ({album.Tracks.Count} tracks)");
            }
            catch (Exception ex) when (!LucidaRetryPolicy.IsTransient(ex))
            {
                _logger.Warn($"HTML metadata extraction failed for {url}: {ex.Message}");
                break;
            }
            catch (Exception ex) when (!LucidaRetryPolicy.IsLastAttempt(attempt))
            {
                _logger.Warn(ex, "HTML metadata extraction failed for {0}, retrying", url);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "HTML metadata extraction also failed for {0}", url);
            }
        }

        if (album is not { IsValid: true })
        {
            _logger.Error("All metadata extraction strategies failed for {0}", url);
            return new LucidaAlbumModel { OriginalServiceUrl = url };
        }

        bool hasCsrfTokens = album.Tracks.Any(t => t.HasValidTokens) || album.HasValidTokens;
        if (!hasCsrfTokens)
        {
            _logger.Debug("Album lacks CSRF tokens, fetching from HTML page...");
            try
            {
                await EnrichWithCsrfTokensAsync(httpClient, url, album);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to enrich album with CSRF tokens");
            }
        }

        album.OriginalServiceUrl = url;
        return album;
    }

    private static async Task<LucidaAlbumModel?> ExtractViaApiAsync(BaseHttpClient httpClient, string serviceUrl)
    {
        string apiUrl = $"{httpClient.BaseUrl}/api/load?url={Uri.EscapeDataString($"/api/fetch/metadata?url={serviceUrl}")}";

        _logger.Trace($"Fetching metadata via API: {apiUrl}");

        using HttpResponseMessage response = await httpClient.GetAsync(apiUrl);
        string body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode == HttpStatusCode.NotFound ||
            body.Contains("<title>404 Not Found</title>", StringComparison.OrdinalIgnoreCase) ||
            IsCharIndexed404(body))
        {
            _logger.Debug("Metadata API returned 404");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.Debug($"Metadata API returned HTTP {(int)response.StatusCode}");
            return null;
        }

        string? contentType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Debug("Metadata API returned HTML instead of JSON");
            return null;
        }

        LucidaInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<LucidaInfo>(body, Json);
        }
        catch (JsonException ex)
        {
            _logger.Debug($"Metadata API response not valid JSON: {ex.Message}");
            return null;
        }

        if (info is null || !info.Success)
        {
            _logger.Debug($"Metadata API returned success={info?.Success}, type={info?.Type}");
            return null;
        }

        if (string.Equals(info.Type, "album", StringComparison.OrdinalIgnoreCase))
            return ConvertToAlbum(info);

        if (string.Equals(info.Type, "track", StringComparison.OrdinalIgnoreCase))
            return ConvertSingleTrackToAlbum(info);

        _logger.Debug($"Metadata API returned unexpected type: {info.Type}");
        return null;
    }

    private static async Task<LucidaAlbumModel?> ExtractViaHtmlAsync(BaseHttpClient httpClient, string serviceUrl)
    {
        string pageUrl = $"{httpClient.BaseUrl}/?url={Uri.EscapeDataString(serviceUrl)}";

        _logger.Trace($"Fetching HTML page: {pageUrl}");

        string html;
        HttpStatusCode statusCode;
        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(pageUrl);
            html = await response.Content.ReadAsStringAsync();
            statusCode = response.StatusCode;
        }
        catch (Exception ex)
        {
            _logger.Debug($"HTML page fetch failed: {ex.Message}");
            return null;
        }

        if ((int)statusCode is < 200 or > 299)
        {
            _logger.Debug($"HTML page returned HTTP {(int)statusCode}");
            if (!LucidaRetryPolicy.IsTransient(statusCode))
                throw new HttpRequestException($"HTML page returned HTTP {(int)statusCode}", null, statusCode);
            return null;
        }

        if (string.IsNullOrWhiteSpace(html) || html.Length < 200)
        {
            _logger.Debug("HTML page is empty or too short");
            return null;
        }

        LucidaInfo? info = ExtractInfoFromHtml(html);

        if (info is null || !info.Success)
        {
            _logger.Warn($"HTML/Jint extraction failed: success={info?.Success}, type={info?.Type}");
            return null;
        }

        LucidaAlbumModel? album = info.Type?.ToLowerInvariant() switch
        {
            "album" => ConvertToAlbum(info),
            "track" => ConvertSingleTrackToAlbum(info),
            _ => null
        };

        if (album is null)
        {
            _logger.Warn($"Unsupported info type from HTML: {info.Type}");
            return null;
        }

        LucidaTokens pageTokens = LucidaTokenExtractor.ExtractTokensFromHtml(html);
        if (pageTokens.IsValid)
            ApplyTokensToAlbum(album, pageTokens);

        album.DetailPageUrl = pageUrl;
        return album;
    }

    private static async Task EnrichWithCsrfTokensAsync(
        BaseHttpClient httpClient, string serviceUrl, LucidaAlbumModel album)
    {
        string pageUrl = $"{httpClient.BaseUrl}/?url={Uri.EscapeDataString(serviceUrl)}";

        string html;
        try
        {
            using HttpResponseMessage resp = await httpClient.GetAsync(pageUrl);
            html = await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.Debug($"CSRF token fetch failed: {ex.Message}");
            return;
        }

        LucidaTokens pageTokens = LucidaTokenExtractor.ExtractTokensFromHtml(html);
        if (pageTokens.IsValid)
        {
            ApplyTokensToAlbum(album, pageTokens);
            _logger.Debug("Applied page-level CSRF tokens to album");
            return;
        }

        LucidaInfo? info = ExtractInfoFromHtml(html);
        if (info?.Tracks is null) return;

        foreach (LucidaTrackInfo trackInfo in info.Tracks)
        {
            if (string.IsNullOrEmpty(trackInfo.Csrf)) continue;

            LucidaTrackModel? match = album.Tracks.FirstOrDefault(t =>
                (!string.IsNullOrEmpty(t.Id) && t.Id == trackInfo.Id) ||
                (!string.IsNullOrEmpty(t.Url) && t.Url == trackInfo.Url) ||
                (t.TrackNumber == trackInfo.TrackNumber && t.DiscNumber == trackInfo.DiscNumber));

            if (match is not null)
            {
                match.PrimaryToken = trackInfo.Csrf;
                match.FallbackToken = trackInfo.CsrfFallback ?? trackInfo.Csrf;
                match.TokenExpiry = pageTokens.Expiry > 0
                    ? pageTokens.Expiry
                    : DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            }
        }

        _logger.Debug($"Applied per-track CSRF tokens: {album.Tracks.Count(t => t.HasValidTokens)}/{album.Tracks.Count} tracks");
    }

    private static LucidaInfo? ExtractInfoFromHtml(string html)
    {
        try
        {
            string jsonArray = ExtractJsonArrayFromHtml(html);
            if (string.IsNullOrEmpty(jsonArray))
            {
                _logger.Debug("No data JSON array found in HTML");
                return null;
            }

            _logger.Trace($"Extracted data array: {jsonArray.Length} chars");
            return ParseDataArray(jsonArray);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "HTML info extraction failed");
            return null;
        }
    }

    private static string ExtractJsonArrayFromHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        int dataIdx = -1;
        string[] needles = ["const data = [", "var data = [", "data\t=\t[", "data = ["];

        foreach (string needle in needles)
        {
            dataIdx = html.IndexOf(needle, StringComparison.Ordinal);
            if (dataIdx >= 0)
            {
                dataIdx = html.IndexOf('[', dataIdx);
                break;
            }
        }

        if (dataIdx < 0)
        {
            _logger.Debug("No 'data = [' assignment found in HTML");
            return string.Empty;
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = dataIdx; i < html.Length; i++)
        {
            char c = html[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (c == '"' && !escaped)
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c == '[') depth++;
            else if (c == ']') depth--;

            if (depth == 0)
            {
                string jsonArray = html[dataIdx..(i + 1)];
                _logger.Trace($"Extracted JSON array: {jsonArray.Length} chars");
                return jsonArray;
            }
        }

        _logger.Debug("Bracket matching failed: unbalanced brackets in data array");
        return string.Empty;
    }

    private static LucidaInfo? ParseDataArray(string jsonArray)
    {
        if (string.IsNullOrEmpty(jsonArray))
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonArray);

            foreach (JsonElement entry in doc.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("type", out JsonElement typeEl) &&
                    typeEl.GetString() == "data" &&
                    entry.TryGetProperty("data", out JsonElement dataEl) &&
                    dataEl.TryGetProperty("info", out JsonElement infoEl))
                {
                    string infoJson = infoEl.GetRawText();
                    _logger.Trace($"Found info object via direct JSON: {infoJson.Length} chars");
                    return JsonSerializer.Deserialize<LucidaInfo>(infoJson, Json);
                }
            }

            _logger.Debug("No 'data.info' entry found in JSON array");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.Debug($"Direct JSON parse failed, trying Jint fallback: {ex.Message}");
        }

        return ExecuteJintOnDataArray(jsonArray);
    }

    private static LucidaInfo? ExecuteJintOnDataArray(string jsonArray)
    {
        try
        {
            Engine engine = LucidaJintEngine.Create();

            engine.Execute($@"
                var __data = {jsonArray};
                var __infoJson = null;

                if (__data && __data.length) {{
                    for (var i = 0; i < __data.length; i++) {{
                        var e = __data[i];
                        if (e && e.type === 'data' && e.data && e.data.info) {{
                            __infoJson = JSON.stringify(e.data.info);
                            break;
                        }}
                    }}
                }}
            ");

            JsValue val = engine.GetValue("__infoJson");
            if (val.IsNull() || val.IsUndefined())
            {
                _logger.Debug("Jint fallback: no info found in data array");
                return null;
            }

            string json = val.AsString();
            _logger.Trace($"Jint fallback JSON.stringify: {json.Length} chars");
            return JsonSerializer.Deserialize<LucidaInfo>(json, Json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Jint fallback extraction failed");
            return null;
        }
    }
    private static LucidaAlbumModel ConvertToAlbum(LucidaInfo info)
    {
        _logger.Trace($"Converting info to album: title={info.Title}, " +
                       $"trackCount={info.TrackCount}, tracks.length={info.Tracks?.Length ?? 0}");

        LucidaAlbumModel album = new()
        {
            Id = info.Id,
            Title = info.Title,
            Artist = info.Artists?.FirstOrDefault()?.Name ?? string.Empty,
            TrackCount = info.TrackCount,
            DiscCount = info.DiscCount,
            Upc = info.Upc,
            Copyright = info.Copyright,
            ReleaseDate = info.ReleaseDate,
            ServiceName = info.Stats?.Service
        };

        if (info.Artists is not null)
            album.Artists.AddRange(info.Artists);

        if (info.CoverArtwork is not null)
            album.CoverArtworks.AddRange(info.CoverArtwork);

        album.CoverUrl = album.GetBestCoverArtUrl();

        if (info.Tracks is not null)
        {
            foreach (LucidaTrackInfo ti in info.Tracks)
            {
                LucidaTrackModel track = new()
                {
                    Id = ti.Id,
                    Title = ti.Title,
                    Artist = ti.Artists?.FirstOrDefault()?.Name ?? album.Artist,
                    DurationMs = ti.DurationMs,
                    TrackNumber = ti.TrackNumber,
                    DiscNumber = ti.DiscNumber,
                    IsExplicit = ti.Explicit,
                    Isrc = ti.Isrc,
                    Copyright = ti.Copyright,
                    Url = ti.Url,
                    PrimaryToken = ti.Csrf,
                    FallbackToken = ti.CsrfFallback
                };

                if (ti.Artists is not null)
                    track.Artists.AddRange(ti.Artists.Select(
                        a => new LucidaArtist(a.Id, a.Name, a.Url, a.Pictures?.ToList())));

                if (ti.Composers is not null) track.Composers.AddRange(ti.Composers);
                if (ti.Producers is not null) track.Producers.AddRange(ti.Producers);
                if (ti.Lyricists is not null) track.Lyricists.AddRange(ti.Lyricists);

                album.Tracks.Add(track);
            }
        }

        if (!string.IsNullOrEmpty(info.ReleaseDate) && info.ReleaseDate.Length >= 4)
            album.Year = info.ReleaseDate[..4];

        _logger.Trace($"Album conversion done: {album.Tracks.Count} tracks");
        return album;
    }

    private static LucidaAlbumModel ConvertSingleTrackToAlbum(LucidaInfo info)
    {
        _logger.Trace($"Converting single track to album wrapper: {info.Title}");

        string albumTitle = info.Album?.Title ?? info.Title;
        string albumArtist = info.Artists?.FirstOrDefault()?.Name ?? string.Empty;

        LucidaAlbumModel album = new()
        {
            Id = info.Album?.Id ?? info.Id,
            Title = albumTitle,
            Artist = albumArtist,
            TrackCount = 1,
            DiscCount = 1,
            Copyright = info.Copyright,
            ReleaseDate = info.Album?.ReleaseDate ?? info.ReleaseDate,
            ServiceName = info.Stats?.Service
        };

        if (info.Artists is not null)
            album.Artists.AddRange(info.Artists);

        LucidaArtworkInfo[]? artworks = info.Album?.CoverArtwork ?? info.CoverArtwork;
        if (artworks is not null)
            album.CoverArtworks.AddRange(artworks);

        album.CoverUrl = album.GetBestCoverArtUrl();

        LucidaTrackModel track = new()
        {
            Id = info.Id,
            Title = info.Title,
            Artist = albumArtist,
            DurationMs = info.DurationMs,
            TrackNumber = info.TrackNumber,
            DiscNumber = info.DiscNumber,
            IsExplicit = info.Explicit,
            Isrc = info.Isrc,
            Copyright = info.Copyright,
            Url = info.Url
        };

        if (info.Artists is not null)
            track.Artists.AddRange(info.Artists.Select(
                a => new LucidaArtist(a.Id, a.Name, a.Url, a.Pictures?.ToList())));

        if (info.Composers is not null) track.Composers.AddRange(info.Composers);
        if (info.Producers is not null) track.Producers.AddRange(info.Producers);
        if (info.Lyricists is not null) track.Lyricists.AddRange(info.Lyricists);

        album.Tracks.Add(track);

        if (!string.IsNullOrEmpty(album.ReleaseDate) && album.ReleaseDate.Length >= 4)
            album.Year = album.ReleaseDate[..4];

        return album;
    }
    private static void ApplyTokensToAlbum(LucidaAlbumModel album, LucidaTokens tokens)
    {
        album.PrimaryToken = tokens.Primary;
        album.FallbackToken = tokens.Fallback;
        album.TokenExpiry = tokens.Expiry;

        foreach (LucidaTrackModel track in album.Tracks)
        {
            if (string.IsNullOrEmpty(track.PrimaryToken))
            {
                track.PrimaryToken = tokens.Primary;
                track.FallbackToken = tokens.Fallback;
                track.TokenExpiry = tokens.Expiry;
            }
        }
    }
    private static bool IsCharIndexed404(string body)
    {
        if (string.IsNullOrEmpty(body) || body.Length < 30) return false;
        return body.StartsWith("{\"0\":\"<", StringComparison.Ordinal) ||
              (body.StartsWith("{\"0\":\"", StringComparison.Ordinal) &&
               body.Contains("\"fromExternal\"", StringComparison.Ordinal));
    }
}