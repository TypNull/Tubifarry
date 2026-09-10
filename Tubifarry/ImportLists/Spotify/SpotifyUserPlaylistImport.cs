using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Models;
using Tubifarry.Core.Model;
using Tubifarry.ImportLists.ArrStack;
using Tubifarry.ImportLists.Spotify;

namespace NzbDrone.Core.ImportLists.Spotify
{
    public class SpotifyUserPlaylistImport : SpotifyImportListBase<SpotifyUserPlaylistImportSettings>, IPlaylistTrackSource
    {
        private const int BaseThrottleMilliseconds = 500;
        private const int MaxRetries = 5;
        private const int BaseRateLimitDelayMilliseconds = 1000;
        private const int MaxRateLimitDelayMilliseconds = 30000;
        private FileCache? _fileCache;

        private static readonly SemaphoreSlim _throttleSemaphore = new(1, 1);
        private static DateTime _lastRequestTime = DateTime.MinValue;

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
            List<SpotifyImportListItemInfo> result = [];
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

                    CachedPlaylistData? cachedData = _fileCache.GetAsync<CachedPlaylistData>(cacheKey).GetAwaiter().GetResult();
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

            List<SpotifyImportListItemInfo> playlistItems = [];
            ProcessPlaylistTracks(api, playlistTracks, playlistItems);

            CachedPlaylistData cachedDataToSave = new()
            {
                ImportListItems = playlistItems,
                Playlist = playlist
            };

            _fileCache!.SetAsync(cacheKey, cachedDataToSave, TimeSpan.FromDays(Settings.CacheRetentionDays)).GetAwaiter().GetResult();
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

        public List<PlaylistItem> FetchTrackLevelItems()
        {
            List<PlaylistItem> result = [];

            if (Settings.AccessToken.IsNullOrWhiteSpace())
            {
                _logger.Warn("Access token not configured.");
                return result;
            }

            try
            {
                using SpotifyWebAPI api = GetApi();
                PrivateProfile profile = _spotifyProxy.GetPrivateProfile(this, api);
                if (profile == null)
                {
                    _logger.Warn("Failed to fetch user profile.");
                    return result;
                }

                Paging<SimplePlaylist> playlistPage = GetUserPlaylistsWithRetry(api, profile.Id);
                if (playlistPage != null)
                    CollectTrackItems(api, playlistPage, result);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Error fetching track-level items from Spotify");
            }

            return result;
        }

        private void CollectTrackItems(SpotifyWebAPI api, Paging<SimplePlaylist> page, List<PlaylistItem> result)
        {
            if (page.Items == null) return;

            foreach (SimplePlaylist playlist in page.Items)
            {
                Paging<PlaylistTrack> tracks = GetPlaylistTracksWithRetry(api, playlist.Id);
                if (tracks != null)
                    AppendTrackItems(api, tracks, result, playlist.Name);
            }

            if (page.HasNextPage())
            {
                Paging<SimplePlaylist> next = GetNextPageWithRetry(api, page);
                if (next != null)
                    CollectTrackItems(api, next, result);
            }
        }

        private void AppendTrackItems(SpotifyWebAPI api, Paging<PlaylistTrack> tracks, List<PlaylistItem> result, string playlistName)
        {
            if (tracks.Items == null) return;

            foreach (PlaylistTrack pt in tracks.Items)
            {
                if (pt?.Track?.Album == null) continue;

                SimpleAlbum album = pt.Track.Album;
                string artistName = album.Artists?.FirstOrDefault()?.Name
                    ?? pt.Track.Artists?.FirstOrDefault()?.Name
                    ?? "";
                string trackTitle = pt.Track.Name ?? "";

                if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(trackTitle))
                    continue;

                result.Add(new PlaylistItem(
                    ArtistMusicBrainzId: "",
                    AlbumMusicBrainzId: null,
                    ArtistName: artistName,
                    AlbumTitle: album.Name,
                    TrackTitle: trackTitle,
                    PlaylistName: playlistName));
            }

            if (tracks.HasNextPage())
            {
                Paging<PlaylistTrack> next = GetNextPageWithRetry(api, tracks);
                if (next != null)
                    AppendTrackItems(api, next, result, playlistName);
            }
        }

        private class CachedPlaylistData
        {
            public List<SpotifyImportListItemInfo> ImportListItems { get; set; } = [];
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
                Task.Delay(delay).GetAwaiter().GetResult();
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
                Task.Delay(delay).GetAwaiter().GetResult();
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
                Task.Delay(delay).GetAwaiter().GetResult();
                return GetNextPageWithRetry(api, paging, retryCount + 1);
            }
        }

        private static void Throttle()
        {
            _throttleSemaphore.Wait();
            try
            {
                TimeSpan timeSinceLastRequest = DateTime.Now - _lastRequestTime;
                if (timeSinceLastRequest.TotalMilliseconds < BaseThrottleMilliseconds)
                {
                    int delayNeeded = BaseThrottleMilliseconds - (int)timeSinceLastRequest.TotalMilliseconds;
                    Task.Delay(delayNeeded).GetAwaiter().GetResult();
                }
                _lastRequestTime = DateTime.Now;
            }
            finally
            {
                _throttleSemaphore.Release();
            }
        }

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