using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzCFRecommendations
{
    public class ListenBrainzCFRecommendationsRequestGenerator : IImportListRequestGenerator
    {
        private readonly ListenBrainzCFRecommendationsSettings _settings;
        private const int MaxItemsPerRequest = 100;

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

            HttpRequest request = requestBuilder.Build();
            request.Url = new HttpUri($"{_settings.BaseUrl}/1/cf/recommendation/user/{_settings.UserName}/recording?count={count}&offset={offset}");

            return new ImportListRequest(request);
        }
    }
}
