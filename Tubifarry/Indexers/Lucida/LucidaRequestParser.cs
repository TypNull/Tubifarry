using Jint;
using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;
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
            new(@"data\s*=\s*(\[(?:[^\[\]]|\[(?:[^\[\]]|\[(?:[^\[\]]|\[[^\[\]]*\])*\])*\])*\]);", RegexOptions.Compiled | RegexOptions.Singleline),
            new(@"__INITIAL_DATA__\s*=\s*({.+?});", RegexOptions.Compiled | RegexOptions.Singleline)
        };

        public LucidaParser(Logger logger) => _logger = logger;

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = new();
            LucidaRequestData? requestData = GetRequestData(indexerResponse);
            if (requestData == null) return releases;

            try
            {
                (List<LucidaAlbum>? albums, List<LucidaTrack>? tracks) = ExtractSearchResults(indexerResponse.Content);
                ProcessResults(albums, tracks, releases, requestData);
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
                return JsonSerializer.Deserialize<LucidaRequestData>(indexerResponse.Request.HttpRequest.ContentSummary ?? string.Empty);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to deserialize request data");
                return null;
            }
        }

        private (List<LucidaAlbum>? Albums, List<LucidaTrack>? Tracks) ExtractSearchResults(string html)
        {
            foreach (Regex pattern in SearchDataPatterns)
            {
                Match match = pattern.Match(html);
                if (!match.Success) continue;

                try
                {
                    string raw = NormalizeJsonData(match.Groups[1].Value);
                    List<LucidaDataWrapper>? wrapperList = JsonSerializer.Deserialize<List<LucidaDataWrapper>>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (wrapperList != null)
                    {
                        LucidaDataWrapper? dataWrapper = wrapperList
                            .FirstOrDefault(w => w.Type == "data" && w.Data?.Results?.Success == true);

                        if (dataWrapper?.Data?.Results?.Results != null)
                        {
                            LucidaResultsData results = dataWrapper.Data.Results.Results;
                            return (results.Albums, results.Tracks);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Typed deserialization failed, trying Jint fallback");
                    try
                    {
                        return ExtractWithJintToRecords(match.Groups[1].Value);
                    }
                    catch (Exception jintEx)
                    {
                        _logger.Error(jintEx, "Jint extraction failed");
                    }
                }
            }
            return (null, null);
        }

        private static (List<LucidaAlbum>? Albums, List<LucidaTrack>? Tracks) ExtractWithJintToRecords(string jsData)
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

                    // Extract separate arrays for albums and tracks
                    var albums = searchResults && searchResults.results && searchResults.results.albums 
                        ? searchResults.results.albums : [];
                    var tracks = searchResults && searchResults.results && searchResults.results.tracks 
                        ? searchResults.results.tracks : [];
                ");

            object? albumsObj = engine.GetValue("albums").ToObject();
            object? tracksObj = engine.GetValue("tracks").ToObject();

            string albumsJson = JsonSerializer.Serialize(albumsObj);
            string tracksJson = JsonSerializer.Serialize(tracksObj);

            List<LucidaAlbum>? albums = null;
            List<LucidaTrack>? tracks = null;

            if (!string.IsNullOrEmpty(albumsJson) && albumsJson != "[]")
                albums = JsonSerializer.Deserialize<List<LucidaAlbum>>(albumsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (!string.IsNullOrEmpty(tracksJson) && tracksJson != "[]")
                tracks = JsonSerializer.Deserialize<List<LucidaTrack>>(tracksJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return (albums, tracks);
        }

        private void ProcessResults(List<LucidaAlbum>? albums, List<LucidaTrack>? tracks, List<ReleaseInfo> releases, LucidaRequestData requestData)
        {
            (AudioFormat format, int bitrate, int bitDepth) = LucidaServiceHelper.GetServiceQuality(requestData.ServiceValue);

            if (albums?.Count > 0)
            {
                foreach (LucidaAlbum alb in albums)
                    TryAdd(() => CreateAlbumData(alb, requestData, format, bitrate, bitDepth), releases, alb.Title);
            }

            if (tracks != null && requestData.IsSingle && tracks.Count > 0)
            {
                foreach (LucidaTrack trk in tracks)
                    TryAdd(() => CreateTrackData(trk, requestData, format, bitrate, bitDepth), releases, trk.Title);
            }
        }

        private void TryAdd(Func<AlbumData> factory, List<ReleaseInfo> list, string title)
        {
            try
            {
                list.Add(factory().ToReleaseInfo());
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error processing item: {title}");
            }
        }

        private AlbumData CreateAlbumData(LucidaAlbum album, LucidaRequestData rd, AudioFormat format, int bitrate, int bitDepth)
        {
            List<LucidaArtist> artists = album.Artists ?? new List<LucidaArtist>();

            string artist = artists.FirstOrDefault()?.Name ?? "Unknown Artist";

            AlbumData data = new("Lucida", nameof(LucidaDownloadProtocol))
            {
                AlbumId = album.Url,
                AlbumName = album.Title,
                ArtistName = artist,
                InfoUrl = $"{rd.BaseUrl}/?url={album.Url}",
                TotalTracks = album.TrackCount == 0 ? 10 : (int)album.TrackCount,
                CustomString = "album",
                Codec = format,
                Bitrate = bitrate,
                BitDepth = bitDepth
            };

            ProcessReleaseDate(data, album.ReleaseDate);
            return data;
        }

        private AlbumData CreateTrackData(LucidaTrack track, LucidaRequestData rd, AudioFormat format, int bitrate, int bitDepth)
        {
            List<LucidaArtist> artists = track.Artists ?? new List<LucidaArtist>();
            string artist = artists.FirstOrDefault()?.Name ?? "Unknown Artist";
            string resolution = string.Empty;

            AlbumData data = new("Lucida", nameof(LucidaDownloadProtocol))
            {
                AlbumId = track.Url,
                AlbumName = track.Title,
                ArtistName = artist,
                InfoUrl = $"{rd.BaseUrl}/?url={track.Url}",
                TotalTracks = 1,
                CustomString = "track",
                Codec = format,
                Bitrate = bitrate,
                BitDepth = bitDepth
            };

            ProcessReleaseDate(data, track.ReleaseDate);
            return data;
        }

        private static void ProcessReleaseDate(AlbumData albumData, string? releaseDate)
        {
            if (string.IsNullOrEmpty(releaseDate))
            {
                albumData.ReleaseDate = DateTime.Now.Year.ToString();
                albumData.ReleaseDatePrecision = "year";
            }
            else if (Regex.IsMatch(releaseDate, "^\\d{4}-\\d{2}-\\d{2}$"))
            {
                albumData.ReleaseDate = releaseDate;
                albumData.ReleaseDatePrecision = "day";
            }
            else if (Regex.IsMatch(releaseDate, "^\\d{4}$"))
            {
                albumData.ReleaseDate = releaseDate;
                albumData.ReleaseDatePrecision = "year";
            }
            else
            {
                Match match = Regex.Match(releaseDate, "\\b(\\d{4})\\b");
                albumData.ReleaseDate = match.Success ? match.Groups[1].Value : DateTime.Now.Year.ToString();
                albumData.ReleaseDatePrecision = "year";
            }

            albumData.ParseReleaseDate();
        }

        private static string NormalizeJsonData(string js)
        {
            js = Regex.Replace(js, @"([\{,])\s*([a-zA-Z0-9_$]+)\s*:", "$1\"$2\":");
            js = Regex.Replace(js, @":\s*'([^']*)'", ":\"$1\"");
            js = Regex.Replace(js, @":\s*True\b", ":true");
            js = Regex.Replace(js, @":\s*False\b", ":false");
            return js;
        }
    }
}