using Jint;
using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Lucida
{
    public interface ILucidaParser : IParseIndexerResponse { }

    public partial class LucidaParser(Logger logger) : ILucidaParser
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
        private readonly Logger _logger = logger;

        private static readonly Regex[] SearchDataPatterns = [Data1Regex(), Data2Regex()];

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = [];
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
                    List<LucidaDataWrapper>? wrapperList = JsonSerializer.Deserialize<List<LucidaDataWrapper>>(raw, _jsonOptions);
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

            NzbDroneLogger.GetLogger(nameof(LucidaParser)).Info(albumsJson);
            NzbDroneLogger.GetLogger(nameof(LucidaParser)).Info(tracksJson);

            if (!string.IsNullOrEmpty(albumsJson) && albumsJson != "[]")
                albums = JsonSerializer.Deserialize<List<LucidaAlbum>>(albumsJson, _jsonOptions);

            if (!string.IsNullOrEmpty(tracksJson) && tracksJson != "[]")
                tracks = JsonSerializer.Deserialize<List<LucidaTrack>>(tracksJson, _jsonOptions);

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
            List<LucidaArtist> artists = album.Artists ?? [];

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
            List<LucidaArtist> artists = track.Artists ?? [];
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
            else if (ReleaseDateDayRegex().IsMatch(releaseDate))
            {
                albumData.ReleaseDate = releaseDate;
                albumData.ReleaseDatePrecision = "day";
            }
            else if (ReleaseDateYearRegex().IsMatch(releaseDate))
            {
                albumData.ReleaseDate = releaseDate;
                albumData.ReleaseDatePrecision = "year";
            }
            else
            {
                Match match = ReleaseDateYear2Regex().Match(releaseDate);
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

        [GeneratedRegex("^\\d{4}-\\d{2}-\\d{2}$")]
        private static partial Regex ReleaseDateDayRegex();
        [GeneratedRegex("^\\d{4}$")]
        private static partial Regex ReleaseDateYearRegex();
        [GeneratedRegex("\\b(\\d{4})\\b")]
        private static partial Regex ReleaseDateYear2Regex();


        [GeneratedRegex(@"data\s*=\s*(\[(?:[^\[\]]|\[(?:[^\[\]]|\[(?:[^\[\]]|\[[^\[\]]*\])*\])*\])*\]);", RegexOptions.Compiled | RegexOptions.Singleline)]
        private static partial Regex Data1Regex();

        [GeneratedRegex(@"__INITIAL_DATA__\s*=\s*({.+?});", RegexOptions.Compiled | RegexOptions.Singleline)]
        private static partial Regex Data2Regex();
    }
}