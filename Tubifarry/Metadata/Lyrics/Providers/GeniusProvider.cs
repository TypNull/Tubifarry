using Newtonsoft.Json.Linq;
using NLog;
using System.Text.RegularExpressions;
using LidarrHttp = NzbDrone.Common.Http;
using Tubifarry.Core.Records;
using Tubifarry.Metadata.Lyrics.Converters;

namespace Tubifarry.Metadata.Lyrics.Providers
{
    public partial class GeniusProvider(HttpClient httpClient, LidarrHttp.IHttpClient pageClient, Logger logger, LyricsEnhancerSettings settings)
    {
        public async Task<Lyric?> FetchLyricsAsync(string artistName, string trackTitle, CancellationToken token = default)
        {
            try
            {
                JToken? bestMatch = await SearchSongOnGeniusAsync(artistName, trackTitle, token);
                if (bestMatch == null)
                    return null;

                string? songPath = bestMatch["result"]?["path"]?.ToString();
                if (string.IsNullOrEmpty(songPath))
                {
                    logger.Warn("Could not find song path in Genius response");
                    return null;
                }

                string? plainLyrics = await ExtractLyricsFromGeniusPageAsync(songPath, token);
                if (string.IsNullOrWhiteSpace(plainLyrics))
                    return null;

                return new PlainTextConverter().Read(plainLyrics) is Lyric lyric
                    ? lyric with { Title = bestMatch["result"]?["title"]?.ToString(), Artist = bestMatch["result"]?["primary_artist"]?["name"]?.ToString() }
                    : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Error(ex, $"Error fetching lyrics from Genius for track: {trackTitle} by {artistName}");
                return null;
            }
        }

        private async Task<JToken?> SearchSongOnGeniusAsync(string artistName, string trackTitle, CancellationToken token)
        {
            string searchUrl = $"https://api.genius.com/search?q={Uri.EscapeDataString($"{artistName} {trackTitle}")}";
            logger.Debug($"Searching for track on Genius: {searchUrl}");

            using HttpRequestMessage request = new(HttpMethod.Get, searchUrl);
            request.Headers.Add("Authorization", $"Bearer {settings.GeniusApiKey}");

            using HttpResponseMessage response = await httpClient.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                logger.Warn($"Failed to search Genius. Status: {response.StatusCode}");
                return null;
            }

            string responseContent = await response.Content.ReadAsStringAsync(token);
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

            return LyricsHelper.SelectBestGeniusHit(songHits, artistName, trackTitle, logger);
        }

        private async Task<string?> ExtractLyricsFromGeniusPageAsync(string songPath, CancellationToken token)
        {
            string songUrl = $"https://genius.com{songPath}";
            logger.Trace($"Fetching lyrics from Genius page: {songUrl}");

            LidarrHttp.HttpRequest request = new(songUrl) { SuppressHttpError = true, LogHttpError = false };
            LidarrHttp.HttpResponse pageResponse = await pageClient.GetAsync(request).WaitAsync(token);

            if (pageResponse.HasHttpError)
            {
                logger.Warn($"Failed to fetch Genius lyrics page. Status: {pageResponse.StatusCode}");
                return null;
            }

            string html = pageResponse.Content;

            string? plainLyrics = ExtractLyricsFromHtml(html);

            if (string.IsNullOrWhiteSpace(plainLyrics))
            {
                logger.Debug("Extracted lyrics from Genius are empty");
                return null;
            }

            return plainLyrics;
        }

        internal string? ExtractLyricsFromHtml(string html)
        {
            List<string> lyricsContainers = ExtractDivs(html, DataLyricsContainerRegex());

            if (lyricsContainers.Count == 0)
            {
                logger.Debug("No matching lyrics pattern found in HTML");
                return null;
            }

            logger.Trace($"Found {lyricsContainers.Count} potential lyrics container(s). Processing...");

            List<string> validLyricsBlocks = new();

            foreach (string lyricsHtml in lyricsContainers)
            {
                string plainLyrics = BrTagRegex().Replace(RemoveDivs(lyricsHtml, ExcludedBlockRegex()), "\n");
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

        private static List<string> ExtractDivs(string html, Regex opening)
        {
            List<string> blocks = [];
            int position = 0;

            while (position < html.Length && opening.Match(html, position) is { Success: true } match)
            {
                int start = match.Index + match.Length;
                if (FindClosingDiv(html, start) is not Match close)
                    break;

                string inner = html[start..close.Index];
                if (!string.IsNullOrWhiteSpace(inner))
                    blocks.Add(inner);
                position = close.Index + close.Length;
            }

            return blocks;
        }

        private static string RemoveDivs(string html, Regex opening)
        {
            Match match = opening.Match(html);
            while (match.Success)
            {
                if (FindClosingDiv(html, match.Index + match.Length) is not Match close)
                    break;

                html = html.Remove(match.Index, close.Index + close.Length - match.Index);
                match = opening.Match(html, match.Index);
            }

            return html;
        }

        private static Match? FindClosingDiv(string html, int start)
        {
            int depth = 1;
            foreach (Match tag in DivTagRegex().Matches(html, start))
            {
                depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
                if (depth == 0)
                    return tag;
            }

            return null;
        }

        [GeneratedRegex(@"<div[^>]*data-lyrics-container[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled, "de-DE")]
        private static partial Regex DataLyricsContainerRegex();

        [GeneratedRegex(@"<div[^>]*data-exclude-from-selection[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled, "de-DE")]
        private static partial Regex ExcludedBlockRegex();

        [GeneratedRegex(@"<div\b[^>]*>|</div\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled, "de-DE")]
        private static partial Regex DivTagRegex();

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
