using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models;
using YouTubeMusicAPI.Models.Search;
using YouTubeMusicAPI.Models.Streaming;
using YouTubeMusicAPI.Types;

namespace Tubifarry.Indexers.Youtube
{

    public interface IYoutubeParser : IParseIndexerResponse
    {
        public void SetCookies(string path);
    }

    /// <summary>
    /// Parses Spotify responses and converts them to YouTube Music releases.
    /// </summary>
    public class YoutubeParser : IYoutubeParser
    {
        private YouTubeMusicClient _ytClient;

        private readonly Logger _logger;
        private string? _cookiePath;

        public YoutubeParser(Logger logger)
        {
            _logger = logger;
            _ytClient = new YouTubeMusicClient();
        }

        public void SetCookies(string path)
        {
            if (string.IsNullOrEmpty(path) || path == _cookiePath)
                return;
            _cookiePath = path;
            _ytClient = new(cookies: CookieManager.ParseCookieFile(path));
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = new();
            _logger.Trace("Starting to parse Spotify response.");

            try
            {
                IEnumerable<AlbumSearchResult> albums = ParseSearchResponse(JObject.Parse(indexerResponse.Content)).Where(searchResult => searchResult.Kind == YouTubeMusicItemKind.Albums).SelectMany(x => x.Items.Cast<AlbumSearchResult>());

                ProcessAlbumsAsync(albums, releases).Wait();
                return releases.DistinctBy(x => x.DownloadUrl).OrderByDescending(o => o.PublishDate).ToArray();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"An error occurred while parsing the Spotify response. Response content: {indexerResponse.Content}");
            }
            return releases.DistinctBy(x => x.DownloadUrl).OrderByDescending(o => o.PublishDate).ToArray();
        }
        IEnumerable<Shelf> ParseSearchResponse(JObject requestResponse)
        {
            List<Shelf> results = new();

            bool isContinued = requestResponse.ContainsKey("continuationContents");

            IEnumerable<JToken>? shelvesData = isContinued
                ? requestResponse.SelectToken("continuationContents")
                : requestResponse
                    .SelectToken("contents.tabbedSearchResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents")
                    ?.Where(token => token["musicShelfRenderer"] is not null)
                    ?.Select(token => token.First!);

            if (shelvesData is null || !shelvesData.Any())
            {
                _logger?.Warn($"Parsing search failed. Request response does not contain any shelves.");
                return new List<Shelf>();
            }

            foreach (JToken? shelfData in shelvesData)
            {
                JToken? shelfDataObject = shelfData.First;
                if (shelfDataObject is null)
                    continue;

                string? nextContinuationToken = shelfDataObject.SelectObjectOptional<string>("continuations[0].nextContinuationData.continuation");

                string? category = isContinued
                    ? requestResponse
                        .SelectToken("header.musicHeaderRenderer.header.chipCloudRenderer.chips")
                        ?.FirstOrDefault(token => token.SelectObjectOptional<bool>("chipCloudChipRenderer.isSelected"))
                        ?.SelectObjectOptional<string>("chipCloudChipRenderer.uniqueId")
                    : shelfDataObject.SelectObjectOptional<string>("title.runs[0].text");

                JToken[] shelfItems = shelfDataObject.SelectObjectOptional<JToken[]>("contents") ?? Array.Empty<JToken>();

                YouTubeMusicItemKind kind = category.ToShelfKind();
                Func<JToken, IYouTubeMusicItem>? getShelfItem = kind switch
                {
                    YouTubeMusicItemKind.Albums => GetAlbums,
                    _ => null
                };

                List<IYouTubeMusicItem> items = new();
                if (getShelfItem is not null)
                {
                    foreach (JToken shelfItem in shelfItems)
                    {
                        JToken? itemObject = shelfItem.First?.First;
                        if (itemObject is null)
                            continue;

                        items.Add(getShelfItem(itemObject));
                    }
                }

                Shelf shelf = new(nextContinuationToken, items.ToArray(), kind);
                results.Add(shelf);
            }

            return results;
        }

        public static AlbumSearchResult GetAlbums(JToken jsonToken)
        {
            YouTubeMusicItem[] artists = jsonToken.SelectArtists("flexColumns[1].musicResponsiveListItemFlexColumnRenderer.text.runs", 2, 1);
            int yearIndex = artists[0].Id is null ? 4 : artists.Length * 2 + 2;

            return new(
                name: jsonToken.SelectObject<string>("flexColumns[0].musicResponsiveListItemFlexColumnRenderer.text.runs[0].text"),
                id: jsonToken.SelectObject<string>("overlay.musicItemThumbnailOverlayRenderer.content.musicPlayButtonRenderer.playNavigationEndpoint.watchPlaylistEndpoint.playlistId"),
                artists: artists,
                releaseYear: jsonToken.SelectObject<int>($"flexColumns[1].musicResponsiveListItemFlexColumnRenderer.text.runs[{yearIndex}].text"),
                isSingle: jsonToken.SelectObject<string>("flexColumns[1].musicResponsiveListItemFlexColumnRenderer.text.runs[0].text") == "Single",
                isEp: jsonToken.SelectObject<string>("flexColumns[1].musicResponsiveListItemFlexColumnRenderer.text.runs[0].text") == "EP",
                radio: jsonToken.SelectRadio("menu.menuRenderer.items[1].menuNavigationItemRenderer.navigationEndpoint.watchPlaylistEndpoint.playlistId", null),
                thumbnails: jsonToken.SelectThumbnails()
            );
        }

        private async Task AddYoutubeData(AlbumData albumData)
        {
            try
            {
                string browseId = await _ytClient.GetAlbumBrowseIdAsync(albumData.AlbumId);
                YouTubeMusicAPI.Models.Info.AlbumInfo album = await _ytClient.GetAlbumInfoAsync(browseId);

                if (album?.Songs == null || !album.Songs.Any()) return;

                YouTubeMusicAPI.Models.Info.AlbumSongInfo? firstTrack = album.Songs.FirstOrDefault();
                if (firstTrack?.Id == null) return;

                try
                {
                    StreamingData streamingData = await _ytClient.GetStreamingDataAsync(firstTrack.Id);
                    AudioStreamInfo? highestAudioStreamInfo = streamingData.StreamInfo.OfType<AudioStreamInfo>().OrderByDescending(info => info.Bitrate).FirstOrDefault();

                    if (highestAudioStreamInfo != null)
                    {
                        albumData.Duration = (long)album.Duration.TotalSeconds;
                        albumData.Bitrate = AudioFormatHelper.RoundToStandardBitrate(highestAudioStreamInfo.Bitrate / 1000);
                        albumData.TotalTracks = album.SongCount;
                        albumData.ExplicitContent = album.Songs.Any(x => x.IsExplicit);
                        _logger.Debug($"Successfully added YouTube data for album: {albumData.AlbumName}.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to process track {firstTrack.Name} in album {albumData.AlbumName}.");
                }

            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Unexpected error while adding YouTube data for album: {albumData.AlbumName}.");
            }
        }

        private async Task ProcessAlbumsAsync(IEnumerable<AlbumSearchResult> searchResult, List<ReleaseInfo> releases)
        {
            int i = 0;
            foreach (AlbumSearchResult album in searchResult)
            {
                if (i >= 10)
                    break;
                try
                {
                    AlbumData albumInfo = ExtractAlbumInfo(album);
                    albumInfo.ParseReleaseDate();
                    await AddYoutubeData(albumInfo);

                    if (albumInfo.Bitrate == 0)
                        _logger.Trace($"No YouTube Music URL found for album: {albumInfo.AlbumName} by {albumInfo.ArtistName}.");
                    else
                        releases.Add(albumInfo.ToReleaseInfo());
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"An error occurred while processing an album: {ex.Message}. Album JSON: {album}");
                }
                i++;
            }
        }

        private static AlbumData ExtractAlbumInfo(AlbumSearchResult album) => new("Youtube")
        {
            AlbumId = album.Id,
            AlbumName = album.Name,
            ArtistName = album.Artists.FirstOrDefault()?.Name ?? "UnknownArtist",
            ReleaseDate = album.ReleaseYear.ToString() ?? "0000-01-01",
            ReleaseDatePrecision = "year",
            CustomString = album.Thumbnails.FirstOrDefault()?.Url ?? string.Empty,
            CoverResolution = (album.Thumbnails.FirstOrDefault() is Thumbnail thumbnail) ? $"{thumbnail.Width}x{thumbnail.Height}" : "UnknownResolution"
        };
    }
}
