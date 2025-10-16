using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzCreatedForPlaylist
{
    public class ListenBrainzCreatedForPlaylistRequestGenerator : IImportListRequestGenerator
    {
        private readonly ListenBrainzCreatedForPlaylistSettings _settings;
        private const int DefaultPlaylistsPerCall = 25;

        public ListenBrainzCreatedForPlaylistRequestGenerator(ListenBrainzCreatedForPlaylistSettings settings) => _settings = settings;

        public virtual ImportListPageableRequestChain GetListItems()
        {
            ImportListPageableRequestChain pageableRequests = new();
            pageableRequests.Add(GetPagedRequests());
            return pageableRequests;
        }

        private IEnumerable<ImportListRequest> GetPagedRequests() => (List<ImportListRequest>)
        [
            CreateRequest(0, DefaultPlaylistsPerCall)
        ];

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
            request.Url = new HttpUri($"{_settings.BaseUrl}/1/user/{_settings.UserName}/playlists/createdfor?count={count}&offset={offset}");

            return new ImportListRequest(request);
        }

        public string GetPlaylistTypeName() => _settings.PlaylistType switch
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
