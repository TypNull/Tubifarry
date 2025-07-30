using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Reflection;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models.Info;
using YouTubeMusicAPI.Models.Search;
using YouTubeMusicAPI.Models.Streaming;
using YouTubeMusicAPI.Pagination;

namespace Tubifarry.Indexers.YouTube
{
    public interface IYouTubeParser : IParseIndexerResponse
    {
        void SetAuth(YouTubeIndexerSettings settings);
    }

    /// <summary>
    /// Parses YouTube Music API responses and converts them to releases.
    /// </summary>
    public class YouTubeParser : IYouTubeParser
    {
        private const int DEFAULT_BITRATE = 128;
        private YouTubeMusicClient? _ytClient;
        private readonly Logger _logger;
        private YouTubeIndexerSettings? _currentSettings;

        private static readonly Lazy<Func<JObject, Page<SearchResult>?>?> _getPageDelegate = new(() =>
        {
            try
            {
                Assembly ytMusicAssembly = typeof(YouTubeMusicClient).Assembly;
                Type? searchParserType = ytMusicAssembly.GetType("YouTubeMusicAPI.Internal.Parsers.SearchParser");
                MethodInfo? getPageMethod = searchParserType?.GetMethod("GetPage", BindingFlags.Public | BindingFlags.Static);
                if (getPageMethod == null) return null;
                return (Func<JObject, Page<SearchResult>?>)Delegate.CreateDelegate(
                    typeof(Func<JObject, Page<SearchResult>?>), getPageMethod);
            }
            catch { return null; }
        });

        public YouTubeParser(Logger logger) => _logger = logger;

        /// <summary>
        /// Sets authentication for the YouTube Music client.
        /// </summary>
        /// <param name="settings">The settings containing authentication information.</param>
        public void SetAuth(YouTubeIndexerSettings settings)
        {
            if (SettingsEqual(_currentSettings, settings))
                return;

            _currentSettings = settings;

            if (settings.CookiePath == null && settings.PoToken == null &&
                settings.VisitorData == null && settings.TrustedSessionGeneratorUrl == null)
            {
                _ytClient = null;
                return;
            }

            try
            {
                _ytClient = TrustedSessionHelper.CreateAuthenticatedClientAsync(
                    settings.TrustedSessionGeneratorUrl,
                    settings.PoToken,
                    settings.VisitorData,
                    settings.CookiePath,
                    logger: _logger).Result;

                _logger.Debug("Successfully created authenticated YouTube Music client for additional metadata");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to create authenticated YouTube Music client, will parse without additional metadata");
                _ytClient = null;
            }
        }

        /// <summary>
        /// Compare if two settings objects have the same auth-related properties
        /// </summary>
        private static bool SettingsEqual(YouTubeIndexerSettings? settings1, YouTubeIndexerSettings? settings2)
        {
            if (settings1 == null || settings2 == null)
                return false;

            return settings1.CookiePath == settings2.CookiePath &&
                   settings1.PoToken == settings2.PoToken &&
                   settings1.VisitorData == settings2.VisitorData &&
                   settings1.TrustedSessionGeneratorUrl == settings2.TrustedSessionGeneratorUrl;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = new();

            try
            {
                if (string.IsNullOrEmpty(indexerResponse.Content))
                {
                    _logger.Warn("Received empty response content");
                    return releases;
                }
                JObject jsonResponse = JObject.Parse(indexerResponse.Content);
                Page<SearchResult> searchPage = TryParseWithDelegate(jsonResponse) ?? new Page<SearchResult>(new List<SearchResult>(), null);

                _logger.Trace($"Parsed {searchPage.Items.Count} search results from YouTube Music API response");
                ProcessSearchResults(searchPage.Items, releases);
                _logger.Debug($"Successfully converted {releases.Count} results to releases");
                return releases.DistinctBy(x => x.DownloadUrl).OrderByDescending(o => o.PublishDate).ToList();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"An error occurred while parsing YouTube Music API response. Response length: {indexerResponse.Content?.Length ?? 0}");
                return releases;
            }
        }

        /// <summary>
        /// Try to parse using cached delegate to access internal SearchParser - 50x faster than reflection
        /// </summary>
        private Page<SearchResult>? TryParseWithDelegate(JObject jsonResponse)
        {
            try
            {
                Func<JObject, Page<SearchResult>?>? delegateMethod = _getPageDelegate.Value;
                if (delegateMethod == null)
                {
                    _logger.Error("SearchParser.GetPage delegate not available");
                    return null;
                }
                Page<SearchResult>? result = delegateMethod(jsonResponse);
                if (result != null)
                {
                    _logger.Trace("Successfully parsed response using cached delegate");
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to parse response using delegate, falling back to manual parsing");
            }
            return null;
        }

        private void ProcessSearchResults(IReadOnlyList<SearchResult> searchResults, List<ReleaseInfo> releases)
        {
            foreach (SearchResult searchResult in searchResults)
            {
                if (searchResult is not AlbumSearchResult album)
                    continue;

                try
                {
                    AlbumData albumData = ExtractAlbumInfo(album);
                    albumData.ParseReleaseDate();

                    if (_ytClient != null)
                    {
                        EnrichAlbumWithYouTubeDataAsync(albumData).Wait();
                    }
                    else
                    {
                        albumData.Bitrate = DEFAULT_BITRATE;
                        albumData.Duration = albumData.TotalTracks * 180;
                    }

                    if (albumData.Bitrate > 0)
                    {
                        releases.Add(albumData.ToReleaseInfo());
                        _logger.Trace($"Added album: '{albumData.AlbumName}' by '{albumData.ArtistName}' (Bitrate: {albumData.Bitrate}kbps)");
                    }
                    else
                    {
                        _logger.Trace($"Skipped album (no bitrate): '{albumData.AlbumName}' by '{albumData.ArtistName}'");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to process album: '{album?.Name}' by '{album?.Artists?.FirstOrDefault()?.Name}'");
                }
            }
        }

        private async Task EnrichAlbumWithYouTubeDataAsync(AlbumData albumData)
        {
            if (_ytClient == null)
                return;

            try
            {
                string browseId = await _ytClient.GetAlbumBrowseIdAsync(albumData.AlbumId);
                AlbumInfo albumInfo = await _ytClient.GetAlbumInfoAsync(browseId);

                if (albumInfo?.Songs == null || !albumInfo.Songs.Any())
                {
                    _logger.Trace($"No songs found for album: '{albumData.AlbumName}'");
                    albumData.Bitrate = DEFAULT_BITRATE;
                    return;
                }

                albumData.Duration = (long)albumInfo.Duration.TotalSeconds;
                albumData.TotalTracks = albumInfo.SongCount;
                albumData.ExplicitContent = albumInfo.Songs.Any(x => x.IsExplicit);

                AlbumSong? firstTrack = albumInfo.Songs.FirstOrDefault(s => !string.IsNullOrEmpty(s.Id));
                if (firstTrack?.Id != null)
                {
                    try
                    {
                        StreamingData streamingData = await _ytClient.GetStreamingDataAsync(firstTrack.Id);
                        AudioStreamInfo? highestQualityStream = streamingData.StreamInfo
                            .OfType<AudioStreamInfo>()
                            .OrderByDescending(info => info.Bitrate)
                            .FirstOrDefault();

                        if (highestQualityStream != null)
                        {
                            albumData.Bitrate = AudioFormatHelper.RoundToStandardBitrate(highestQualityStream.Bitrate / 1000);
                            _logger.Trace($"Retrieved streaming info for album: '{albumData.AlbumName}' (Bitrate: {albumData.Bitrate}kbps)");
                        }
                        else
                        {
                            albumData.Bitrate = DEFAULT_BITRATE;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, $"Failed to get streaming data for track '{firstTrack.Name}' in album '{albumData.AlbumName}'");
                        albumData.Bitrate = DEFAULT_BITRATE;
                    }
                }
                else
                {
                    albumData.Bitrate = DEFAULT_BITRATE;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, $"Failed to enrich album data for: '{albumData.AlbumName}'");
                albumData.Bitrate = DEFAULT_BITRATE;
            }
        }

        private static AlbumData ExtractAlbumInfo(AlbumSearchResult album) => new("Youtube", nameof(YoutubeDownloadProtocol))
        {
            AlbumId = album.Id,
            AlbumName = album.Name,
            ArtistName = album.Artists.FirstOrDefault()?.Name ?? "Unknown Artist",
            ReleaseDate = album.ReleaseYear > 0 ? album.ReleaseYear.ToString() : "0000-01-01",
            ReleaseDatePrecision = "year",
            CustomString = album.Thumbnails.FirstOrDefault()?.Url ?? string.Empty,
            CoverResolution = album.Thumbnails.FirstOrDefault() is { } thumbnail
                    ? $"{thumbnail.Width}x{thumbnail.Height}"
                    : "Unknown Resolution"
        };
    }
}