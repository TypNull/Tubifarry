using NLog;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tubifarry.Core.Records;

namespace Tubifarry.Metadata.Lyrics.Providers
{
    public sealed class LyricsPlusProvider
    {
        private readonly HttpClient _httpClient;
        private readonly Logger _logger;
        private readonly LyricsEnhancerSettings _settings;

        public LyricsPlusProvider(HttpClient httpClient, Logger logger, LyricsEnhancerSettings settings)
        {
            _httpClient = httpClient;
            _logger = logger;
            _settings = settings;
        }

        public async Task<Lyric?> FetchLyricsAsync(string artistName, string trackTitle, string albumName, int duration, string? isrc = null, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(trackTitle))
                return null;

            try
            {
                StringBuilder query = new();
                query.Append("?title=").Append(Uri.EscapeDataString(trackTitle));
                query.Append("&artist=").Append(Uri.EscapeDataString(artistName));
                if (!string.IsNullOrEmpty(isrc))
                    query.Append("&isrc=").Append(Uri.EscapeDataString(isrc));
                if (!string.IsNullOrEmpty(albumName))
                    query.Append("&album=").Append(Uri.EscapeDataString(albumName));
                if (duration > 0)
                    query.Append("&duration=").Append(duration);

                string requestUri = $"{_settings.LyricsPlusUrl.TrimEnd('/')}/v2/lyrics/get{query}";
                _logger.Trace($"Requesting lyrics from LyricsPlus: {requestUri}");

                using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
                request.Headers.TryAddWithoutValidation("User-Agent", "Tubifarry Lyrics Enhancer");

                HttpResponseMessage response = await _httpClient.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Debug($"No lyrics from LyricsPlus for {trackTitle} by {artistName}. Status: {response.StatusCode}");
                    return null;
                }

                string content = await response.Content.ReadAsStringAsync(token);
                return ParseResponse(content);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error fetching lyrics from LyricsPlus for track: {trackTitle} by {artistName}");
                return null;
            }
        }

        internal static Lyric? ParseResponse(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            LyricsPlusResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<LyricsPlusResponse>(content);
            }
            catch
            {
                return null;
            }

            if (response?.Lyrics is not { Count: > 0 } rawLines)
                return null;

            List<LyricLine> lines = [];
            foreach (LyricsPlusLine raw in rawLines)
            {
                string text = raw.Text?.Trim() ?? string.Empty;
                long? start = raw.Time;
                long? end = raw.Time + raw.Duration;

                List<LyricLine>? words = BuildWords(raw.Syllabus);

                if (string.IsNullOrEmpty(text) && words != null)
                    text = string.Join(" ", words.Select(w => w.Text));

                if (string.IsNullOrEmpty(text) && words == null)
                    continue;

                lines.Add(new LyricLine { Text = text, StartMs = start, EndMs = end, Words = words });
            }

            if (lines.Count == 0)
                return null;

            string? title = string.IsNullOrEmpty(response.Metadata?.Title) ? null : response.Metadata!.Title;
            string? artist = response.Metadata?.SongWriters?.FirstOrDefault();
            int duration = ParseDurationSeconds(response.Metadata?.TotalDuration);

            return new Lyric { Lines = lines, Title = title, Artist = artist, Duration = duration };
        }

        private static List<LyricLine>? BuildWords(List<LyricsPlusSyllable>? syllabus)
        {
            if (syllabus is not { Count: > 0 })
                return null;

            List<LyricLine> words = [];
            StringBuilder wordText = new();
            long? wStart = null, wEnd = null;

            void Flush()
            {
                string w = wordText.ToString().Trim();
                if (w.Length > 0)
                    words.Add(new LyricLine { Text = w, StartMs = wStart, EndMs = wEnd });
                wordText.Clear();
                wStart = null;
                wEnd = null;
            }

            foreach (LyricsPlusSyllable s in syllabus)
            {
                string raw = s.Text ?? string.Empty;
                wStart ??= s.Time;
                wEnd = s.Time + s.Duration;
                wordText.Append(raw);
                if (raw.Length > 0 && char.IsWhiteSpace(raw[^1]))
                    Flush();
            }
            Flush();

            return words.Count > 0 ? words : null;
        }

        private static int ParseDurationSeconds(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            string[] parts = value.Trim().Split(':');
            try
            {
                double h = 0, m = 0, s;
                switch (parts.Length)
                {
                    case 1: s = double.Parse(parts[0], CultureInfo.InvariantCulture); break;
                    case 2: m = double.Parse(parts[0], CultureInfo.InvariantCulture); s = double.Parse(parts[1], CultureInfo.InvariantCulture); break;
                    case 3: h = double.Parse(parts[0], CultureInfo.InvariantCulture); m = double.Parse(parts[1], CultureInfo.InvariantCulture); s = double.Parse(parts[2], CultureInfo.InvariantCulture); break;
                    default: return 0;
                }
                return (int)Math.Round((h * 3600) + (m * 60) + s);
            }
            catch
            {
                return 0;
            }
        }

        private sealed record LyricsPlusResponse(
            [property: JsonPropertyName("lyrics")] List<LyricsPlusLine>? Lyrics,
            [property: JsonPropertyName("metadata")] LyricsPlusMetadata? Metadata);

        private sealed record LyricsPlusLine(
            [property: JsonPropertyName("time")] long Time,
            [property: JsonPropertyName("duration")] long Duration,
            [property: JsonPropertyName("text")] string? Text,
            [property: JsonPropertyName("syllabus")] List<LyricsPlusSyllable>? Syllabus);

        private sealed record LyricsPlusSyllable(
            [property: JsonPropertyName("text")] string? Text,
            [property: JsonPropertyName("time")] long Time,
            [property: JsonPropertyName("duration")] long Duration);

        private sealed record LyricsPlusMetadata(
            [property: JsonPropertyName("title")] string? Title,
            [property: JsonPropertyName("songWriters")] List<string>? SongWriters,
            [property: JsonPropertyName("totalDuration")] string? TotalDuration);
    }
}
