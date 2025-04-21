using Jint;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Text.RegularExpressions;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Lucida
{
    public interface ILucidaParser : IParseIndexerResponse { }

    public class LucidaParser : ILucidaParser
    {
        private readonly Logger _logger;

        private static readonly Regex[] SearchDataPatterns =
        {
            new(@"data\s*=\s*(\[(?:[^\[\]]|\[(?:[^\[\]]|\[(?:[^\[\]]|\[[^\[\]]*\])*\])*\])*\]);",
                RegexOptions.Compiled | RegexOptions.Singleline),
            new(@"__INITIAL_DATA__\s*=\s*({.+?});",
                RegexOptions.Compiled | RegexOptions.Singleline)
        };

        public LucidaParser(Logger logger) => _logger = logger;

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = new();
            LucidaRequestData? requestData = GetRequestData(indexerResponse);

            if (requestData == null)
                return releases;

            try
            {
                (JArray? albumsArray, JArray? tracksArray) = ExtractSearchResults(indexerResponse.Content);
                ProcessResults(albumsArray, tracksArray, releases, requestData);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error parsing Lucida search response");
            }

            return releases;
        }

        private LucidaRequestData? GetRequestData(IndexerResponse indexerResponse)
        {
            try
            {
                return JsonConvert.DeserializeObject<LucidaRequestData>(
                    indexerResponse.Request.HttpRequest.ContentSummary ?? string.Empty);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to deserialize request data");
                return null;
            }
        }

        private (JArray? AlbumsArray, JArray? TracksArray) ExtractSearchResults(string html)
        {
            foreach (Regex pattern in SearchDataPatterns)
            {
                Match match = pattern.Match(html);
                if (!match.Success) continue;

                try
                {
                    string rawData = NormalizeJsonData(match.Groups[1].Value);
                    (JArray? AlbumsArray, JArray? TracksArray) results = ExtractResultsFromData(rawData);
                    if (results.AlbumsArray != null || results.TracksArray != null)
                        return results;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Error parsing search results");
                }
            }

            return (null, null);
        }

        private (JArray? AlbumsArray, JArray? TracksArray) ExtractResultsFromData(string rawData)
        {
            // Direct extraction
            JArray dataArray = JArray.Parse(rawData);
            foreach (JToken item in dataArray)
            {
                if (item["type"]?.ToString() != "data" ||
                    item["data"]?["results"]?["success"]?.Value<bool>() != true)
                    continue;

                JArray? albumsArray = item["data"]?["results"]?["results"]?["albums"] as JArray;
                JArray? tracksArray = item["data"]?["results"]?["results"]?["tracks"] as JArray;

                if ((albumsArray?.Count > 0) || (tracksArray?.Count > 0))
                    return (albumsArray, tracksArray);
            }

            // Jint fallback
            return ExtractWithJint(rawData);
        }

        private (JArray? AlbumsArray, JArray? TracksArray) ExtractWithJint(string jsData)
        {
            try
            {
                Engine engine = new();
                engine.Execute($@"
                    var data = {jsData};
                    
                    // Find the search results in the data array
                    var searchResults = null;
                    for (var i = 0; i < data.length; i++) {{
                        var item = data[i];
                        if (item.type === 'data' && item.data && item.data.results && item.data.results.success) {{
                            searchResults = item.data.results;
                            break;
                        }}
                    }}
                ");

                object? searchResults = engine.GetValue("searchResults").ToObject();

                if (searchResults == null)
                    return (null, null);
                JObject resultsObj = JObject.FromObject(new { results = searchResults });

                return (
                    resultsObj.SelectToken("results.results.albums") as JArray,
                    resultsObj.SelectToken("results.results.tracks") as JArray
                );
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in Jint extraction");
                return (null, null);
            }
        }

        private void ProcessResults(JArray? albumsArray, JArray? tracksArray, List<ReleaseInfo> releases, LucidaRequestData requestData)
        {
            (AudioFormat format, int bitrate, int bitDepth) = LucidaServiceHelper.GetServiceQuality(requestData.ServiceValue);

            if (albumsArray?.Count > 0)
                ProcessAlbums(albumsArray, releases, requestData, format, bitrate, bitDepth);
            if (tracksArray?.Count > 0 && (requestData.IsSingle || releases.Count == 0))
                ProcessTracks(tracksArray, releases, requestData, format, bitrate, bitDepth);
        }

        private void ProcessAlbums(JArray albumsArray, List<ReleaseInfo> releases, LucidaRequestData requestData, AudioFormat format, int bitrate, int bitDepth)
        {
            foreach (JToken album in albumsArray)
            {
                try
                {
                    AlbumData albumData = CreateAlbumData(album, requestData, format, bitrate, bitDepth);
                    releases.Add(albumData.ToReleaseInfo());
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Error processing album: {album["title"]}");
                }
            }
        }

        private void ProcessTracks(JArray tracksArray, List<ReleaseInfo> releases, LucidaRequestData requestData, AudioFormat format, int bitrate, int bitDepth)
        {
            foreach (JToken track in tracksArray)
            {
                try
                {
                    AlbumData albumData = CreateTrackData(track, requestData, format, bitrate, bitDepth);
                    releases.Add(albumData.ToReleaseInfo());
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Error processing track: {track["title"]}");
                }
            }
        }

        private AlbumData CreateAlbumData(JToken albumToken, LucidaRequestData requestData, AudioFormat format, int bitrate, int bitDepth)
        {
            (string coverUrl, string coverResolution) = ExtractCoverInfo(albumToken["coverArtwork"]);
            int trackCount = ExtractTrackCount(albumToken["trackCount"]);
            string artistName = ExtractArtistName(albumToken["artists"]);

            AlbumData albumData = new("Lucida", nameof(LucidaDownloadProtocol))
            {
                AlbumId = $"{requestData.ServiceValue}|{albumToken["id"]}|{albumToken["url"]}",
                AlbumName = albumToken["title"]?.ToString() ?? "Unknown Album",
                ArtistName = artistName,
                InfoUrl = $"{requestData.BaseUrl}/?url={Uri.EscapeDataString(albumToken["url"]?.ToString() ?? string.Empty)}",
                CustomString = coverUrl,
                CoverResolution = coverResolution,
                TotalTracks = trackCount,
                Codec = format,
                Bitrate = bitrate,
                BitDepth = bitDepth
            };

            ProcessReleaseDate(albumData, albumToken["releaseDate"]?.ToString());
            return albumData;
        }

        private AlbumData CreateTrackData(JToken trackToken, LucidaRequestData requestData, AudioFormat format, int bitrate, int bitDepth)
        {
            (string coverUrl, string coverResolution) = ExtractCoverInfo(trackToken["coverArtwork"]);
            string artistName = ExtractArtistName(trackToken["artists"]);
            JToken? albumToken = trackToken["album"];

            AlbumData albumData = new("Lucida", nameof(LucidaDownloadProtocol))
            {
                AlbumId = $"T:{requestData.ServiceValue}-{trackToken["id"]}-{trackToken["url"]}",
                AlbumName = albumToken?["title"]?.ToString() ?? trackToken["title"]?.ToString() ?? "Unknown Album",
                ArtistName = artistName,
                InfoUrl = $"{requestData.BaseUrl}/?url={Uri.EscapeDataString(trackToken["url"]?.ToString() ?? string.Empty)}",
                CustomString = coverUrl,
                CoverResolution = coverResolution,
                TotalTracks = 1,
                Codec = format,
                Bitrate = bitrate,
                BitDepth = bitDepth
            };

            ProcessReleaseDate(albumData,
                trackToken["releaseDate"]?.ToString() ??
                albumToken?["releaseDate"]?.ToString() ??
                DateTime.Now.Year.ToString());

            return albumData;
        }

        private static void ProcessReleaseDate(AlbumData albumData, string? releaseDate)
        {
            if (string.IsNullOrEmpty(releaseDate))
            {
                albumData.ReleaseDate = DateTime.Now.Year.ToString();
                albumData.ReleaseDatePrecision = "year";
            }
            else if (Regex.IsMatch(releaseDate, @"^\d{4}-\d{2}-\d{2}$"))
            {
                albumData.ReleaseDate = releaseDate;
                albumData.ReleaseDatePrecision = "day";
            }
            else if (Regex.IsMatch(releaseDate, @"^\d{4}$"))
            {
                albumData.ReleaseDate = releaseDate;
                albumData.ReleaseDatePrecision = "year";
            }
            else
            {
                Match yearMatch = Regex.Match(releaseDate, @"\b(\d{4})\b");
                albumData.ReleaseDate = yearMatch.Success
                    ? yearMatch.Groups[1].Value
                    : DateTime.Now.Year.ToString();
                albumData.ReleaseDatePrecision = "year";
            }

            albumData.ParseReleaseDate();
        }

        private static string ExtractArtistName(JToken? artistsToken) =>
            artistsToken is JArray artists && artists.Count > 0
                ? artists[0]["name"]?.ToString() ?? "Unknown Artist"
                : "Unknown Artist";

        private static (string CoverUrl, string Resolution) ExtractCoverInfo(JToken? artworkToken)
        {
            if (artworkToken is JArray artworks && artworks.Count > 0)
            {
                JToken firstArtwork = artworks[0];
                string url = firstArtwork["url"]?.ToString() ?? string.Empty;

                int width = firstArtwork["width"]?.Value<int>() ?? 0;
                int height = firstArtwork["height"]?.Value<int>() ?? 0;

                return width > 0 && height > 0
                    ? (url, $"{width}x{height}")
                    : (url, "UnknownResolution");
            }

            return (string.Empty, "UnknownResolution");
        }

        private static int ExtractTrackCount(JToken? trackCountToken)
        {
            if (trackCountToken == null)
                return 10;

            return trackCountToken.Type switch
            {
                JTokenType.Float => (int)Math.Round(trackCountToken.Value<double>()),
                JTokenType.Integer => trackCountToken.Value<int>(),
                _ => int.TryParse(trackCountToken.ToString(), out int count)
                    ? count
                    : 10
            };
        }

        private static string NormalizeJsonData(string jsObjectNotation)
        {
            jsObjectNotation = Regex.Replace(jsObjectNotation,
                @"([{,])\s*([a-zA-Z0-9_$]+)\s*:", "$1\"$2\":");
            jsObjectNotation = Regex.Replace(jsObjectNotation,
                @":\s*'([^']*)'", ":\"$1\"");
            jsObjectNotation = Regex.Replace(jsObjectNotation,
                @":\s*True\b", ":true");
            jsObjectNotation = Regex.Replace(jsObjectNotation,
                @":\s*False\b", ":false");

            return jsObjectNotation;
        }
    }
}