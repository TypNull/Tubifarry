using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzPlaylist
{
    public class ListenBrainzPlaylistRequestGenerator : IImportListRequestGenerator
    {
        private readonly ListenBrainzPlaylistSettings _settings;

        public ListenBrainzPlaylistRequestGenerator(ListenBrainzPlaylistSettings settings)
        {
            _settings = settings;
        }

        public virtual ImportListPageableRequestChain GetListItems()
        {
            ImportListPageableRequestChain pageableRequests = new();

            List<ImportListRequest> requests = new();

            foreach (string playlistId in _settings.PlaylistIds)
            {
                if (!string.IsNullOrWhiteSpace(playlistId))
                {
                    requests.Add(CreatePlaylistRequest(playlistId));
                }
            }

            if (requests.Any())
            {
                pageableRequests.Add(requests);
            }

            return pageableRequests;
        }

        private ImportListRequest CreatePlaylistRequest(string playlistId)
        {
            HttpRequestBuilder requestBuilder = new HttpRequestBuilder(_settings.BaseUrl)
                .Accept(HttpAccept.Json);

            if (!string.IsNullOrEmpty(_settings.UserToken))
            {
                requestBuilder.SetHeader("Authorization", $"Token {_settings.UserToken}");
            }

            HttpRequest request = requestBuilder.Build();
            request.Url = new HttpUri($"{_settings.BaseUrl}/1/playlist/{playlistId}");

            return new ImportListRequest(request);
        }

        public string GetEndpointUrl() => (ListenBrainzPlaylistEndpointType)_settings.PlaylistType switch
        {
            ListenBrainzPlaylistEndpointType.Normal => $"{_settings.BaseUrl}/1/user/{_settings.AccessToken}/playlists",
            ListenBrainzPlaylistEndpointType.CreatedFor => $"{_settings.BaseUrl}/1/user/{_settings.AccessToken}/playlists/createdfor",
            ListenBrainzPlaylistEndpointType.Recommendations => $"{_settings.BaseUrl}/1/user/{_settings.AccessToken}/playlists/recommendations",
            _ => $"{_settings.BaseUrl}/1/user/{_settings.AccessToken}/playlists"
        };

        public ImportListRequest CreateDiscoveryRequest(int count = 100, int offset = 0)
        {
            HttpRequestBuilder requestBuilder = new HttpRequestBuilder(_settings.BaseUrl)
                .Accept(HttpAccept.Json);

            if (!string.IsNullOrEmpty(_settings.UserToken))
            {
                requestBuilder.SetHeader("Authorization", $"Token {_settings.UserToken}");
            }

            HttpRequest request = requestBuilder.Build();
            string endpointUrl = GetEndpointUrl();
            request.Url = new HttpUri($"{endpointUrl}?count={count}&offset={offset}");

            return new ImportListRequest(request);
        }
    }
}
