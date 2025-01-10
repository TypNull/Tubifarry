using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Parser.Model;
using Requests;
using Requests.Options;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tubifarry.Core;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models.Search;
using YouTubeMusicAPI.Models.Streaming;

namespace NzbDrone.Core.Indexers.Spotify
{
    public interface ISpotifyToYoutubeParser : IParseIndexerResponse
    {
        public void SetCookies(string path);
    }

    /// <summary>
    /// Parses Spotify responses and converts them to YouTube Music releases.
    /// </summary>
    public class SpotifyToYoutubeParser : ISpotifyToYoutubeParser
    {
        private YouTubeMusicClient _ytClient;
        private static readonly int[] StandardBitrates = { 96, 128, 160, 192, 256, 320 };

        private readonly Logger _logger;
        private string? _cookiePath;

        public SpotifyToYoutubeParser()
        {
            _logger = NzbDroneLogger.GetLogger(this);
            _ytClient = new YouTubeMusicClient();
        }

        public void SetCookies(string path)
        {
            if (string.IsNullOrEmpty(path) || path == _cookiePath)
                return;
            _cookiePath = path;
            _ytClient = new(cookies: CookieManager.ParseCookieFile(path));
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
                return releases.OrderByDescending(o => o.PublishDate).ToArray();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"An error occurred while parsing the Spotify response. Response content: {indexerResponse.Content}");
            }
            return releases.OrderByDescending(o => o.PublishDate).ToArray();

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
                string query = $"\"{albumData.AlbumName}\" \"{albumData.ArtistName}\"";
                IEnumerable<AlbumSearchResult> searchResults = await _ytClient.SearchAsync<AlbumSearchResult>(query, 4);

                if (searchResults == null || !searchResults.Any()) return;

                foreach (AlbumSearchResult result in searchResults)
                {
                    if (result == null)
                        continue;

                    if (!IsRelevantResult(result, albumData)) continue;

                    string browsID = await _ytClient.GetAlbumBrowseIdAsync(result.Id);
                    YouTubeMusicAPI.Models.Info.AlbumInfo album = await _ytClient.GetAlbumInfoAsync(browsID);

                    if (album?.Songs == null || !album.Songs.Any()) continue;

                    if (albumData.TotalTracks > 0 && Math.Abs(album.Songs.Length - albumData.TotalTracks) / (double)albumData.TotalTracks > 0.6) continue;

                    if (album.ReleaseYear != 0 && Math.Abs(album.ReleaseYear - albumData.ReleaseDateTime.Year) > 1) continue;

                    YouTubeMusicAPI.Models.Info.AlbumSongInfo? firstTrack = album.Songs.FirstOrDefault();
                    if (firstTrack?.Id == null)
                        continue;

                    try
                    {
                        StreamingData streamingData = await _ytClient.GetStreamingDataAsync(firstTrack.Id);
                        AudioStreamInfo? highestAudioStreamInfo = streamingData.StreamInfo.OfType<AudioStreamInfo>().OrderByDescending(info => info.Bitrate).FirstOrDefault();

                        if (highestAudioStreamInfo != null)
                        {
                            albumData.Duration = (long)album.Duration.TotalSeconds;
                            albumData.Bitrate = RoundToStandardBitrate(highestAudioStreamInfo.Bitrate / 1000);
                            albumData.AlbumId = result.Id;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to process track stream {firstTrack.Name} in album {albumData.AlbumName}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error while adding Youtube data: {ex.Message}");
            }
        }

        private static bool IsRelevantResult(AlbumSearchResult result, AlbumData parameters)
        {
            string normalizedResultName = NormalizeTitle(result.Name);
            string normalizedParametersAlbumName = NormalizeTitle(parameters.AlbumName);

            bool isAlbumMatch = normalizedResultName.Contains(normalizedParametersAlbumName, StringComparison.OrdinalIgnoreCase);
            bool isArtistMatch = result.Artists.Any(x => x.Name.Contains(parameters.ArtistName, StringComparison.OrdinalIgnoreCase));

            return isAlbumMatch && isArtistMatch;
        }

        private static string NormalizeTitle(string title)
        {
            title = Regex.Replace(title, @"[\(\[].*?[\)\]]", "").Trim();
            title = Regex.Replace(title, @"\[\w+(_\w+)?\]", "").Trim();
            Match match = Regex.Match(title, @"^(?<artist>.+?)(?: - )(?<album>.+?)(?: - )(?<year>\d{4})");
            if (match.Success)
            {
                string artist = match.Groups["artist"].Value.Trim();
                string album = match.Groups["album"].Value.Trim();
                string year = match.Groups["year"].Value.Trim();
                title = $"{artist} - {album} - {year}";
            }
            return title;
        }

        private static int RoundToStandardBitrate(int bitrateKbps) => StandardBitrates.OrderBy(b => Math.Abs(b - bitrateKbps)).First();
    }
}