using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzPlaylists
{
    public class ListenBrainzPlaylistsRequestGenerator : IImportListRequestGenerator
    {
        private readonly ListenBrainzPlaylistsSettings _settings;
        private const int DEFAULT_PLAYLISTS_PER_CALL = 25; // ListenBrainz default

        public ListenBrainzPlaylistsRequestGenerator(ListenBrainzPlaylistsSettings settings)
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
                request.Url = new HttpUri($"{_settings.BaseUrl}/1/user/{_settings.UserName}/playlists/createdfor?count={currentPageSize}&offset={offset}");

                yield return new ImportListRequest(request);

                totalRequested += currentPageSize;
                offset += currentPageSize;

                if (currentPageSize < DEFAULT_PLAYLISTS_PER_CALL)
                    break;

            }
        }

        public string GetPlaylistTypeName()
        {
            return _settings.PlaylistType switch
            {
                (int)ListenBrainzPlaylistType.DailyJams => "daily-jams",
                (int)ListenBrainzPlaylistType.WeeklyJams => "weekly-jams",
                (int)ListenBrainzPlaylistType.WeeklyExploration => "weekly-exploration",
                (int)ListenBrainzPlaylistType.WeeklyNew => "weekly-new",
                (int)ListenBrainzPlaylistType.MonthlyExploration => "monthly-exploration",
                _ => "daily-jams"
            };
        }
    }
}