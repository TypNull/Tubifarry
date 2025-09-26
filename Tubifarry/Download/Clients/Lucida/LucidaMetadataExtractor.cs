using Jint;
using NLog;
using NzbDrone.Common.Instrumentation;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tubifarry.Download.Base;
using Tubifarry.Indexers.Lucida;

namespace Tubifarry.Download.Clients.Lucida
{
    /// <summary>
    /// Metadata extractor for Lucida pages
    /// Uses System.Text.Json exclusively with Jint fallback for JavaScript execution
    /// </summary>
    public static class LucidaMetadataExtractor
    {
        private static readonly Logger _logger = NzbDroneLogger.GetLogger(typeof(LucidaMetadataExtractor));
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // Regex patterns for JavaScript data extraction
        private static readonly Regex[] DataExtractionRegexes = new[]
        {
            new Regex (@"data\s*=\s*(\[(?:[^\[\]]|\[(?:[^\[\]]|\[(?:[^\[\]]|\[[^\[\]]*\])*\])*\])*\]);", RegexOptions.Compiled | RegexOptions.Singleline),
            new Regex(@"__INITIAL_DATA__\s*=\s*({.+?});", RegexOptions.Compiled | RegexOptions.Singleline)
        };


        /// <summary>
        /// Extracts album metadata from Lucida page
        /// </summary>
        public static async Task<LucidaAlbumModel> ExtractAlbumMetadataAsync(BaseHttpClient httpClient, string url)
        {
            try
            {
                string lucidaUrl = $"{httpClient.BaseUrl}/?url={Uri.EscapeDataString(url)}";
                string html = await httpClient.GetStringAsync(lucidaUrl);

                LucidaAlbumModel album = ExtractAlbumFromHtml(html);
                album.OriginalServiceUrl = url;
                album.DetailPageUrl = lucidaUrl;
                LucidaTokens tokens = LucidaTokenExtractor.ExtractTokensFromHtml(html);
                if (tokens.IsValid)
                    ApplyTokensToAlbum(album, tokens);

                return album;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to extract album metadata from {0}", url);
                throw;
            }
        }

        /// <summary>
        /// Extracts album from HTML
        /// </summary>
        public static LucidaAlbumModel ExtractAlbumFromHtml(string html)
        {
            LucidaInfo? info = ExtractInfo(html);
            if (info?.Success == true && info.Type == "album")
                return ConvertToAlbum(info);

            _logger.Warn($"JSON extraction failed for type {info?.Type ?? "unknown"}");
            return new();
        }

        /// <summary>
        /// Extracts info from embedded JavaScript data
        /// </summary>
        private static LucidaInfo? ExtractInfo(string html)
        {
            try
            {
                string jsData = ExtractJavaScriptFromHtml(html);
                if (string.IsNullOrEmpty(jsData))
                {
                    _logger.Debug("No JavaScript data found");
                    return null;
                }
                _logger.Debug("Extracted JavaScript data: {0} characters", jsData.Length);
                return ExtractWithJint(jsData);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Info extraction failed");
            }

            return null;
        }

        /// <summary>
        /// Extracts JavaScript data from HTML
        /// </summary>
        private static string ExtractJavaScriptFromHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return string.Empty;

            foreach (Regex regex in DataExtractionRegexes)
            {
                Match match = regex.Match(html);
                if (match.Success)
                    return match.Groups[1].Value;

            }
            return string.Empty;
        }

        /// <summary>
        /// Extracts info using Jint JavaScript engine
        /// </summary>
        private static LucidaInfo? ExtractWithJint(string jsData)
        {
            try
            {
                Engine engine = new();
                engine.Execute($@"
                    var data = {jsData};
                    var info = null;
                    for (var i = 0; i < data.length; i++) {{
                        if (data[i].type === 'data' && data[i].data && data[i].data.info) {{
                            info = data[i].data.info;
                            break;
                        }}
                    }}
                ");

                object? infoObj = engine.GetValue("info").ToObject();
                if (infoObj == null)
                    return null;
                string infoJson = JsonSerializer.Serialize(infoObj);
                return JsonSerializer.Deserialize<LucidaInfo>(infoJson, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Jint extraction failed");
                return null;
            }
        }

        /// <summary>
        /// Converts LucidaInfo to LucidaAlbumModel
        /// </summary>
        private static LucidaAlbumModel ConvertToAlbum(LucidaInfo info)
        {
            _logger.Trace("Converting JSON info to album: title={0}, trackCount={1}, tracks.length={2}", info.Title, info.TrackCount, info.Tracks.Length);

            LucidaAlbumModel album = new()
            {
                Id = info.Id.ToString(),
                Title = info.Title,
                Artist = info.Artists.FirstOrDefault()?.Name ?? string.Empty,
                TrackCount = info.TrackCount,
                DiscCount = info.DiscCount,
                Upc = info.Upc,
                Copyright = info.Copyright,
                ReleaseDate = info.ReleaseDate,
                ServiceName = info.Stats?.Service
            };

            album.Artists.AddRange(info.Artists);
            album.CoverArtworks.AddRange(info.CoverArtwork);
            album.CoverUrl = album.GetBestCoverArtUrl();

            foreach (LucidaTrackInfo trackInfo in info.Tracks)
            {
                LucidaTrackModel track = new()
                {
                    Id = trackInfo.Id.ToString(),
                    Title = trackInfo.Title,
                    Artist = trackInfo.Artists.FirstOrDefault()?.Name ?? string.Empty,
                    DurationMs = trackInfo.DurationMs,
                    TrackNumber = trackInfo.TrackNumber,
                    DiscNumber = trackInfo.DiscNumber,
                    IsExplicit = trackInfo.Explicit,
                    Isrc = trackInfo.Isrc,
                    Copyright = trackInfo.Copyright,
                    Url = trackInfo.Url,
                    PrimaryToken = trackInfo.Csrf,
                    FallbackToken = trackInfo.CsrfFallback
                };

                track.Artists.AddRange(trackInfo.Artists.Select(a => new LucidaArtist(a.Id, a.Name, a.Url, a.Pictures?.ToList())));
                track.Composers.AddRange(trackInfo.Composers);
                track.Producers.AddRange(trackInfo.Producers);
                track.Lyricists.AddRange(trackInfo.Lyricists);
                album.Tracks.Add(track);
            }

            if (!string.IsNullOrEmpty(info.ReleaseDate) && DateTime.TryParse(info.ReleaseDate, out DateTime date))
                album.Year = date.Year.ToString();

            _logger.Trace("Album conversion completed: {0} tracks converted", album.Tracks.Count);
            return album;
        }

        /// <summary>
        /// Applies tokens to album and all tracks
        /// </summary>
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
    }
}
