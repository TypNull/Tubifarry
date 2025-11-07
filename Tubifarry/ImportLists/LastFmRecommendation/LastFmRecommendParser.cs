using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.ImportLists.LastFm;
using NzbDrone.Core.Parser.Model;
using System.Net;
using System.Text.Json;

namespace Tubifarry.ImportLists.LastFmRecommendation
{
    internal class LastFmRecommendParser : IParseImportListResponse
    {
        private readonly LastFmRecommendSettings _settings;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public LastFmRecommendParser(LastFmRecommendSettings settings, IHttpClient httpClient)
        {
            _settings = settings;
            _httpClient = httpClient;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IList<ImportListItemInfo> ParseResponse(ImportListResponse importListResponse)
        {
            List<ImportListItemInfo> items = [];

            if (!PreProcess(importListResponse))
                return items;

            LastFmTopResponse? jsonResponse = JsonSerializer.Deserialize<LastFmTopResponse>(importListResponse.Content, _jsonOptions);
            if (jsonResponse == null)
                return items;

            if (jsonResponse.TopAlbums != null)
            {
                _logger.Trace("Processing TopAlbums response.");
                items.AddRange(ProcessTopAlbumsResponse(jsonResponse));
            }
            else if (jsonResponse.TopArtists != null)
            {
                _logger.Trace("Processing TopArtists response.");
                items.AddRange(ProcessTopArtistsResponse(jsonResponse));
            }
            else if (jsonResponse.TopTracks != null)
            {
                _logger.Trace("Processing TopTracks response.");
                items.AddRange(ProcessTopTracksResponse(jsonResponse));
            }

            _logger.Trace("Parsing complete. Total items: {0}", items.Count);
            return items;
        }

        private List<ImportListItemInfo> ProcessTopAlbumsResponse(LastFmTopResponse jsonResponse)
        {
            List<ImportListItemInfo> items = [];
            List<LastFmArtist> inputArtists = jsonResponse.TopAlbums?.Album.ConvertAll(x => x.Artist) ?? [];
            _logger.Trace("Found {0} input artists from TopAlbums.", inputArtists.Count);

            List<List<LastFmArtist>> similarLists = [];
            foreach (LastFmArtist artist in inputArtists)
            {
                HttpRequest request = BuildRequest("artist.getSimilar", new Dictionary<string, string> { { "artist", artist.Name } });
                ImportListResponse response = FetchImportListResponse(request);
                LastFmSimilarArtistsResponse? similarResponse = JsonSerializer.Deserialize<LastFmSimilarArtistsResponse>(response.Content, _jsonOptions);
                List<LastFmArtist> similarList = similarResponse?.SimilarArtists?.Artist ?? [];
                similarLists.Add(similarList);
                _logger.Trace("Artist '{0}': Fetched {1} similar artists.", artist.Name, similarList.Count);
            }

            List<(LastFmArtist artist, int count, int bestRank)> sortedRecommended = GroupAndSortSimilarArtists(similarLists);
            _logger.Trace("Grouped similar artists. Unique recommendations: {0}", sortedRecommended.Count);

            int overallCap = _settings.FetchCount * _settings.ImportCount;
            int totalAlbums = 0;
            foreach ((LastFmArtist artist, int count, int bestRank) in sortedRecommended)
            {
                int albumLimitForArtist = Math.Min(count, 5);
                List<LastFmAlbum> albums = FetchTopAlbumsForArtist(artist);
                foreach (LastFmAlbum? album in albums.Take(albumLimitForArtist))
                {
                    items.Add(ConvertAlbumToImportListItems(album));
                    totalAlbums++;
                    if (totalAlbums >= overallCap)
                    {
                        _logger.Trace("Overall album cap of {0} reached.", overallCap);
                        break;
                    }
                }
                if (totalAlbums >= overallCap)
                    break;
            }
            return items;
        }

        private List<ImportListItemInfo> ProcessTopArtistsResponse(LastFmTopResponse jsonResponse)
        {
            List<ImportListItemInfo> items = [];
            List<LastFmArtist>? inputArtists = jsonResponse.TopArtists?.Artist ?? [];
            _logger.Trace("Found {0} input artists from TopArtists.", inputArtists.Count);

            List<List<LastFmArtist>> similarLists = [];
            foreach (LastFmArtist artist in inputArtists)
            {
                HttpRequest request = BuildRequest("artist.getSimilar", new Dictionary<string, string> { { "artist", artist.Name } });
                ImportListResponse response = FetchImportListResponse(request);
                LastFmSimilarArtistsResponse? similarResponse = JsonSerializer.Deserialize<LastFmSimilarArtistsResponse>(response.Content, _jsonOptions);
                List<LastFmArtist> similarList = similarResponse?.SimilarArtists?.Artist ?? [];
                similarLists.Add(similarList);
                _logger.Trace("Artist '{0}': Fetched {1} similar artists.", artist.Name, similarList.Count);
            }

            List<(LastFmArtist artist, int count, int bestRank)> sortedRecommended = GroupAndSortSimilarArtists(similarLists);
            _logger.Trace("Grouped similar artists. Unique recommendations: {0}", sortedRecommended.Count);

            int overallCap = _settings.FetchCount * _settings.ImportCount;
            int totalArtists = 0;
            foreach ((LastFmArtist artist, int count, int bestRank) in sortedRecommended)
            {
                items.Add(new ImportListItemInfo
                {
                    Artist = artist.Name,
                    ArtistMusicBrainzId = artist.Mbid
                });
                totalArtists++;
                if (totalArtists >= overallCap)
                    break;
            }
            return items;
        }

        private List<ImportListItemInfo> ProcessTopTracksResponse(LastFmTopResponse jsonResponse)
        {
            List<ImportListItemInfo> items = [];
            List<LastFmTrack>? inputTracks = jsonResponse.TopTracks?.Track ?? [];
            _logger.Trace("Found {0} input tracks from TopTracks.", inputTracks.Count);

            List<List<LastFmArtist>> similarLists = [];
            foreach (LastFmTrack track in inputTracks)
            {
                HttpRequest request = BuildRequest("track.getSimilar", new Dictionary<string, string>
            {
                { "artist", track.Artist.Name },
                { "track", track.Name }
            });
                ImportListResponse response = FetchImportListResponse(request);
                LastFmSimilarTracksResponse? similarResponse = JsonSerializer.Deserialize<LastFmSimilarTracksResponse>(response.Content, _jsonOptions);
                List<LastFmArtist> similarList = similarResponse?.SimilarTracks?.Track.ConvertAll(t => t.Artist) ?? [];
                similarLists.Add(similarList);
                _logger.Trace("Track '{0}' by '{1}': Fetched {2} similar artists.", track.Name, track.Artist.Name, similarList.Count);
            }

            List<(LastFmArtist artist, int count, int bestRank)> sortedRecommended = GroupAndSortSimilarArtists(similarLists);
            _logger.Trace("Grouped similar artists. Unique recommendations: {0}", sortedRecommended.Count);

            int overallCap = _settings.FetchCount * _settings.ImportCount;
            int totalArtists = 0;
            foreach ((LastFmArtist artist, int count, int bestRank) in sortedRecommended)
            {
                items.Add(new ImportListItemInfo
                {
                    Artist = artist.Name,
                    ArtistMusicBrainzId = artist.Mbid
                });
                totalArtists++;
                if (totalArtists >= overallCap)
                    break;
            }
            return items;
        }

        private static List<(LastFmArtist artist, int count, int bestRank)> GroupAndSortSimilarArtists(List<List<LastFmArtist>> similarLists)
        {
            Dictionary<string, (LastFmArtist artist, int count, int bestRank)> recommendedDict = [];
            int maxRank = similarLists.Count != 0 ? similarLists.Max(list => list.Count) : 0;

            for (int rank = 0; rank < maxRank; rank++)
            {
                foreach (List<LastFmArtist> list in similarLists)
                {
                    if (list.Count > rank)
                    {
                        LastFmArtist simArtist = list[rank];
                        string key = !string.IsNullOrEmpty(simArtist.Mbid) ? simArtist.Mbid : simArtist.Name;
                        if (recommendedDict.TryGetValue(key, out (LastFmArtist artist, int count, int bestRank) entry))
                        {
                            entry.count++;
                            entry.bestRank = Math.Min(entry.bestRank, rank);
                            recommendedDict[key] = entry;
                        }
                        else
                        {
                            recommendedDict[key] = (simArtist, 1, rank);
                        }
                    }
                }
            }

            return [.. recommendedDict.Values
                .OrderByDescending(x => x.count)
                .ThenBy(x => x.bestRank)];
        }

        private List<LastFmAlbum> FetchTopAlbumsForArtist(LastFmArtist artist)
        {
            _logger.Trace("Fetching top albums for artist '{0}'.", artist.Name);
            HttpRequest request = BuildRequest("artist.gettopalbums", new Dictionary<string, string> { { "artist", artist.Name } });
            ImportListResponse response = FetchImportListResponse(request);
            return JsonSerializer.Deserialize<LastFmTopAlbumsResponse>(response.Content, _jsonOptions)?.TopAlbums?.Album ?? [];
        }

        private static ImportListItemInfo ConvertAlbumToImportListItems(LastFmAlbum album) => new()
        {
            Album = album.Name,
            AlbumMusicBrainzId = album.Mbid,
            Artist = album.Artist.Name,
            ArtistMusicBrainzId = album.Artist.Mbid
        };

        private HttpRequest BuildRequest(string method, Dictionary<string, string> parameters)
        {
            HttpRequestBuilder requestBuilder = new HttpRequestBuilder(_settings.BaseUrl)
                .AddQueryParam("api_key", _settings.ApiKey)
                .AddQueryParam("method", method)
                .AddQueryParam("limit", _settings.ImportCount)
                .AddQueryParam("format", "json")
                .WithRateLimit(5)
                .Accept(HttpAccept.Json);

            foreach (KeyValuePair<string, string> param in parameters)
                requestBuilder.AddQueryParam(param.Key, param.Value);

            _logger.Trace("Built request for method '{0}' with parameters: {1}", method, string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}")));
            return requestBuilder.Build();
        }

        protected virtual ImportListResponse FetchImportListResponse(HttpRequest request)
        {
            _logger.Trace("Fetching API response from {0}", request.Url);
            return new ImportListResponse(new ImportListRequest(request), _httpClient.Execute(request));
        }

        protected virtual bool PreProcess(ImportListResponse importListResponse)
        {
            if (importListResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
                throw new ImportListException(importListResponse, "Unexpected StatusCode [{0}]", importListResponse.HttpResponse.StatusCode);

            if (importListResponse.HttpResponse.Headers.ContentType?.Contains("text/json") == true && importListResponse.HttpRequest.Headers.Accept?.Contains("text/json") == false)
                throw new ImportListException(importListResponse, "Server returned HTML content");

            return true;
        }
    }
}