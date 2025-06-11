using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzCFRecommendations
{
    public class ListenBrainzCFRecommendationsRequestGenerator : IImportListRequestGenerator
    {
        private readonly ListenBrainzCFRecommendationsSettings _settings;
        private const int MAX_ITEMS_PER_REQUEST = 100; // ListenBrainz API limit

        public ListenBrainzCFRecommendationsRequestGenerator(ListenBrainzCFRecommendationsSettings settings)
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

                HttpRequestBuilder requestBuilder = new HttpRequestBuilder(_settings.BaseUrl)
                    .Accept(HttpAccept.Json);

                if (!string.IsNullOrEmpty(_settings.UserToken))
                {
                    requestBuilder.SetHeader("Authorization", $"Token {_settings.UserToken}");
                }

                HttpRequest request = requestBuilder.Build();
                request.Url = new HttpUri($"{_settings.BaseUrl}/1/cf/recommendation/user/{_settings.UserName}/recording?count={currentPageSize}&offset={offset}");

                yield return new ImportListRequest(request);

                totalRequested += currentPageSize;
                offset += currentPageSize;

                if (currentPageSize < MAX_ITEMS_PER_REQUEST)
                    break;
            }
        }
    }
}