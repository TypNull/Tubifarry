using Newtonsoft.Json.Linq;
using NLog;
using Tubifarry.Core.Records;
using Tubifarry.Metadata.Lyrics.Converters;

namespace Tubifarry.Metadata.Lyrics.Providers
{
    public class LrcLibProvider
    {
        private readonly HttpClient _httpClient;
        private readonly Logger _logger;
        private readonly LyricsEnhancerSettings _settings;

        private static readonly LyricsfileConverter _lyricsfileConverter = new();
        private static readonly LrcConverter _lrcConverter = new();
        private static readonly PlainTextConverter _plainConverter = new();

        public LrcLibProvider(HttpClient httpClient, Logger logger, LyricsEnhancerSettings settings)
        {
            _httpClient = httpClient;
            _logger = logger;
            _settings = settings;
        }

        public async Task<Lyric?> FetchLyricsAsync(string artistName, string trackTitle, string albumName, int duration, CancellationToken token = default)
        {
            try
            {
                string baseUrl = _settings.LrcLibInstanceUrl.TrimEnd('/');
                string requestUri = $"{baseUrl}/api/get?artist_name={Uri.EscapeDataString(artistName)}&track_name={Uri.EscapeDataString(trackTitle)}{(string.IsNullOrEmpty(albumName) ? "" : $"&album_name={Uri.EscapeDataString(albumName)}")}{(duration != 0 ? $"&duration={duration}" : "")}";

                _logger.Trace($"Requesting lyrics from LRCLIB: {requestUri}");

                using (HttpResponseMessage response = await _httpClient.GetAsync(requestUri, token))
                {
                    if (response.IsSuccessStatusCode)
                        return ParseResponse(await response.Content.ReadAsStringAsync(token));

                    _logger.Debug($"LRCLIB lookup for {trackTitle} by {artistName} returned {response.StatusCode}, searching instead");
                }

                return await SearchAsync(baseUrl, artistName, trackTitle, duration, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, $"Error fetching lyrics from LRCLIB for track: {trackTitle} by {artistName}");
                return null;
            }
        }

        private async Task<Lyric?> SearchAsync(string baseUrl, string artistName, string trackTitle, int duration, CancellationToken token)
        {
            string requestUri = $"{baseUrl}/api/search?artist_name={Uri.EscapeDataString(artistName)}&track_name={Uri.EscapeDataString(trackTitle)}";
            _logger.Trace($"Searching lyrics on LRCLIB: {requestUri}");

            using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Debug($"LRCLIB search for {trackTitle} by {artistName} returned {response.StatusCode}");
                return null;
            }

            JArray results;
            try
            {
                results = JArray.Parse(await response.Content.ReadAsStringAsync(token));
            }
            catch
            {
                return null;
            }

            return results.OfType<JObject>()
                .Select(ParseEntry)
                .OfType<Lyric>()
                .Where(lyric => LyricsHelper.IsMatch(lyric, artistName, trackTitle, duration))
                .OrderByDescending(lyric => lyric.HasLineSync)
                .ThenBy(lyric => duration > 0 && lyric.Duration > 0 ? Math.Abs(lyric.Duration - duration) : 0)
                .ThenByDescending(lyric => LyricsHelper.ExactTitleScore(lyric.Title ?? string.Empty, trackTitle))
                .FirstOrDefault();
        }

        private static Lyric? ParseResponse(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            try
            {
                return ParseEntry(JObject.Parse(content));
            }
            catch
            {
                return null;
            }
        }

        private static Lyric? ParseEntry(JObject json)
        {
            string lyricsfile = json["lyricsfile"]?.ToString() ?? string.Empty;
            string synced = json["syncedLyrics"]?.ToString() ?? string.Empty;
            string plain = json["plainLyrics"]?.ToString() ?? string.Empty;

            Lyric? result = _lyricsfileConverter.Read(lyricsfile)
                ?? _lrcConverter.Read(synced)
                ?? _plainConverter.Read(plain);

            if (result == null)
                return null;

            JToken? durationToken = json["duration"];
            int duration = durationToken?.Type is JTokenType.Integer or JTokenType.Float
                ? (int)Math.Round(durationToken.Value<double>())
                : result.Duration;

            return result with
            {
                Artist = json["artistName"]?.ToString() ?? result.Artist,
                Title = json["trackName"]?.ToString() ?? result.Title,
                Album = json["albumName"]?.ToString() ?? result.Album,
                Duration = duration
            };
        }
    }
}
