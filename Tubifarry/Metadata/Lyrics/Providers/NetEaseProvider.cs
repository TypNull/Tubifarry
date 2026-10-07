using NLog;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Tubifarry.Core.Records;
using Tubifarry.Metadata.Lyrics.Converters;

namespace Tubifarry.Metadata.Lyrics.Providers
{
    public sealed partial class NetEaseProvider
    {
        private const int SearchLimit = 10;
        private const string InstrumentalMarker = "纯音乐";

        private static readonly HttpClient _httpClient = new(new SocketsHttpHandler { UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });

        private readonly Logger _logger;
        private readonly LyricsEnhancerSettings _settings;

        private static readonly LrcConverter _lrcConverter = new();
        private static readonly PlainTextConverter _plainConverter = new();

        public NetEaseProvider(Logger logger, LyricsEnhancerSettings settings)
        {
            _logger = logger;
            _settings = settings;
        }

        public async Task<Lyric?> FetchLyricsAsync(string artistName, string trackTitle, int duration, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(trackTitle))
                return null;

            try
            {
                string baseUrl = _settings.NetEaseUrl.TrimEnd('/');
                string searchUri = $"{baseUrl}/api/search/get?type=1&limit={SearchLimit}&s={Uri.EscapeDataString($"{artistName} {trackTitle}")}";
                _logger.Trace($"Searching NetEase: {searchUri}");

                NetEaseSearchResponse? search = await GetAsync<NetEaseSearchResponse>(searchUri, token);
                NetEaseSong? song = SelectBestMatch(search?.Result?.Songs, artistName, trackTitle, duration);
                if (song == null)
                {
                    _logger.Debug($"No confident NetEase match for '{trackTitle}' by '{artistName}'");
                    return null;
                }

                string lyricUri = $"{baseUrl}/api/song/lyric?id={song.Id}&lv=1&kv=1&tv=-1";
                _logger.Trace($"Fetching NetEase lyrics: {lyricUri}");

                NetEaseLyricResponse? lyrics = await GetAsync<NetEaseLyricResponse>(lyricUri, token);
                if (lyrics == null || lyrics.NoLyric || lyrics.Uncollected)
                    return null;

                return BuildLyric(song, lyrics.Lrc?.Lyric);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, $"Error fetching lyrics from NetEase for track: {trackTitle} by {artistName}");
                return null;
            }
        }

        private async Task<T?> GetAsync<T>(string requestUri, CancellationToken token) where T : class
        {
            using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            request.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Debug($"NetEase request failed. Status: {response.StatusCode}");
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(token));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        internal static NetEaseSong? SelectBestMatch(List<NetEaseSong>? songs, string artistName, string trackTitle, int duration) =>
            songs?
                .Where(song => song.Id > 0 && LyricsHelper.IsMatch(ToLyric(song), artistName, trackTitle, duration))
                .OrderByDescending(song => LyricsHelper.ExactTitleScore(song.Name ?? string.Empty, trackTitle))
                .ThenByDescending(song => LyricsHelper.ArtistScore(ArtistNames(song), artistName))
                .ThenBy(song => duration > 0 ? Math.Abs(DurationSeconds(song) - duration) : 0)
                .FirstOrDefault();

        internal static Lyric? BuildLyric(NetEaseSong song, string? content)
        {
            if (string.IsNullOrWhiteSpace(content) || IsInstrumental(content))
                return null;

            Lyric? result = _lrcConverter.Read(content) ?? _plainConverter.Read(content);
            if (result == null)
                return null;

            List<LyricLine> lines = result.Lines.Where(line => !CreditLineRegex().IsMatch(line.Text)).ToList();
            if (!lines.Any(line => !string.IsNullOrWhiteSpace(line.Text)))
                return null;

            return result with { Lines = lines, Title = song.Name, Artist = ArtistNames(song), Album = song.Album?.Name, Duration = DurationSeconds(song) };
        }

        private static bool IsInstrumental(string content) =>
            content.Contains(InstrumentalMarker, StringComparison.Ordinal) && LrcTagRegex().Replace(content, string.Empty).Trim().Length <= 20;

        private static Lyric ToLyric(NetEaseSong song) => new() { Title = song.Name, Artist = ArtistNames(song), Duration = DurationSeconds(song) };

        private static string ArtistNames(NetEaseSong song) =>
            string.Join(", ", song.Artists?.Select(artist => artist.Name).Where(name => !string.IsNullOrWhiteSpace(name)) ?? []);

        private static int DurationSeconds(NetEaseSong song) => (int)Math.Round(song.Duration / 1000.0);

        [GeneratedRegex(@"^\s*(作词|作曲|编曲|制作人|监制|混音|母带|录音|和声|吉他|贝斯|鼓|弦乐|出品|发行|词|曲)\s*[:：]", RegexOptions.Compiled)]
        private static partial Regex CreditLineRegex();

        [GeneratedRegex(@"\[[^\]]*\]", RegexOptions.Compiled)]
        private static partial Regex LrcTagRegex();

        internal sealed record NetEaseSearchResponse(
            [property: JsonPropertyName("result")] NetEaseSearchResult? Result);

        internal sealed record NetEaseSearchResult(
            [property: JsonPropertyName("songs")] List<NetEaseSong>? Songs);

        internal sealed record NetEaseSong(
            [property: JsonPropertyName("id")] long Id,
            [property: JsonPropertyName("name")] string? Name,
            [property: JsonPropertyName("artists")] List<NetEaseArtist>? Artists,
            [property: JsonPropertyName("album")] NetEaseAlbum? Album,
            [property: JsonPropertyName("duration")] long Duration);

        internal sealed record NetEaseArtist(
            [property: JsonPropertyName("name")] string? Name);

        internal sealed record NetEaseAlbum(
            [property: JsonPropertyName("name")] string? Name);

        internal sealed record NetEaseLyricResponse(
            [property: JsonPropertyName("nolyric")] bool NoLyric,
            [property: JsonPropertyName("uncollected")] bool Uncollected,
            [property: JsonPropertyName("lrc")] NetEaseLyricText? Lrc);

        internal sealed record NetEaseLyricText(
            [property: JsonPropertyName("lyric")] string? Lyric);
    }
}
