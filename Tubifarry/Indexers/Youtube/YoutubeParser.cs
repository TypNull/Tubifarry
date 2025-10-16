using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Reflection;
using Tubifarry.Core.Model;
using Tubifarry.Core.Records;
using Tubifarry.Core.Utilities;
using Tubifarry.Download.Clients.YouTube;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models.Info;
using YouTubeMusicAPI.Models.Search;
using YouTubeMusicAPI.Models.Streaming;
using YouTubeMusicAPI.Pagination;

namespace Tubifarry.Indexers.YouTube
{
    /// <summary>
    /// Parses YouTube Music API responses and converts them to releases.
    /// </summary>
    internal class YouTubeParser : IParseIndexerResponse
    {
        private const int DEFAULT_BITRATE = 128;
        private readonly Logger _logger;
        private readonly YouTubeIndexer _youTubeIndexer;
        private YouTubeMusicClient? _youTubeClient;
        private SessionTokens? _sessionToken;


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

        public YouTubeParser(YouTubeIndexer indexer)
        {
            _youTubeIndexer = indexer;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = [];

            try
            {
                if (string.IsNullOrEmpty(indexerResponse.Content))
                {
                    _logger.Warn("Received empty response content");
                    return releases;
                }
                JObject jsonResponse = JObject.Parse(indexerResponse.Content);
                Page<SearchResult> searchPage = TryParseWithDelegate(jsonResponse) ?? new Page<SearchResult>([], null);

                _logger.Trace($"Parsed {searchPage.Items.Count} search results from YouTube Music API response");
                ProcessSearchResults(searchPage.Items, releases);
                _logger.Debug($"Successfully converted {releases.Count} results to releases");
                return [.. releases.DistinctBy(x => x.DownloadUrl).OrderByDescending(o => o.PublishDate)];
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
                    EnrichAlbumWithYouTubeDataAsync(albumData).Wait();
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
            try
            {
                UpdateClient();

                string browseId = await _youTubeClient!.GetAlbumBrowseIdAsync(albumData.AlbumId);
                AlbumInfo albumInfo = await _youTubeClient.GetAlbumInfoAsync(browseId);

                if (albumInfo?.Songs == null || albumInfo.Songs.Length == 0)
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
                        StreamingData streamingData = await _youTubeClient.GetStreamingDataAsync(firstTrack.Id);
                        AudioStreamInfo? highestQualityStream = streamingData.StreamInfo
                            .OfType<AudioStreamInfo>()
                            .OrderByDescending(info => info.Bitrate)
                            .FirstOrDefault();

                        if (highestQualityStream != null)
                            albumData.Bitrate = AudioFormatHelper.RoundToStandardBitrate(highestQualityStream.Bitrate / 1000);
                        else
                            albumData.Bitrate = DEFAULT_BITRATE;
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
        private void UpdateClient()
        {
            if (_sessionToken?.IsValid == true)
                return;
            _sessionToken = TrustedSessionHelper.GetTrustedSessionTokensAsync(_youTubeIndexer.Settings.TrustedSessionGeneratorUrl).Result;
            _youTubeClient = TrustedSessionHelper.CreateAuthenticatedClientAsync(_youTubeIndexer.Settings.TrustedSessionGeneratorUrl, _youTubeIndexer.Settings.CookiePath).Result;
        }

        private static AlbumData ExtractAlbumInfo(AlbumSearchResult album) => new("Youtube", nameof(YoutubeDownloadProtocol))
        {
            AlbumId = album.Id,
            InfoUrl = $"https://music.youtube.com/playlist?list={album.Id}",
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