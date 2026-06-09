using NLog;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tubifarry.Core.Records;
using Tubifarry.Core.Utilities;
using Tubifarry.Metadata.Lyrics.Converters;

namespace Tubifarry.Metadata.Lyrics.Providers
{
    public sealed class BinimumProvider
    {
        private readonly HttpClient _httpClient;
        private readonly Logger _logger;
        private readonly LyricsEnhancerSettings _settings;

        private static readonly TtmlConverter _ttmlConverter = new();

        public BinimumProvider(HttpClient httpClient, Logger logger, LyricsEnhancerSettings settings)
        {
            _httpClient = httpClient;
            _logger = logger;
            _settings = settings;
        }

        public async Task<Lyric?> FetchLyricsAsync(string artistName, string trackTitle, string albumName, int duration, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(trackTitle))
                return null;

            try
            {
                string q = string.IsNullOrWhiteSpace(artistName) ? trackTitle : $"{artistName} {trackTitle}";
                string lookupUri = $"{_settings.BinimumApiUrl.TrimEnd('/')}/?q={Uri.EscapeDataString(q)}{(duration > 0 ? $"&duration={duration}" : string.Empty)}";
                _logger.Trace($"Searching Binimum: {lookupUri}");

                HttpResponseMessage response = await _httpClient.GetAsync(lookupUri, token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Debug($"No Binimum results for '{q}'. Status: {response.StatusCode}");
                    return null;
                }

                string content = await response.Content.ReadAsStringAsync(token);
                BinimumResult? match = SelectBestMatch(content, artistName, trackTitle, duration);
                if (match?.LyricsUrl is not string lyricsUrl || string.IsNullOrEmpty(lyricsUrl))
                {
                    _logger.Debug($"No confident Binimum match for '{trackTitle}' by '{artistName}'");
                    return null;
                }

                _logger.Trace($"Fetching Binimum TTML: {lyricsUrl}");
                HttpResponseMessage ttmlResponse = await _httpClient.GetAsync(lyricsUrl, token);
                if (!ttmlResponse.IsSuccessStatusCode)
                {
                    _logger.Debug($"Failed to fetch Binimum TTML. Status: {ttmlResponse.StatusCode}");
                    return null;
                }

                string ttmlContent = await ttmlResponse.Content.ReadAsStringAsync(token);
                return BuildLyric(match, ttmlContent);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error fetching lyrics from Binimum for track: {trackTitle} by {artistName}");
                return null;
            }
        }


        internal static BinimumResult? SelectBestMatch(string content, string artistName, string trackTitle, int duration)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            BinimumLookup? lookup;
            try { lookup = JsonSerializer.Deserialize<BinimumLookup>(content); } catch { return null; }
            if (lookup?.Results is not { Count: > 0 } results)
                return null;

            string qTitle = Normalize(trackTitle);
            string qArtist = Normalize(artistName);

            BinimumResult? best = null;
            int bestScore = int.MinValue;

            foreach (BinimumResult r in results)
            {
                if (string.IsNullOrEmpty(r.LyricsUrl))
                    continue;

                string rTitle = Normalize(r.TrackName);
                int score;
                if (rTitle == qTitle)
                    score = 100;
                else if (rTitle.Length > 0 && (rTitle.Contains(qTitle) || qTitle.Contains(rTitle)))
                    score = 40;
                else
                    continue;

                string rArtist = Normalize(r.ArtistName);
                if (rArtist == qArtist)
                    score += 50;
                else if (rArtist.Length > 0 && qArtist.Length > 0 && (rArtist.Contains(qArtist) || qArtist.Contains(rArtist)))
                    score += 20;

                int rDuration = (int)Math.Round(r.Duration);
                if (duration > 0 && rDuration > 0)
                    score += Math.Max(0, 20 - Math.Abs(rDuration - duration));

                if (score > bestScore)
                {
                    bestScore = score;
                    best = r;
                }
            }

            return best;
        }

        private static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return string.Join(' ', value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static Lyric? BuildLyric(BinimumResult meta, string ttmlContent)
        {
            Lyric? result = _ttmlConverter.Read(ttmlContent);
            if (result == null)
                return null;

            int duration = (int)Math.Round(meta.Duration);
            return result with
            {
                Artist = string.IsNullOrEmpty(meta.ArtistName) ? result.Artist : meta.ArtistName,
                Title = string.IsNullOrEmpty(meta.TrackName) ? result.Title : meta.TrackName,
                Album = string.IsNullOrEmpty(meta.AlbumName) ? result.Album : meta.AlbumName,
                Duration = duration > 0 ? duration : result.Duration
            };
        }

        internal sealed record BinimumLookup(
            [property: JsonPropertyName("results")] List<BinimumResult>? Results);

        internal sealed record BinimumResult(
            [property: JsonPropertyName("track_name")] string? TrackName,
            [property: JsonPropertyName("artist_name")] string? ArtistName,
            [property: JsonPropertyName("album_name")] string? AlbumName,
            [property: JsonPropertyName("duration"), JsonConverter(typeof(FloatConverter))] float Duration,
            [property: JsonPropertyName("lyricsUrl")] string? LyricsUrl);
    }
}
