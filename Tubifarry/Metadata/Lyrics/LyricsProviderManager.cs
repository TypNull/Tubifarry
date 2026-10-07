using NLog;
using LidarrHttp = NzbDrone.Common.Http;
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
        private readonly LidarrHttp.IHttpClient _pageClient;
        private readonly Logger _logger;
        private readonly LyricsEnhancerSettings _settings;

        private readonly Lazy<LrcLibProvider> _lrcLibProvider;
        private readonly Lazy<GeniusProvider> _geniusProvider;
        private readonly Lazy<BinimumProvider> _binimumProvider;
        private readonly Lazy<LyricsPlusProvider> _lyricsPlusProvider;
        private readonly Lazy<UnisonProvider> _unisonProvider;
        private readonly Lazy<NetEaseProvider> _netEaseProvider;

        public LyricsProviderManager(HttpClient httpClient, LidarrHttp.IHttpClient pageClient, Logger logger, LyricsEnhancerSettings settings)
        {
            _httpClient = httpClient;
            _pageClient = pageClient;
            _logger = logger;
            _settings = settings;

            _lrcLibProvider = new Lazy<LrcLibProvider>(() => new LrcLibProvider(_httpClient, _logger, _settings));
            _geniusProvider = new Lazy<GeniusProvider>(() => new GeniusProvider(_httpClient, _pageClient, _logger, _settings));
            _binimumProvider = new Lazy<BinimumProvider>(() => new BinimumProvider(_httpClient, _logger, _settings));
            _lyricsPlusProvider = new Lazy<LyricsPlusProvider>(() => new LyricsPlusProvider(_httpClient, _logger, _settings));
            _unisonProvider = new Lazy<UnisonProvider>(() => new UnisonProvider(_httpClient, _logger, _settings));
            _netEaseProvider = new Lazy<NetEaseProvider>(() => new NetEaseProvider(_logger, _settings));
        }

        public Task<Lyric?> FetchFromLrcLibAsync(string artistName, string trackTitle, string albumName, int duration, CancellationToken token)
            => _lrcLibProvider.Value.FetchLyricsAsync(artistName, trackTitle, albumName, duration, token);

        public Task<Lyric?> FetchFromGeniusAsync(string artistName, string trackTitle, CancellationToken token)
            => _geniusProvider.Value.FetchLyricsAsync(artistName, trackTitle, token);

        public Task<Lyric?> FetchFromBinimumAsync(string artistName, string trackTitle, string albumName, int duration, CancellationToken token)
            => _binimumProvider.Value.FetchLyricsAsync(artistName, trackTitle, albumName, duration, token);

        public Task<Lyric?> FetchFromLyricsPlusAsync(string artistName, string trackTitle, string albumName, int duration, CancellationToken token)
            => _lyricsPlusProvider.Value.FetchLyricsAsync(artistName, trackTitle, albumName, duration, token);

        public Task<Lyric?> FetchFromUnisonAsync(string artistName, string trackTitle, string albumName, int duration, CancellationToken token)
            => _unisonProvider.Value.FetchLyricsAsync(artistName, trackTitle, albumName, duration, token);

        public Task<Lyric?> FetchFromNetEaseAsync(string artistName, string trackTitle, int duration, CancellationToken token)
            => _netEaseProvider.Value.FetchLyricsAsync(artistName, trackTitle, duration, token);
    }
}
