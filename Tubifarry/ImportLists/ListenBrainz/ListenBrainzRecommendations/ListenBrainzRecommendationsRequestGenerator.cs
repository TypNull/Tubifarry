using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzRecommendations
{
    public class ListenBrainzRecommendationsRequestGenerator : IImportListRequestGenerator
    {
        private readonly ListenBrainzRecommendationsSettings _settings;
        private const int DEFAULT_PLAYLISTS_PER_CALL = 25; // ListenBrainz default

        public ListenBrainzRecommendationsRequestGenerator(ListenBrainzRecommendationsSettings settings)
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
            int maxPlaylistsToFetch = _settings.MaxPlaylists;
            int totalRequested = 0;
            int offset = 0;

            // Generate paginated requests to get recommendation playlists up to the user's limit
            while (totalRequested < maxPlaylistsToFetch)
            {
                int currentPageSize = Math.Min(DEFAULT_PLAYLISTS_PER_CALL, maxPlaylistsToFetch - totalRequested);

                HttpRequestBuilder requestBuilder = new HttpRequestBuilder(_settings.BaseUrl)
                    .Accept(HttpAccept.Json);

                if (!string.IsNullOrEmpty(_settings.UserToken))
                {
                    requestBuilder.SetHeader("Authorization", $"Token {_settings.UserToken}");
                }

                HttpRequest request = requestBuilder.Build();
                request.Url = new HttpUri($"{_settings.BaseUrl}/1/user/{_settings.UserName}/playlists/recommendations?count={currentPageSize}&offset={offset}");

                yield return new ImportListRequest(request);

                totalRequested += currentPageSize;
                offset += currentPageSize;

                if (currentPageSize < DEFAULT_PLAYLISTS_PER_CALL)
                    break;
            }
        }
    }
}