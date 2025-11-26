using DownloadAssistant.Base;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Parser.Model;
using System.Text.RegularExpressions;

namespace Tubifarry.Core.Records
{
    public record Lyric(string? PlainLyrics, List<SyncLine>? SyncedLyrics)
    {
        public static async Task<Lyric?> FetchLyricsFromLRCLIBAsync(string instance, ReleaseInfo releaseInfo, string trackName, int duration = 0, CancellationToken token = default)
        {
            string requestUri = $"{instance}/api/get?artist_name={Uri.EscapeDataString(releaseInfo.Artist)}&track_name={Uri.EscapeDataString(trackName)}&album_name={Uri.EscapeDataString(releaseInfo.Album)}{(duration != 0 ? $"&duration={duration}" : "")}";
            HttpResponseMessage response = await HttpGet.HttpClient.GetAsync(requestUri, token);
            if (!response.IsSuccessStatusCode) return null;
            JObject json = JObject.Parse(await response.Content.ReadAsStringAsync(token));
            return new Lyric(json["plainLyrics"]?.ToString() ?? string.Empty, SyncLine.ParseSyncedLyrics(json["syncedLyrics"]?.ToString() ?? string.Empty));
        }
    }

    public partial record class SyncLine
    {
        [JsonProperty("lrc_timestamp")]
        public string? LrcTimestamp { get; init; }

        [JsonProperty("milliseconds")]
        public string? Milliseconds { get; init; }

        [JsonProperty("duration")]
        public string? Duration { get; init; }

        [JsonProperty("line")]
        public string? Line { get; init; }

        public static List<SyncLine> ParseSyncedLyrics(string syncedLyrics)
        {
            List<SyncLine> lyric = [];
            string[] array = syncedLyrics.Split(new char[1] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < array.Length; i++)
            {
                Match match = TagRegex().Match(array[i]);
                if (match.Success)
                {
                    string value = match.Groups[1].Value;
                    string line = match.Groups[2].Value.Trim();
                    double totalMilliseconds = TimeSpan.ParseExact(value, "mm\\:ss\\.ff", null).TotalMilliseconds;
                    lyric.Add(new SyncLine
                    {
                        LrcTimestamp = "[" + value + "]",
                        Line = line,
                        Milliseconds = totalMilliseconds.ToString()
                    });
                }
            }
            return lyric;
        }

        [GeneratedRegex("\\[(\\d{2}:\\d{2}\\.\\d{2})\\](.*)")]
        private static partial Regex TagRegex();
    }
}