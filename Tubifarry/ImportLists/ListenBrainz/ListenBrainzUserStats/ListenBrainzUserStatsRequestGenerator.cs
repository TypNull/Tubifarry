using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzUserStats
{
    public class ListenBrainzUserStatsRequestGenerator(ListenBrainzUserStatsSettings settings) : IImportListRequestGenerator
    {
        private readonly ListenBrainzUserStatsSettings _settings = settings;
        private const int MaxItemsPerRequest = 100;

        public virtual ImportListPageableRequestChain GetListItems()
        {
            ImportListPageableRequestChain pageableRequests = new();
            pageableRequests.Add(GetPagedRequests());
            return pageableRequests;
        }

        private IEnumerable<ImportListRequest> GetPagedRequests()
        {
            int requestsNeeded = (_settings.Count + MaxItemsPerRequest - 1) / MaxItemsPerRequest;

            return Enumerable.Range(0, requestsNeeded)
                .Select(page => CreateRequest(page * MaxItemsPerRequest, Math.Min(MaxItemsPerRequest, _settings.Count - (page * MaxItemsPerRequest))))
                .Where(request => request != null);
        }

        private ImportListRequest CreateRequest(int offset, int count)
        {
            if (count <= 0)
                return null!;

            HttpRequestBuilder requestBuilder = new HttpRequestBuilder(_settings.BaseUrl)
                .Accept(HttpAccept.Json);

            if (!string.IsNullOrEmpty(_settings.UserToken))
            {
                requestBuilder.SetHeader("Authorization", $"Token {_settings.UserToken}");
            }

            string endpoint = GetEndpoint();
            string range = GetTimeRange();
            HttpRequest request = requestBuilder.Build();
            request.Url = new HttpUri($"{_settings.BaseUrl}/1/stats/user/{_settings.UserName}/{endpoint}?count={count}&offset={offset}&range={range}");

            return new ImportListRequest(request);
        }

        private string GetEndpoint() => _settings.StatType switch
        {
            (int)ListenBrainzStatType.Artists => "artists",
            (int)ListenBrainzStatType.Releases => "releases",
            (int)ListenBrainzStatType.ReleaseGroups => "release-groups",
            _ => "artists"
        };

        private string GetTimeRange() => _settings.Range switch
        {
            (int)ListenBrainzTimeRange.ThisWeek => "this_week",
            (int)ListenBrainzTimeRange.ThisMonth => "this_month",
            (int)ListenBrainzTimeRange.ThisYear => "this_year",
            (int)ListenBrainzTimeRange.AllTime => "all_time",
            _ => "all_time"
        };
    }
}
