using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzUserStats
{
    public class ListenBrainzUserStatsRequestGenerator : IImportListRequestGenerator
    {
        private readonly ListenBrainzUserStatsSettings _settings;
        private const int MAX_ITEMS_PER_REQUEST = 100; // ListenBrainz API limit

        public ListenBrainzUserStatsRequestGenerator(ListenBrainzUserStatsSettings settings)
        {
            _settings = settings;
        }

        public virtual ImportListPageableRequestChain GetListItems()
        {
            ImportListPageableRequestChain pageableRequests = new();
            pageableRequests.Add(GetPagedRequests());
            return pageableRequests;
        }

        private IEnumerable<ImportListRequest> GetPagedRequests()
        {
            int totalToFetch = _settings.Count;
            int totalRequested = 0;
            int offset = 0;

            // Generate multiple paginated requests if count > MAX_ITEMS_PER_REQUEST
            while (totalRequested < totalToFetch)
            {
                int currentPageSize = Math.Min(totalToFetch - totalRequested, MAX_ITEMS_PER_REQUEST);

                string endpoint = GetEndpoint();
                string range = GetTimeRange();

                HttpRequestBuilder requestBuilder = new HttpRequestBuilder(_settings.BaseUrl)
                    .Accept(HttpAccept.Json);

                if (!string.IsNullOrEmpty(_settings.UserToken))
                {
                    requestBuilder.SetHeader("Authorization", $"Token {_settings.UserToken}");
                }

                HttpRequest request = requestBuilder.Build();
                request.Url = new HttpUri($"{_settings.BaseUrl}/1/stats/user/{_settings.UserName}/{endpoint}?count={currentPageSize}&offset={offset}&range={range}");

                yield return new ImportListRequest(request);

                totalRequested += currentPageSize;
                offset += currentPageSize;

                if (currentPageSize < MAX_ITEMS_PER_REQUEST)
                    break;
            }
        }

        private string GetEndpoint()
        {
            return _settings.StatType switch
            {
                (int)ListenBrainzStatType.Artists => "artists",
                (int)ListenBrainzStatType.Releases => "releases",
                (int)ListenBrainzStatType.ReleaseGroups => "release-groups",
                _ => "artists"
            };
        }

        private string GetTimeRange()
        {
            return _settings.Range switch
            {
                (int)ListenBrainzTimeRange.ThisWeek => "this_week",
                (int)ListenBrainzTimeRange.ThisMonth => "this_month",
                (int)ListenBrainzTimeRange.ThisYear => "this_year",
                (int)ListenBrainzTimeRange.AllTime => "all_time",
                _ => "all_time"
            };
        }
    }
}