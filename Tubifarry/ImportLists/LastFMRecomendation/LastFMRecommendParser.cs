using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.ImportLists.LastFm;
using NzbDrone.Core.Parser.Model;
using System.Net;

namespace Tubifarry.ImportLists.LastFmRecomendation
{
    internal class LastFmRecommendParser : IParseImportListResponse
    {
        private readonly LastFmRecommendSettings _settings;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public LastFmRecommendParser(LastFmRecommendSettings settings, IHttpClient httpClient)
        {
            _settings = settings;
            _httpClient = httpClient;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IList<ImportListItemInfo> ParseResponse(ImportListResponse importListResponse)
        {
            List<ImportListItemInfo> items = new();

            if (!PreProcess(importListResponse))
                return items;

            LastFmTopResponse jsonResponse = Json.Deserialize<LastFmTopResponse>(importListResponse.Content);
            if (jsonResponse == null)
                return items;
            if (jsonResponse.TopAlbums != null)
            {
                _logger.Trace("Processing top albums response");
                List<LastFmArtist> inputArtists = jsonResponse.TopAlbums.Album.Select(x => x.Artist).ToList();
                foreach (LastFmArtist artist in FetchRecommendedArtists(inputArtists))
                {
                    items.AddRange(ConvertAlbumsToImportListItems(FetchTopAlbumsForArtist(artist)));
                }
            }
            else if (jsonResponse.TopArtists != null)
            {
                _logger.Trace("Processing top artists response");
                items.AddRange(ConvertArtistsToImportListItems(FetchRecommendedArtists(jsonResponse.TopArtists.Artist)));
            }
            else if (jsonResponse.TopTracks != null)
            {
                _logger.Trace("Processing top tracks response");
                items.AddRange(ConvertArtistsToImportListItems(FetchRecommendedTracks(jsonResponse.TopTracks.Track)));
            }

            _logger.Debug($"Parsed {items.Count} items from Last.fm response");
            return items;
        }

        private List<LastFmArtist> FetchRecommendedArtists(List<LastFmArtist> artists)
        {
            List<LastFmArtist> recommended = new();
            _logger.Trace($"Fetching similar artists for {artists.Count} input artists");

            foreach (LastFmArtist artist in artists)
            {
                HttpRequest request = BuildRequest("artist.getSimilar", new Dictionary<string, string> { { "artist", artist.Name } });
                ImportListResponse response = FetchImportListResponse(request);
                LastFmSimilarArtistsResponse similarArtistsResponse = Json.Deserialize<LastFmSimilarArtistsResponse>(response.Content);

                if (similarArtistsResponse?.SimilarArtists?.Artist != null)
                {
                    recommended.AddRange(similarArtistsResponse.SimilarArtists.Artist);
                    _logger.Trace($"Found {similarArtistsResponse.SimilarArtists.Artist.Count} similar artists for {artist.Name}");
                }
            }
            return recommended;
        }

        private List<LastFmArtist> FetchRecommendedTracks(List<LastFmTrack> tracks)
        {
            List<LastFmArtist> recommended = new();
            _logger.Trace($"Processing {tracks.Count} tracks for recommendations");

            foreach (LastFmTrack track in tracks)
            {
                HttpRequest request = BuildRequest("track.getSimilar", new Dictionary<string, string> {
                    { "artist", track.Artist.Name }, { "track", track.Name }
                });
                ImportListResponse response = FetchImportListResponse(request);
                LastFmSimilarTracksResponse similarTracksResponse = Json.Deserialize<LastFmSimilarTracksResponse>(response.Content);

                foreach (LastFmTrack similarTrack in similarTracksResponse?.SimilarTracks?.Track ?? new())
                {
                    recommended.Add(similarTrack.Artist);
                }
            }
            return recommended;
        }

        private List<LastFmAlbum> FetchTopAlbumsForArtist(LastFmArtist artist)
        {
            _logger.Trace($"Fetching top albums for {artist.Name}");
            HttpRequest request = BuildRequest("artist.gettopalbums", new Dictionary<string, string> { { "artist", artist.Name } });
            ImportListResponse response = FetchImportListResponse(request);
            return Json.Deserialize<LastFmTopAlbumsResponse>(response.Content)?.TopAlbums?.Album ?? new List<LastFmAlbum>();
        }

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

            _logger.Trace($"Built request for {method} API method");
            return requestBuilder.Build();
        }

        protected virtual ImportListResponse FetchImportListResponse(HttpRequest request)
        {
            _logger.Debug($"Fetching API response from {request.Url}");
            return new ImportListResponse(new ImportListRequest(request), _httpClient.Execute(request));
        }

        protected virtual bool PreProcess(ImportListResponse importListResponse)
        {
            if (importListResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
                throw new ImportListException(importListResponse, "Unexpected StatusCode [{0}]", importListResponse.HttpResponse.StatusCode);

            if (importListResponse.HttpResponse.Headers.ContentType?.Contains("text/json") == true &&
                importListResponse.HttpRequest.Headers.Accept?.Contains("text/json") == false)
                throw new ImportListException(importListResponse, "Server returned HTML content");
            return true;
        }

        private IEnumerable<ImportListItemInfo> ConvertAlbumsToImportListItems(IEnumerable<LastFmAlbum> albums)
        {
            foreach (LastFmAlbum album in albums)
            {
                yield return new ImportListItemInfo
                {
                    Album = album.Name,
                    AlbumMusicBrainzId = album.Mbid,
                    Artist = album.Artist.Name,
                    ArtistMusicBrainzId = album.Artist.Mbid
                };
            }
        }

        private IEnumerable<ImportListItemInfo> ConvertArtistsToImportListItems(IEnumerable<LastFmArtist> artists)
        {
            foreach (LastFmArtist artist in artists)
            {
                yield return new ImportListItemInfo
                {
                    Artist = artist.Name,
                    ArtistMusicBrainzId = artist.Mbid
                };
            }
        }
    }
}