using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Parser.Model;
using System.Net;
using System.Text.Json;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzCFRecommendations
{
    public class ListenBrainzCFRecommendationsParser : IParseImportListResponse
    {
        private readonly ListenBrainzCFRecommendationsSettings _settings;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public ListenBrainzCFRecommendationsParser(ListenBrainzCFRecommendationsSettings settings, IHttpClient httpClient)
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

            try
            {
                items.AddRange(ParseRecordingRecommendations(importListResponse.Content));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error parsing ListenBrainz recording recommendations response");
                throw new ImportListException(importListResponse, "Error parsing response", ex);
            }

            _logger.Debug($"Parsed {items.Count} items from ListenBrainz recording recommendations");
            return items;
        }

        private IList<ImportListItemInfo> ParseRecordingRecommendations(string content)
        {
            List<ImportListItemInfo> items = new();
            RecordingRecommendationResponse? response = JsonSerializer.Deserialize<RecordingRecommendationResponse>(content, GetJsonOptions());

            if (response?.Payload?.Mbids == null)
            {
                _logger.Debug("No recording recommendations found");
                return items;
            }

            _logger.Debug($"Found {response.Payload.Mbids.Count} recording recommendations");

            // Group recordings by their MBIDs to batch the MusicBrainz lookup
            List<string> recordingMbids = response.Payload.Mbids.Select(r => r.RecordingMbid).ToList();

            // We need to look up the artist information for each recording via MusicBrainz API
            // Since we can't use the MusicBrainz API directly here, we'll use ListenBrainz's lookup
            HashSet<string> artistMbids = new();

            foreach (RecordingRecommendation recommendation in response.Payload.Mbids)
            {
                try
                {
                    List<string> artistInfo = LookupRecordingArtist(recommendation.RecordingMbid);
                    if (artistInfo != null)
                    {
                        foreach (string artistId in artistInfo)
                        {
                            artistMbids.Add(artistId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, $"Error looking up artist for recording {recommendation.RecordingMbid}");
                }
            }

            // Convert artist MBIDs to ImportListItemInfo
            foreach (string artistMbid in artistMbids)
            {
                items.Add(new ImportListItemInfo
                {
                    ArtistMusicBrainzId = artistMbid
                });
            }

            return items;
        }

        private List<string> LookupRecordingArtist(string recordingMbid)
        {
            List<string> artistMbids = new();

            try
            {
                // Use MusicBrainz API to look up recording details
                HttpRequestBuilder request = new HttpRequestBuilder("https://musicbrainz.org")
                    .AddQueryParam("fmt", "json")
                    .AddQueryParam("inc", "artist-credits")
                    .Accept(HttpAccept.Json);

                HttpRequest httpRequest = request.Build();
                httpRequest.Url = new HttpUri($"https://musicbrainz.org/ws/2/recording/{recordingMbid}?fmt=json&inc=artist-credits");

                // Add User-Agent as required by MusicBrainz
                httpRequest.Headers.Add("User-Agent", "Lidarr-ListenBrainz-Plugin/1.0 (https://github.com/Lidarr/Lidarr)");

                // Rate limit for MusicBrainz (1 request per second)
                Thread.Sleep(1000);

                HttpResponse response = _httpClient.Execute(httpRequest);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Debug($"Failed to lookup recording {recordingMbid}: HTTP {response.StatusCode}");
                    return artistMbids;
                }

                MusicBrainzRecordingResponse? recordingData = JsonSerializer.Deserialize<MusicBrainzRecordingResponse>(response.Content, GetJsonOptions());

                if (recordingData?.ArtistCredits != null)
                {
                    foreach (MusicBrainzArtistCredit credit in recordingData.ArtistCredits)
                    {
                        if (!string.IsNullOrEmpty(credit.Artist?.Id))
                        {
                            artistMbids.Add(credit.Artist.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, $"Error looking up recording {recordingMbid} in MusicBrainz");
            }

            return artistMbids;
        }

        private JsonSerializerOptions GetJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        private bool PreProcess(ImportListResponse importListResponse)
        {
            if (importListResponse.HttpResponse.StatusCode == HttpStatusCode.NoContent)
            {
                _logger.Info("No recording recommendations available for this user");
                return false;
            }

            if (importListResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new ImportListException(importListResponse, "Unexpected StatusCode [{0}]", importListResponse.HttpResponse.StatusCode);
            }

            return true;
        }
    }
}