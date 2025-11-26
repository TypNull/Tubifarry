using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.LastFm;

namespace Tubifarry.ImportLists.LastFmRecommendation
{
    public class LastFmRecomendRequestGenerator(LastFmRecommendSettings settings) : IImportListRequestGenerator
    {
        private readonly LastFmRecommendSettings _settings = settings;

        public virtual ImportListPageableRequestChain GetListItems()
        {
            ImportListPageableRequestChain pageableRequests = new();

            pageableRequests.Add(GetPagedRequests());

            return pageableRequests;
        }

        private IEnumerable<ImportListRequest> GetPagedRequests()
        {
            string method = _settings.Method switch
            {
                (int)LastFmRecommendMethodList.TopAlbums => "user.gettopalbums",
                (int)LastFmRecommendMethodList.TopArtists => "user.getTopArtists",
                _ => "user.getTopTracks"
            };

            string period = _settings.Period switch
            {
                (int)LastFmUserTimePeriod.LastWeek => "7day",
                (int)LastFmUserTimePeriod.LastMonth => "1month",
                (int)LastFmUserTimePeriod.LastThreeMonths => "3month",
                (int)LastFmUserTimePeriod.LastSixMonths => "6month",
                (int)LastFmUserTimePeriod.LastTwelveMonths => "12month",
                _ => "overall"
            };

            HttpRequest request = new HttpRequestBuilder(_settings.BaseUrl)
                .AddQueryParam("api_key", _settings.ApiKey)
                .AddQueryParam("method", method)
                .AddQueryParam("user", _settings.UserId)
                .AddQueryParam("period", period)
                .AddQueryParam("limit", _settings.FetchCount)
                .AddQueryParam("format", "json")
                .Accept(HttpAccept.Json)
                .Build();

            yield return new ImportListRequest(request);
        }
    }
}