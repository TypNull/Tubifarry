using NLog;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tubifarry.Core.Records;
using Tubifarry.Core.Utilities;
using Tubifarry.Metadata.Lyrics.Converters;

namespace Tubifarry.Metadata.Lyrics.Providers
{

    public sealed class UnisonProvider
    {
        private readonly HttpClient _httpClient;
        private readonly Logger _logger;
        private readonly LyricsEnhancerSettings _settings;

        private static readonly TtmlConverter _ttmlConverter = new();
        private static readonly ElrcConverter _elrcConverter = new();
        private static readonly LrcConverter _lrcConverter = new();
        private static readonly PlainTextConverter _plainConverter = new();

        public UnisonProvider(HttpClient httpClient, Logger logger, LyricsEnhancerSettings settings)
        {
            _httpClient = httpClient;
            _logger = logger;
            _settings = settings;
        }

        public async Task<Lyric?> FetchLyricsAsync(string artistName, string trackTitle, string albumName, int duration, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(trackTitle))
                return null;

            try
            {
                StringBuilder query = new();
                query.Append("?song=").Append(Uri.EscapeDataString(trackTitle));
                query.Append("&artist=").Append(Uri.EscapeDataString(artistName));
                if (!string.IsNullOrEmpty(albumName))
                    query.Append("&album=").Append(Uri.EscapeDataString(albumName));
                if (duration > 0)
                    query.Append("&duration=").Append(duration);

                string requestUri = $"{_settings.UnisonUrl.TrimEnd('/')}/lyrics{query}";
                _logger.Trace($"Requesting lyrics from Unison: {requestUri}");

                HttpResponseMessage response = await _httpClient.GetAsync(requestUri, token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Debug($"No lyrics from Unison for {trackTitle} by {artistName}. Status: {response.StatusCode}");
                    return null;
                }

                string content = await response.Content.ReadAsStringAsync(token);
                return ParseResponse(content);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error fetching lyrics from Unison for track: {trackTitle} by {artistName}");
                return null;
            }
        }

        internal static Lyric? ParseResponse(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            UnisonResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<UnisonResponse>(content);
            }
            catch
            {
                return null;
            }

            if (response is not { Success: true, Data: { } data } || string.IsNullOrWhiteSpace(data.Lyrics))
                return null;

            string format = (data.Format ?? string.Empty).ToLowerInvariant();
            Lyric? result = format == "ttml"
                ? _ttmlConverter.Read(data.Lyrics)
                : _elrcConverter.Read(data.Lyrics) ?? _lrcConverter.Read(data.Lyrics) ?? _plainConverter.Read(data.Lyrics);
            if (result == null)
                return null;

            int duration = (int)Math.Round(data.Duration);
            return result with
            {
                Title = string.IsNullOrEmpty(data.Song) ? result.Title : data.Song,
                Artist = string.IsNullOrEmpty(data.Artist) ? result.Artist : data.Artist,
                Album = string.IsNullOrEmpty(data.Album) ? result.Album : data.Album,
                Duration = duration > 0 ? duration : result.Duration
            };
        }

        private sealed record UnisonResponse(
            [property: JsonPropertyName("success")] bool Success,
            [property: JsonPropertyName("data")] UnisonData? Data);

        private sealed record UnisonData(
            [property: JsonPropertyName("song")] string? Song,
            [property: JsonPropertyName("artist")] string? Artist,
            [property: JsonPropertyName("album")] string? Album,
            [property: JsonPropertyName("duration"), JsonConverter(typeof(FloatConverter))] float Duration,
            [property: JsonPropertyName("format")] string? Format,
            [property: JsonPropertyName("lyrics")] string? Lyrics);
    }
}
