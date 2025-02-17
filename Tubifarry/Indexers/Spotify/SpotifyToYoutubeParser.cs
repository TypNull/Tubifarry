using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using Requests;
using Requests.Options;
using System.Net;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;
using Tubifarry.Indexers.Youtube;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models.Search;
using YouTubeMusicAPI.Models.Streaming;

namespace Tubifarry.Indexers.Spotify
{
    public interface ISpotifyToYoutubeParser : IParseIndexerResponse
    {
        public void SetAuth(YoutubeIndexerSettings settings);
    }

    /// <summary>
    /// Parses Spotify responses and converts them to YouTube Music releases.
    /// </summary>
    public class SpotifyToYoutubeParser : ISpotifyToYoutubeParser
    {
        private YouTubeMusicClient _ytClient;

        private readonly Logger _logger;
        private string? _cookiePath;
        private string? _poToken;

        public SpotifyToYoutubeParser()
        {
            _logger = NzbDroneLogger.GetLogger(this);
            _ytClient = new YouTubeMusicClient();
        }

        public void SetAuth(YoutubeIndexerSettings settings)
        {
            if (settings.CookiePath == _cookiePath && settings.PoToken == _poToken)
                return;
            if (string.IsNullOrEmpty(settings.CookiePath) && string.IsNullOrEmpty(settings.PoToken))
                return;

            _cookiePath = settings.CookiePath;
            _poToken = settings.PoToken;
            Cookie[]? cookies = !string.IsNullOrEmpty(settings.CookiePath) ? CookieManager.ParseCookieFile(settings.CookiePath) : null;
            _ytClient = new YouTubeMusicClient(cookies: cookies, poToken: settings.PoToken);
        }

        /// <summary>
        /// Parses the Spotify response and converts it to a list of ReleaseInfo objects.
        /// </summary>
        /// <param name="indexerResponse">The response from the Spotify indexer.</param>
        /// <returns>A list of ReleaseInfo objects.</returns>
        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = new();
            _logger.Trace("Starting to parse Spotify response.");

            try
            {
                JsonDocument jsonDoc = JsonDocument.Parse(indexerResponse.Content);

                if (!TryGetAlbums(jsonDoc, out JsonElement albums))
                {
                    _logger.Error("Spotify response does not contain 'albums' property.");
                    return releases;
                }

                if (!TryGetItems(albums, out JsonElement items))
                {
                    _logger.Error("Spotify response does not contain 'items' property under 'albums'.");
                    return releases;
                }
                ProcessAlbums(items, releases);
                return releases.DistinctBy(x => x.DownloadUrl).OrderByDescending(o => o.PublishDate).ToArray();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"An error occurred while parsing the Spotify response. Response content: {indexerResponse.Content}");
            }
            return releases.DistinctBy(x => x.DownloadUrl).OrderByDescending(o => o.PublishDate).ToArray();

        }

        private static bool TryGetAlbums(JsonDocument jsonDoc, out JsonElement albums) => jsonDoc.RootElement.TryGetProperty("albums", out albums);

        private static bool TryGetItems(JsonElement albums, out JsonElement items) => albums.TryGetProperty("items", out items);

        private void ProcessAlbums(JsonElement items, List<ReleaseInfo> releases)
        {
            RequestContainer<OwnRequest> requestContainer = new();

            foreach (JsonElement album in items.EnumerateArray())
            {
                requestContainer.Add(new OwnRequest(async (token) =>
                {
                    try
                    {
                        AlbumData albumInfo = ExtractAlbumInfo(album);
                        albumInfo.ParseReleaseDate();

                        await AddYoutubeData(albumInfo);

                        if (albumInfo.Bitrate == 0)
                            _logger.Trace($"No YouTube Music URL found for album: {albumInfo.AlbumName} by {albumInfo.ArtistName}.");
                        else
                            releases.Add(albumInfo.ToReleaseInfo());

                        return true;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"An error occurred while processing an album: {ex.Message}. Album JSON: {album}");
                        return false;
                    }
                }, new RequestOptions<VoidStruct, VoidStruct>() { NumberOfAttempts = 1 }));
            }
            requestContainer.Task.Wait();
        }

        private static AlbumData ExtractAlbumInfo(JsonElement album) => new("Tubifarry")
        {
            AlbumId = album.TryGetProperty("id", out JsonElement idProp) ? idProp.GetString() ?? "UnknownAlbumId" : "UnknownAlbumId",
            AlbumName = album.TryGetProperty("name", out JsonElement nameProp) ? nameProp.GetString() ?? "UnknownAlbum" : "UnknownAlbum",
            ArtistName = album.TryGetProperty("artists", out JsonElement artistsProp) && artistsProp.GetArrayLength() > 0
                    ? artistsProp[0].GetProperty("name").GetString() ?? "UnknownArtist" : "UnknownArtist",
            InfoUrl = album.TryGetProperty("external_urls", out JsonElement externalUrlsProp)
                    ? externalUrlsProp.GetProperty("spotify").GetString() ?? string.Empty : string.Empty,
            ReleaseDate = album.TryGetProperty("release_date", out JsonElement releaseDateProp) ? releaseDateProp.GetString() ?? "0000-01-01" : "0000-01-01",
            ReleaseDatePrecision = album.TryGetProperty("release_date_precision", out JsonElement releaseDatePrecisionProp)
                    ? releaseDatePrecisionProp.GetString() ?? "day" : "day",
            TotalTracks = album.TryGetProperty("total_tracks", out JsonElement totalTracksProp) ? totalTracksProp.GetInt32() : 0,
            ExplicitContent = album.TryGetProperty("explicit", out JsonElement explicitProp) && explicitProp.GetBoolean(),
            CustomString = album.TryGetProperty("images", out JsonElement imagesProp) && imagesProp.GetArrayLength() > 0
                    ? imagesProp[0].GetProperty("url").GetString() ?? string.Empty : string.Empty,
            CoverResolution = album.TryGetProperty("images", out JsonElement imagesProp2) && imagesProp2.GetArrayLength() > 0
                    ? $"{imagesProp2[0].GetProperty("width").GetInt32()}x{imagesProp2[0].GetProperty("height").GetInt32()}" : "UnknownResolution"
        };


        private async Task AddYoutubeData(AlbumData albumData)
        {
            try
            {
                _logger.Debug($"Adding YouTube data for album: {albumData.AlbumName} by {albumData.ArtistName}.");

                string query = $"\"{albumData.AlbumName}\" \"{albumData.ArtistName}\"";
                IEnumerable<AlbumSearchResult> searchResults = await _ytClient.SearchAsync<AlbumSearchResult>(query, 10);

                if (searchResults == null || !searchResults.Any())
                {
                    _logger.Debug($"No search results found for album: {albumData.AlbumName}.");
                    return;
                }

                foreach (AlbumSearchResult result in searchResults)
                {
                    if (result == null) continue;

                    string browseId = await _ytClient.GetAlbumBrowseIdAsync(result.Id);
                    YouTubeMusicAPI.Models.Info.AlbumInfo album = await _ytClient.GetAlbumInfoAsync(browseId);

                    if (album?.Songs == null || !album.Songs.Any()) continue;

                    if (albumData.TotalTracks > 0 && Math.Abs(album.Songs.Length - albumData.TotalTracks) / (double)albumData.TotalTracks > 0.6) continue;

                    if (album.ReleaseYear != 0 && Math.Abs(album.ReleaseYear - albumData.ReleaseDateTime.Year) > 2) continue;

                    YouTubeMusicAPI.Models.Info.AlbumSongInfo? firstTrack = album.Songs.FirstOrDefault();
                    if (firstTrack?.Id == null) continue;

                    try
                    {
                        StreamingData streamingData = await _ytClient.GetStreamingDataAsync(firstTrack.Id);
                        AudioStreamInfo? highestAudioStreamInfo = streamingData.StreamInfo.OfType<AudioStreamInfo>().OrderByDescending(info => info.Bitrate).FirstOrDefault();

                        if (highestAudioStreamInfo != null)
                        {
                            albumData.Duration = (long)album.Duration.TotalSeconds;
                            albumData.Bitrate = AudioFormatHelper.RoundToStandardBitrate(highestAudioStreamInfo.Bitrate / 1000);
                            albumData.AlbumId = result.Id;
                            _logger.Debug($"Successfully added YouTube data for album: {albumData.AlbumName}.");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to process track {firstTrack.Name} in album {albumData.AlbumName}.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Unexpected error while adding YouTube data for album: {albumData.AlbumName}.");
            }
        }
    }
}