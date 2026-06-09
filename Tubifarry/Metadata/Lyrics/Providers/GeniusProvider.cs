using Newtonsoft.Json.Linq;
using NLog;
using System.Text.RegularExpressions;
using Tubifarry.Core.Records;
using Tubifarry.Metadata.Lyrics.Converters;

namespace Tubifarry.Metadata.Lyrics.Providers
{
    public partial class GeniusProvider(HttpClient httpClient, Logger logger, LyricsEnhancerSettings settings)
    {
        public async Task<Lyric?> FetchLyricsAsync(string artistName, string trackTitle)
        {
            try
            {
                JToken? bestMatch = await SearchSongOnGeniusAsync(artistName, trackTitle);
                if (bestMatch == null)
                    return null;

                string? songPath = bestMatch["result"]?["path"]?.ToString();
                if (string.IsNullOrEmpty(songPath))
                {
                    logger.Warn("Could not find song path in Genius response");
                    return null;
                }

                string? plainLyrics = await ExtractLyricsFromGeniusPageAsync(songPath);
                if (string.IsNullOrWhiteSpace(plainLyrics))
                    return null;

                return new PlainTextConverter().Read(plainLyrics);
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error fetching lyrics from Genius for track: {trackTitle} by {artistName}");
                return null;
            }
        }

        private async Task<JToken?> SearchSongOnGeniusAsync(string artistName, string trackTitle)
        {
            string searchUrl = $"https://api.genius.com/search?q={Uri.EscapeDataString($"{artistName} {trackTitle}")}";
            logger.Debug($"Searching for track on Genius: {searchUrl}");

            using HttpRequestMessage request = new(HttpMethod.Get, searchUrl);
            request.Headers.Add("Authorization", $"Bearer {settings.GeniusApiKey}");

            HttpResponseMessage response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                logger.Warn($"Failed to search Genius. Status: {response.StatusCode}");
                return null;
            }

            string responseContent = await response.Content.ReadAsStringAsync();
            JObject? searchJson = JObject.Parse(responseContent);

            if (searchJson?["response"] == null)
            {
                logger.Warn("Invalid response format from Genius API");
                return null;
            }

            if (searchJson["response"]?["hits"] is not JArray hits || hits.Count == 0)
            {
                logger.Debug($"No results found on Genius for: {trackTitle} by {artistName}");
                return null;
            }

            List<JToken> songHits = hits.Where(h => h["type"]?.ToString() == "song" && h["result"] != null).ToList();

            if (songHits.Count == 0)
            {
                logger.Debug("No songs found in search results");
                return null;
            }

            List<JToken> artistMatches = songHits.Where(h => string.Equals(h["result"]?["primary_artist"]?["name"]?.ToString() ?? string.Empty,
                    artistName, StringComparison.OrdinalIgnoreCase)).ToList();

            logger.Trace($"Found {artistMatches.Count} tracks by exact artist name '{artistName}'");

            return LyricsHelper.ScoreAndSelectBestMatch(artistMatches, songHits, artistName, trackTitle, logger);
        }

        private async Task<string?> ExtractLyricsFromGeniusPageAsync(string songPath)
        {
            string songUrl = $"https://genius.com{songPath}";
            logger.Trace($"Fetching lyrics from Genius page: {songUrl}");

            HttpResponseMessage? pageResponse = await httpClient.GetAsync(songUrl);

            if (pageResponse?.IsSuccessStatusCode != true)
            {
                logger.Warn($"Failed to fetch Genius lyrics page. Status: {pageResponse?.StatusCode}");
                return null;
            }

            string html = await pageResponse.Content.ReadAsStringAsync();
            logger.Trace("Attempting to extract lyrics using multiple regex patterns");

            string? plainLyrics = ExtractLyricsFromHtml(html);

            if (string.IsNullOrWhiteSpace(plainLyrics))
            {
                logger.Debug("Extracted lyrics from Genius are empty");
                return null;
            }

            return plainLyrics;
        }

        private string? ExtractLyricsFromHtml(string html)
        {
            List<string> lyricsContainers = DataLyricsContainerRegex().Matches(html)
                .Select(m => m.Groups[1].Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            if (lyricsContainers.Count == 0)
            {
                Match classicMatch = ClassicLyricsClassRegex().Match(html);
                if (classicMatch.Success)
                    lyricsContainers.Add(classicMatch.Groups[1].Value);
            }

            if (lyricsContainers.Count == 0)
            {
                Match rootMatch = LyricsRootIdRegex().Match(html);
                if (rootMatch.Success)
                    lyricsContainers.Add(rootMatch.Groups[1].Value);
            }

            if (lyricsContainers.Count == 0)
            {
                logger.Debug("No matching lyrics pattern found in HTML");
                return null;
            }

            logger.Trace($"Found {lyricsContainers.Count} potential lyrics container(s). Processing...");

            List<string> validLyricsBlocks = new();

            foreach (string lyricsHtml in lyricsContainers)
            {
                string plainLyrics = BrTagRegex().Replace(lyricsHtml, "\n");
                plainLyrics = ItalicTagRegex().Replace(plainLyrics, "");
                plainLyrics = BoldTagRegex().Replace(plainLyrics, "");
                plainLyrics = AnchorTagRegex().Replace(plainLyrics, "");
                plainLyrics = AllHtmlTagsRegex().Replace(plainLyrics, "");
                plainLyrics = System.Web.HttpUtility.HtmlDecode(plainLyrics).Trim();

                if (string.IsNullOrWhiteSpace(plainLyrics))
                    continue;

                if (ContributorsOnlyRegex().IsMatch(plainLyrics))
                {
                    logger.Trace($"Ignoring non-lyrics Genius block: '{plainLyrics}'");
                    continue;
                }

                validLyricsBlocks.Add(plainLyrics);
            }

            if (validLyricsBlocks.Count == 0)
            {
                logger.Debug("No valid lyrics blocks found in Genius HTML");
                return null;
            }

            return string.Join("\n", validLyricsBlocks).Trim();
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

        [GeneratedRegex(@"^\s*\d[\d.,KkMm]*\s+contributors?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase, "de-DE")]
        private static partial Regex ContributorsOnlyRegex();
    }
}
