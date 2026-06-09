using DownloadAssistant.Base;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Core.Parser.Model;
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

        public async Task<Lyric?> FetchLyricsAsync(string artistName, string trackTitle, string albumName, int duration)
        {
            try
            {
                string requestUri = $"{_settings.LrcLibInstanceUrl}/api/get?artist_name={Uri.EscapeDataString(artistName)}&track_name={Uri.EscapeDataString(trackTitle)}{(string.IsNullOrEmpty(albumName) ? "" : $"&album_name={Uri.EscapeDataString(albumName)}")}{(duration != 0 ? $"&duration={duration}" : "")}";

                _logger.Trace($"Requesting lyrics from LRCLIB: {requestUri}");

                HttpResponseMessage response = await _httpClient.GetAsync(requestUri);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        _logger.Debug($"No lyrics found on LRCLIB for track: {trackTitle} by {artistName}");
                    else
                        _logger.Debug($"Failed to fetch lyrics from LRCLIB. Status: {response.StatusCode}");
                    return null;
                }

                string content = await response.Content.ReadAsStringAsync();
                return ParseResponse(content);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error fetching lyrics from LRCLIB for track: {trackTitle} by {artistName}");
                return null;
            }
        }

        public static async Task<Lyric?> FetchFromLRCLIBAsync(string instance, ReleaseInfo releaseInfo, string trackName, int duration = 0, CancellationToken token = default)
        {
            string requestUri = $"{instance}/api/get?artist_name={Uri.EscapeDataString(releaseInfo.Artist)}&track_name={Uri.EscapeDataString(trackName)}&album_name={Uri.EscapeDataString(releaseInfo.Album)}{(duration != 0 ? $"&duration={duration}" : "")}";
            HttpResponseMessage response = await HttpGet.HttpClient.GetAsync(requestUri, token);
            if (!response.IsSuccessStatusCode) return null;
            string content = await response.Content.ReadAsStringAsync(token);
            return ParseResponse(content);
        }

        private static Lyric? ParseResponse(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            JObject json;
            try
            {
                json = JObject.Parse(content);
            }
            catch
            {
                return null;
            }

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
