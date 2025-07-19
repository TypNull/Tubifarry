using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using System.Text;
using System.Text.RegularExpressions;
using Tubifarry.Core.Records;

namespace Tubifarry.Metadata.Lyrics
{
    public class LyricsEnhancer : MetadataBase<LyricsEnhancerSettings>
    {
        private readonly Logger _logger;
        private readonly HttpClient _httpClient;

        public LyricsEnhancer(HttpClient httpClient, Logger logger)
        {
            _logger = logger;
            _httpClient = httpClient;
        }

        public override string Name => "Lyrics Enhancer";

        public override MetadataFile? FindMetadataFile(Artist artist, string path) => null;
        public override MetadataFileResult? ArtistMetadata(Artist artist) => null;
        public override MetadataFileResult? AlbumMetadata(Artist artist, Album album, string albumPath) => null;
        public override List<ImageFileResult> ArtistImages(Artist artist) => new();
        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumFolder) => new();
        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();

        public override string GetFilenameAfterMove(Artist artist, TrackFile trackFile, MetadataFile metadataFile)
        {
            string trackFilePath = trackFile.Path;

            if (metadataFile.Type == MetadataType.TrackMetadata)
                return Path.ChangeExtension(trackFilePath, ".lrc");

            _logger.Trace("Unknown track file metadata: {0}", metadataFile.RelativePath);
            return Path.Combine(artist.Path, metadataFile.RelativePath);
        }

        public override MetadataFileResult? TrackMetadata(Artist artist, TrackFile trackFile)
        {
            if (!ShouldProcessTrack(trackFile))
                return null;

            try
            {
                string? lrcContent = ProcessTrackLyricsAsync(artist, trackFile).Result;

                if (!string.IsNullOrEmpty(lrcContent))
                {
                    string relativePath = artist.Path.GetRelativePath(trackFile.Path);
                    relativePath = Path.ChangeExtension(relativePath, ".lrc");

                    return new MetadataFileResult(relativePath, lrcContent);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error processing lyrics for track: {trackFile.Path}");
                return null;
            }
        }

        private bool ShouldProcessTrack(TrackFile trackFile)
        {
            if (trackFile == null || string.IsNullOrEmpty(trackFile.Path))
                return false;

            if (!File.Exists(trackFile.Path))
                return false;

            string lrcPath = Path.ChangeExtension(trackFile.Path, ".lrc");
            if (File.Exists(lrcPath) && !Settings.OverwriteExistingLrcFiles)
                return false;
            return true;
        }

        private async Task<string?> ProcessTrackLyricsAsync(Artist artist, TrackFile trackFile)
        {
            _logger.Debug($"Processing lyrics for track: {trackFile.Path}");

            if (trackFile.Tracks?.Value == null || trackFile.Tracks.Value.Count == 0)
            {
                _logger.Warn($"No track information found for file: {trackFile.Path}");
                return null;
            }

            Track track = trackFile.Tracks.Value[0];

            Album album = track.Album;

            string trackTitle = track.Title;
            string artistName = artist.Name;
            string albumName = album.Title;
            int trackDuration = 0;

            if (track.Duration > 0)
                trackDuration = (int)Math.Round(TimeSpan.FromMilliseconds(track.Duration).TotalSeconds);

            Lyric? lyric = null;

            if (Settings.LrcLibEnabled)
                lyric = await FetchLyricsFromLRCLIBAsync(artistName, trackTitle, albumName, trackDuration);

            if (lyric == null && Settings.GeniusEnabled && !string.IsNullOrWhiteSpace(Settings.GeniusApiKey))
                lyric = await FetchLyricsFromGeniusAsync(artistName, trackTitle);

            if (lyric == null)
            {
                _logger.Trace($"No lyrics found for track: {trackTitle} by {artistName} from any source");
                return null;
            }

            if (lyric.SyncedLyrics != null && Settings.CreateLrcFiles)
            {
                string? lrcContent = CreateLrcContentAsync(lyric, artistName, trackTitle, albumName, trackDuration);
                if (Settings.EmbedLyricsInAudioFiles && !string.IsNullOrWhiteSpace(lyric.PlainLyrics))
                    EmbedLyricsInAudioFile(trackFile.Path, lyric.PlainLyrics);

                return lrcContent;
            }

            return null;
        }

        private string? CreateLrcContentAsync(Lyric lyric, string artistName, string trackTitle, string albumName, int duration)
        {
            if (lyric.SyncedLyrics == null || lyric.SyncedLyrics.Count == 0)
                return null;

            try
            {
                StringBuilder lrcContent = new();

                lrcContent.AppendLine($"[ar:{artistName}]");
                lrcContent.AppendLine($"[al:{albumName}]");
                lrcContent.AppendLine($"[ti:{trackTitle}]");

                if (duration > 0)
                {
                    TimeSpan ts = TimeSpan.FromSeconds(duration);
                    lrcContent.AppendLine($"[length:{ts.ToString(@"mm\:ss\.ff")}]");
                }

                lrcContent.AppendLine($"[by:Tubifarry Lyrics Enhancer]");
                lrcContent.AppendLine();

                foreach (SyncLine syncLine in lyric.SyncedLyrics.Where(l => l != null && !string.IsNullOrEmpty(l.LrcTimestamp) && !string.IsNullOrEmpty(l.Line)).OrderBy(l => double.TryParse(l.Milliseconds ?? "0", out double ms) ? ms : 0))
                    lrcContent.AppendLine($"{syncLine.LrcTimestamp} {syncLine.Line}");

                return lrcContent.ToString();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to create LRC content");
                return null;
            }
        }

        private async Task<Lyric?> FetchLyricsFromLRCLIBAsync(string artistName, string trackTitle, string albumName, int duration)
        {
            try
            {
                string requestUri = $"{Settings.LrcLibInstanceUrl}/api/get?artist_name={Uri.EscapeDataString(artistName)}&track_name={Uri.EscapeDataString(trackTitle)}&album_name={Uri.EscapeDataString(albumName)}{(duration != 0 ? $"&duration={duration}" : "")}";

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

                SyncLyric? syncedLyrics = SyncLine.ParseSyncedLyrics(syncedLyricsStr);

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

        private async Task<Lyric?> FetchLyricsFromGeniusAsync(string artistName, string trackTitle)
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

                string? plainLyrics = await ExtractLyricsFromGeniusPageAsync(songPath, bestMatch, artistName, trackTitle);
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
            _logger.Debug($"Sending API request to Genius");

            using HttpRequestMessage request = new(HttpMethod.Get, searchUrl);
            request.Headers.Add("Authorization", $"Bearer {Settings.GeniusApiKey}");

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

            return ScoreAndSelectBestMatch(artistMatches, songHits, artistName, trackTitle);
        }

        private JToken? ScoreAndSelectBestMatch(List<JToken> artistMatches, List<JToken> songHits, string artistName, string trackTitle)
        {
            JToken? bestMatch = null;
            int bestScore = 0;

            List<JToken> candidatesToScore = artistMatches.Count > 0 ? artistMatches : songHits;

            _logger.Trace("Beginning enhanced fuzzy matching process...");

            foreach (JToken hit in candidatesToScore)
            {
                string resultTitle = hit["result"]?["title"]?.ToString() ?? string.Empty;
                string resultArtist = hit["result"]?["primary_artist"]?["name"]?.ToString() ?? string.Empty;

                int tokenSetScore = FuzzySharp.Fuzz.TokenSetRatio(resultTitle, trackTitle);
                int tokenSortScore = FuzzySharp.Fuzz.TokenSortRatio(resultTitle, trackTitle);
                int partialRatio = FuzzySharp.Fuzz.PartialRatio(resultTitle, trackTitle);
                int weightedRatio = FuzzySharp.Fuzz.WeightedRatio(resultTitle, trackTitle);

                int titleScore = Math.Max(Math.Max(tokenSetScore, tokenSortScore), Math.Max(partialRatio, weightedRatio));

                int artistScore = artistMatches.Count > 0 ? 100 : FuzzySharp.Fuzz.WeightedRatio(resultArtist, artistName);

                int combinedScore = (titleScore * 3 + artistScore * 7) / 10;

                _logger.Debug($"Match candidate: '{resultTitle}' by '{resultArtist}' - " +
                             $"Title Score: {titleScore} (Token Set: {tokenSetScore}, Token Sort: {tokenSortScore}, " +
                             $"Partial: {partialRatio}, Weighted: {weightedRatio}), " +
                             $"Artist Score: {artistScore}, Combined: {combinedScore}");

                if (combinedScore > bestScore)
                {
                    bestScore = combinedScore;
                    bestMatch = hit;
                    _logger.Debug($"New best match found with score: {combinedScore}");
                }
            }
            if (bestMatch == null || bestScore < 70)
            {
                _logger.Warn($"Match score below threshold (70%). No lyrics will be selected for: '{trackTitle}' by '{artistName}'");
                return null;
            }

            return bestMatch;
        }

        private async Task<string?> ExtractLyricsFromGeniusPageAsync(string songPath, JToken bestMatch, string artistName, string trackTitle)
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
            Match match = DataLyricsContainerRegex.Match(html);

            if (!match.Success)
                match = ClassicLyricsClassRegex.Match(html);
            if (!match.Success)
                match = LyricsRootIdRegex.Match(html);

            if (match.Success)
            {
                _logger.Trace("Match found. Processing lyrics HTML...");
                string lyricsHtml = match.Groups[1].Value;

                string plainLyrics = BrTagRegex.Replace(lyricsHtml, "\n");
                plainLyrics = ItalicTagRegex.Replace(plainLyrics, "");
                plainLyrics = BoldTagRegex.Replace(plainLyrics, "");
                plainLyrics = AnchorTagRegex.Replace(plainLyrics, "");
                plainLyrics = AllHtmlTagsRegex.Replace(plainLyrics, "");
                plainLyrics = System.Web.HttpUtility.HtmlDecode(plainLyrics).Trim();

                int lineCount = plainLyrics.Split('\n').Length;

                return plainLyrics;
            }
            else
            {
                _logger.Debug("No matching lyrics pattern found in HTML");
                return null;
            }
        }

        private void EmbedLyricsInAudioFile(string filePath, string lyrics)
        {
            try
            {
                using (TagLib.File file = TagLib.File.Create(filePath))
                {
                    file.Tag.Lyrics = lyrics;
                    file.Save();
                }
                _logger.Trace($"Embedded lyrics in file: {filePath}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to embed lyrics in file: {filePath}");
            }
        }

        private static readonly Regex DataLyricsContainerRegex = new(@"<div[^>]*data-lyrics-container[^>]*>(.*?)<\/div>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ClassicLyricsClassRegex = new(@"<div[^>]*class=""[^""]*lyrics[^""]*""[^>]*>(.*?)<\/div>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LyricsRootIdRegex = new(@"<div[^>]*id=""lyrics-root[^""]*""[^>]*>(.*?)<\/div>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BrTagRegex = new(@"<br[^>]*>", RegexOptions.Compiled);
        private static readonly Regex ItalicTagRegex = new(@"</?i[^>]*>", RegexOptions.Compiled);
        private static readonly Regex BoldTagRegex = new(@"</?b[^>]*>", RegexOptions.Compiled);
        private static readonly Regex AnchorTagRegex = new(@"</?a[^>]*>", RegexOptions.Compiled);
        private static readonly Regex AllHtmlTagsRegex = new(@"<[^>]*>", RegexOptions.Compiled);
    }
}