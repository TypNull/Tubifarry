using Newtonsoft.Json.Linq;
using NLog;
using System.Text.RegularExpressions;
using Tubifarry.Core.Records;

namespace Tubifarry.Metadata.Lyrics
{
    /// <summary>
    /// Handles fetching lyrics from LRCLIB and Genius APIs.
    /// </summary>
    public partial class LyricsProviders
    {
        private readonly HttpClient _httpClient;
        private readonly Logger _logger;
        private readonly LyricsEnhancerSettings _settings;

        public LyricsProviders(HttpClient httpClient, Logger logger, LyricsEnhancerSettings settings)
        {
            _httpClient = httpClient;
            _logger = logger;
            _settings = settings;
        }

        #region LRCLIB Provider

        public async Task<Lyric?> FetchFromLrcLibAsync(string artistName, string trackTitle, string albumName, int duration)
        {
            try
            {
                string requestUri = $"{_settings.LrcLibInstanceUrl}/api/get?artist_name={Uri.EscapeDataString(artistName)}&track_name={Uri.EscapeDataString(trackTitle)}{(string.IsNullOrEmpty(albumName) ? "" : $"&album_name={Uri.EscapeDataString(albumName)}")}{(duration != 0 ? $"&duration={duration}" : "")}";

                _logger.Trace($"Requesting lyrics from LRCLIB: {requestUri}");

                HttpResponseMessage response = await _httpClient.GetAsync(requestUri);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn($"Failed to fetch lyrics from LRCLIB. Status: {response.StatusCode}");
                    return null;
                }

                string content = await response.Content.ReadAsStringAsync();
                JObject? json = JObject.Parse(content);

                if (json == null)
                {
                    _logger.Warn("Failed to parse JSON response from LRCLIB");
                    return null;
                }

                string plainLyrics = json["plainLyrics"]?.ToString() ?? string.Empty;
                string syncedLyricsStr = json["syncedLyrics"]?.ToString() ?? string.Empty;

                List<SyncLine>? syncedLyrics = SyncLine.ParseSyncedLyrics(syncedLyricsStr);

                if (string.IsNullOrWhiteSpace(plainLyrics) && (syncedLyrics == null || syncedLyrics.Count == 0))
                {
                    _logger.Debug($"No lyrics found from LRCLIB for track: {trackTitle} by {artistName}");
                    return null;
                }

                return new Lyric(plainLyrics, syncedLyrics);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error fetching lyrics from LRCLIB for track: {trackTitle} by {artistName}");
                return null;
            }
        }

        #endregion

        #region Genius Provider

        public async Task<Lyric?> FetchFromGeniusAsync(string artistName, string trackTitle)
        {
            try
            {
                JToken? bestMatch = await SearchSongOnGeniusAsync(artistName, trackTitle);
                if (bestMatch == null)
                    return null;

                string? songPath = bestMatch["result"]?["path"]?.ToString();
                if (string.IsNullOrEmpty(songPath))
                {
                    _logger.Warn("Could not find song path in Genius response");
                    return null;
                }

                string? plainLyrics = await ExtractLyricsFromGeniusPageAsync(songPath);
                if (string.IsNullOrWhiteSpace(plainLyrics))
                    return null;

                return new Lyric(plainLyrics, null);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error fetching lyrics from Genius for track: {trackTitle} by {artistName}");
                return null;
            }
        }

        private async Task<JToken?> SearchSongOnGeniusAsync(string artistName, string trackTitle)
        {
            string searchUrl = $"https://api.genius.com/search?q={Uri.EscapeDataString($"{artistName} {trackTitle}")}";
            _logger.Debug($"Searching for track on Genius: {searchUrl}");

            using HttpRequestMessage request = new(HttpMethod.Get, searchUrl);
            request.Headers.Add("Authorization", $"Bearer {_settings.GeniusApiKey}");

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"Failed to search Genius. Status: {response.StatusCode}");
                return null;
            }

            string responseContent = await response.Content.ReadAsStringAsync();
            JObject? searchJson = JObject.Parse(responseContent);

            if (searchJson?["response"] == null)
            {
                _logger.Warn("Invalid response format from Genius API");
                return null;
            }

            if (searchJson["response"]?["hits"] is not JArray hits || hits.Count == 0)
            {
                _logger.Debug($"No results found on Genius for: {trackTitle} by {artistName}");
                return null;
            }

            List<JToken> songHits = hits.Where(h => h["type"]?.ToString() == "song" && h["result"] != null).ToList();

            if (songHits.Count == 0)
            {
                _logger.Debug("No songs found in search results");
                return null;
            }

            List<JToken> artistMatches = songHits.Where(h => string.Equals(h["result"]?["primary_artist"]?["name"]?.ToString() ?? string.Empty,
                    artistName, StringComparison.OrdinalIgnoreCase)).ToList();

            _logger.Trace($"Found {artistMatches.Count} tracks by exact artist name '{artistName}'");

            return LyricsHelper.ScoreAndSelectBestMatch(artistMatches, songHits, artistName, trackTitle, _logger);
        }

        private async Task<string?> ExtractLyricsFromGeniusPageAsync(string songPath)
        {
            string songUrl = $"https://genius.com{songPath}";
            _logger.Trace($"Fetching lyrics from Genius page: {songUrl}");

            HttpResponseMessage? pageResponse = await _httpClient.GetAsync(songUrl);

            if (pageResponse?.IsSuccessStatusCode != true)
            {
                _logger.Warn($"Failed to fetch Genius lyrics page. Status: {pageResponse?.StatusCode}");
                return null;
            }

            string html = await pageResponse.Content.ReadAsStringAsync();
            _logger.Trace("Attempting to extract lyrics using multiple regex patterns");

            string? plainLyrics = ExtractLyricsFromHtml(html);

            if (string.IsNullOrWhiteSpace(plainLyrics))
            {
                _logger.Debug("Extracted lyrics from Genius are empty");
                return null;
            }

            return plainLyrics;
        }

        private string? ExtractLyricsFromHtml(string html)
        {
            Match match = DataLyricsContainerRegex().Match(html);

            if (!match.Success)
                match = ClassicLyricsClassRegex().Match(html);
            if (!match.Success)
                match = LyricsRootIdRegex().Match(html);

            if (match.Success)
            {
                _logger.Trace("Match found. Processing lyrics HTML...");
                string lyricsHtml = match.Groups[1].Value;

                string plainLyrics = BrTagRegex().Replace(lyricsHtml, "\n");
                plainLyrics = ItalicTagRegex().Replace(plainLyrics, "");
                plainLyrics = BoldTagRegex().Replace(plainLyrics, "");
                plainLyrics = AnchorTagRegex().Replace(plainLyrics, "");
                plainLyrics = AllHtmlTagsRegex().Replace(plainLyrics, "");
                plainLyrics = System.Web.HttpUtility.HtmlDecode(plainLyrics).Trim();
                return plainLyrics;
            }
            else
            {
                _logger.Debug("No matching lyrics pattern found in HTML");
                return null;
            }
        }


        [GeneratedRegex(@"<div[^>]*data-lyrics-container[^>]*>(.*?)<\/div>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline, "de-DE")]
        private static partial Regex DataLyricsContainerRegex();

        [GeneratedRegex(@"<div[^>]*class=""[^""]*lyrics[^""]*""[^>]*>(.*?)<\/div>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline, "de-DE")]
        private static partial Regex ClassicLyricsClassRegex();

        [GeneratedRegex(@"<div[^>]*id=""lyrics-root[^""]*""[^>]*>(.*?)<\/div>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline, "de-DE")]
        private static partial Regex LyricsRootIdRegex();

        [GeneratedRegex(@"<br[^>]*>", RegexOptions.Compiled)]
        private static partial Regex BrTagRegex();

        [GeneratedRegex(@"</?i[^>]*>", RegexOptions.Compiled)]
        private static partial Regex ItalicTagRegex();

        [GeneratedRegex(@"</?b[^>]*>", RegexOptions.Compiled)]
        private static partial Regex BoldTagRegex();

        [GeneratedRegex(@"</?a[^>]*>", RegexOptions.Compiled)]
        private static partial Regex AnchorTagRegex();

        [GeneratedRegex(@"<[^>]*>", RegexOptions.Compiled)]
        private static partial Regex AllHtmlTagsRegex();

        #endregion
    }
}
