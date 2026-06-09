using NLog;
using Tubifarry.Core.Records;
using Tubifarry.Metadata.Lyrics.Providers;

namespace Tubifarry.Metadata.Lyrics
{
    /// <summary>
    /// Manages and delegates lyrics fetching across available providers.
    /// Providers are lazily instantiated on first use.
    /// </summary>
    public class LyricsProviderManager
    {
        private readonly HttpClient _httpClient;
        private readonly Logger _logger;
        private readonly LyricsEnhancerSettings _settings;

        private readonly Lazy<LrcLibProvider> _lrcLibProvider;
        private readonly Lazy<GeniusProvider> _geniusProvider;
        private readonly Lazy<BinimumProvider> _binimumProvider;
        private readonly Lazy<LyricsPlusProvider> _lyricsPlusProvider;
        private readonly Lazy<UnisonProvider> _unisonProvider;

        public LyricsProviderManager(HttpClient httpClient, Logger logger, LyricsEnhancerSettings settings)
        {
            _httpClient = httpClient;
            _logger = logger;
            _settings = settings;

            _lrcLibProvider = new Lazy<LrcLibProvider>(() => new LrcLibProvider(_httpClient, _logger, _settings));
            _geniusProvider = new Lazy<GeniusProvider>(() => new GeniusProvider(_httpClient, _logger, _settings));
            _binimumProvider = new Lazy<BinimumProvider>(() => new BinimumProvider(_httpClient, _logger, _settings));
            _lyricsPlusProvider = new Lazy<LyricsPlusProvider>(() => new LyricsPlusProvider(_httpClient, _logger, _settings));
            _unisonProvider = new Lazy<UnisonProvider>(() => new UnisonProvider(_httpClient, _logger, _settings));
        }

        public Task<Lyric?> FetchFromLrcLibAsync(string artistName, string trackTitle, string albumName, int duration)
            => _lrcLibProvider.Value.FetchLyricsAsync(artistName, trackTitle, albumName, duration);

        public Task<Lyric?> FetchFromGeniusAsync(string artistName, string trackTitle)
            => _geniusProvider.Value.FetchLyricsAsync(artistName, trackTitle);

        public Task<Lyric?> FetchFromBinimumAsync(string artistName, string trackTitle, string albumName, int duration)
            => _binimumProvider.Value.FetchLyricsAsync(artistName, trackTitle, albumName, duration);

        public Task<Lyric?> FetchFromLyricsPlusAsync(string artistName, string trackTitle, string albumName, int duration, string? isrc = null)
            => _lyricsPlusProvider.Value.FetchLyricsAsync(artistName, trackTitle, albumName, duration, isrc);

        public Task<Lyric?> FetchFromUnisonAsync(string artistName, string trackTitle, string albumName, int duration)
            => _unisonProvider.Value.FetchLyricsAsync(artistName, trackTitle, albumName, duration);
    }
}
