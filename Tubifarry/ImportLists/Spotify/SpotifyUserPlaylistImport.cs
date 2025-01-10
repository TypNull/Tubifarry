using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists.ArrStack;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Models;
using Tubifarry.Core;

namespace NzbDrone.Core.ImportLists.Spotify
{
    public class SpotifyUserPlaylistImport : SpotifyImportListBase<SpotifyUserPlaylistImportSettings>
    {
        private const int BaseThrottleMilliseconds = 500;
        private const int MaxRetries = 5;
        private const int BaseRateLimitDelayMilliseconds = 1000;
        private const int MaxRateLimitDelayMilliseconds = 30000;
        private FileCache? _fileCache;

        public SpotifyUserPlaylistImport(
            ISpotifyProxy spotifyProxy,
            IMetadataRequestBuilder requestBuilder,
            IImportListStatusService importListStatusService,
            IImportListRepository importListRepository,
            IConfigService configService,
            IParsingService parsingService,
            IHttpClient httpClient,
            Logger logger)
            : base(spotifyProxy, requestBuilder, importListStatusService, importListRepository, configService, parsingService, httpClient, logger)
        {
        }

        public override string Name => "Spotify Saved Playlists";

        public override ProviderMessage Message => new(
            "This import list will attempt to fetch all playlists saved by the authenticated Spotify user. " +
            "Please note that this process may take some time depending on the number of playlists and tracks. " +
            "If the access token is not configured or has expired, the import will fail. " +
            "Additionally, large playlists or frequent refreshes may impact performance or hit API rate limits. ",
            ProviderMessageType.Warning);

        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours((Definition?.Settings as ArrSoundtrackImportSettings)?.RefreshInterval ?? 12);

        public override IList<SpotifyImportListItemInfo> Fetch(SpotifyWebAPI api)
        {
            List<SpotifyImportListItemInfo> result = new();
            if (!string.IsNullOrWhiteSpace(Settings.CacheDirectory))
                _fileCache ??= new FileCache(Settings.CacheDirectory);

            if (Settings.AccessToken.IsNullOrWhiteSpace())
            {
                _logger.Warn("Access token is not configured.");
                return result;
            }

            try
            {
                PrivateProfile profile = _spotifyProxy.GetPrivateProfile(this, api);
                if (profile == null)
                {
                    _logger.Warn("Failed to fetch user profile from Spotify.");
                    return result;
                }

                Paging<SimplePlaylist> playlistPage = GetUserPlaylistsWithRetry(api, profile.Id);
                if (playlistPage == null)
                {
                    _logger.Warn("Failed to fetch playlists from Spotify.");
                    return result;
                }

                _logger.Trace($"Fetched {playlistPage.Total} playlists for user {profile.DisplayName}");

                ProcessPlaylists(api, playlistPage, result, profile);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Error fetching playlists or tracks from Spotify");
            }

            return result;
        }

        private void ProcessPlaylists(SpotifyWebAPI api, Paging<SimplePlaylist> playlistPage, List<SpotifyImportListItemInfo> result, PrivateProfile profile)
        {
            if (playlistPage == null || playlistPage.Items == null)
                return;

            string username = profile?.Id ?? "unknown_user";

            foreach (SimplePlaylist playlist in playlistPage.Items)
            {
                _logger.Trace($"Processing playlist {playlist.Name} (ID: {playlist.Id})");

                if (_fileCache == null)
                {
                    ProcessPlaylistTracks(api, GetPlaylistTracksWithRetry(api, playlist.Id), result);
                    continue;
                }

                string cacheKey = GenerateCacheKey(playlist.Id, username);

                if (_fileCache.IsCacheValid(cacheKey, TimeSpan.FromDays(Settings.CacheRetentionDays)))
                {
                    if (Settings.SkipCachedPlaylists)
                    {
                        _logger.Trace($"Skipping cached playlist {playlist.Name} (ID: {playlist.Id})");
                        continue;
                    }

                    CachedPlaylistData? cachedData = _fileCache.GetAsync<CachedPlaylistData>(cacheKey).Result;
                    if (cachedData != null)
                    {
                        result.AddRange(cachedData.ImportListItems);
                        continue;
                    }
                }

                ProcessPlaylistWithCache(api, playlist, result, cacheKey);
            }

            if (playlistPage.HasNextPage())
            {
                Paging<SimplePlaylist> nextPage = GetNextPageWithRetry(api, playlistPage);
                if (nextPage != null)
                    ProcessPlaylists(api, nextPage, result, profile!);
            }
        }

        private void ProcessPlaylistWithCache(SpotifyWebAPI api, SimplePlaylist playlist, List<SpotifyImportListItemInfo> result, string cacheKey)
        {
            Paging<PlaylistTrack> playlistTracks = GetPlaylistTracksWithRetry(api, playlist.Id);
            if (playlistTracks == null)
                return;

            List<SpotifyImportListItemInfo> playlistItems = new();
            ProcessPlaylistTracks(api, playlistTracks, playlistItems);

            CachedPlaylistData cachedDataToSave = new()
            {
                ImportListItems = playlistItems,
                Playlist = playlist
            };

            _fileCache!.SetAsync(cacheKey, cachedDataToSave, TimeSpan.FromDays(Settings.CacheRetentionDays)).Wait();
            result.AddRange(playlistItems);
        }

        private void ProcessPlaylistTracks(SpotifyWebAPI api, Paging<PlaylistTrack> playlistTracks, List<SpotifyImportListItemInfo> result)
        {
            if (playlistTracks?.Items == null)
                return;

            foreach (PlaylistTrack playlistTrack in playlistTracks.Items)
                result!.AddIfNotNull(ParsePlaylistTrack(playlistTrack));

            if (playlistTracks.HasNextPage())
            {
                Paging<PlaylistTrack> nextPage = GetNextPageWithRetry(api, playlistTracks);
                if (nextPage != null)
                    ProcessPlaylistTracks(api, nextPage, result);
            }
        }

        private class CachedPlaylistData
        {
            public List<SpotifyImportListItemInfo> ImportListItems { get; set; } = new();
            public SimplePlaylist? Playlist { get; set; }
        }

        private static string GenerateCacheKey(string playlistId, string username)
        {
            HashCode hash = new();
            hash.Add(playlistId);
            hash.Add(username);
            return hash.ToHashCode().ToString("x8");
        }

        private Paging<SimplePlaylist> GetUserPlaylistsWithRetry(SpotifyWebAPI api, string userId, int retryCount = 0)
        {
            try
            {
                Throttle();
                return _spotifyProxy.GetUserPlaylists(this, api, userId);
            }
            catch (SpotifyException ex) when (ex.Message.Contains("[429] API rate limit exceeded"))
            {
                if (retryCount >= MaxRetries)
                {
                    _logger.Error("Maximum retry attempts reached for fetching user playlists.");
                    throw;
                }

                int delay = CalculateRateLimitDelay(retryCount);
                _logger.Warn($"Rate limit exceeded. Retrying in {delay} milliseconds.");
                Task.Delay(delay).Wait();
                return GetUserPlaylistsWithRetry(api, userId, retryCount + 1);
            }
        }

        private Paging<PlaylistTrack> GetPlaylistTracksWithRetry(SpotifyWebAPI api, string playlistId, int retryCount = 0)
        {
            try
            {
                Throttle();
                return _spotifyProxy.GetPlaylistTracks(this, api, playlistId, "next, items(track(name, artists(id, name), album(id, name, release_date, release_date_precision, artists(id, name))))");
            }
            catch (SpotifyException ex) when (ex.Message.Contains("[429] API rate limit exceeded"))
            {
                if (retryCount >= MaxRetries)
                {
                    _logger.Error("Maximum retry attempts reached for fetching playlist tracks.");
                    throw;
                }

                int delay = CalculateRateLimitDelay(retryCount);
                _logger.Trace($"Rate limit exceeded. Retrying in {delay} milliseconds.");
                Task.Delay(delay).Wait();
                return GetPlaylistTracksWithRetry(api, playlistId, retryCount + 1);
            }
        }

        private Paging<T> GetNextPageWithRetry<T>(SpotifyWebAPI api, Paging<T> paging, int retryCount = 0)
        {
            try
            {
                Throttle();
                return _spotifyProxy.GetNextPage(this, api, paging);
            }
            catch (SpotifyException ex) when (ex.Message.Contains("[429] API rate limit exceeded"))
            {
                if (retryCount >= MaxRetries)
                {
                    _logger.Error("Maximum retry attempts reached for fetching the next page.");
                    throw;
                }

                int delay = CalculateRateLimitDelay(retryCount);
                _logger.Trace($"Rate limit exceeded. Retrying in {delay} milliseconds.");
                Task.Delay(delay).Wait();
                return GetNextPageWithRetry(api, paging, retryCount + 1);
            }
        }

        private static void Throttle() => Task.Delay(BaseThrottleMilliseconds).Wait();

        private static int CalculateRateLimitDelay(int retryCount)
        {
            int delay = (int)(BaseRateLimitDelayMilliseconds * Math.Pow(2, retryCount));
            delay = Math.Min(delay, MaxRateLimitDelayMilliseconds);
            delay = new Random().Next(delay / 2, delay);
            return delay;
        }

        private SpotifyImportListItemInfo? ParsePlaylistTrack(PlaylistTrack playlistTrack)
        {
            if (playlistTrack?.Track?.Album != null)
            {
                SimpleAlbum album = playlistTrack.Track.Album;

                string albumName = album.Name;
                string? artistName = album.Artists?.FirstOrDefault()?.Name ?? playlistTrack.Track?.Artists?.FirstOrDefault()?.Name;

                if (albumName.IsNotNullOrWhiteSpace() && artistName.IsNotNullOrWhiteSpace())
                {
                    return new SpotifyImportListItemInfo
                    {
                        Artist = artistName,
                        Album = album.Name,
                        AlbumSpotifyId = album.Id,
                        ReleaseDate = ParseSpotifyDate(album.ReleaseDate, album.ReleaseDatePrecision)
                    };
                }
            }
            return null;
        }
    }
}