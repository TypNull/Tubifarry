using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using System.Net;
using System.Text.Json;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzPlaylist
{
    public class ListenBrainzPlaylistImportList : HttpImportListBase<ListenBrainzPlaylistSettings>
    {
        private static object _currentOperation = new();

        public override string Name => "ListenBrainz Playlists";
        public override ImportListType ListType => ImportListType.Other;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromDays(1);
        public override int PageSize => 0;
        public override TimeSpan RateLimit => TimeSpan.FromMilliseconds(200);

        public ListenBrainzPlaylistImportList(IHttpClient httpClient,
                                               IImportListStatusService importListStatusService,
                                               IConfigService configService,
                                               IParsingService parsingService,
                                               Logger logger)
            : base(httpClient, importListStatusService, configService, parsingService, logger) { }

        public override IImportListRequestGenerator GetRequestGenerator() =>
            new ListenBrainzPlaylistRequestGenerator(Settings);

        public override IParseImportListResponse GetParser() =>
            new ListenBrainzPlaylistParser(Settings);

        protected override bool IsValidRelease(ImportListItemInfo release) =>
            release.AlbumMusicBrainzId.IsNotNullOrWhiteSpace() ||
            release.ArtistMusicBrainzId.IsNotNullOrWhiteSpace() ||
            !release.Album.IsNullOrWhiteSpace() || !release.Artist.IsNullOrWhiteSpace();

        protected override ValidationFailure TestConnection()
        {
            try
            {
                ListenBrainzPlaylistRequestGenerator generator = new(Settings);
                ImportListRequest discoveryRequest = generator.CreateDiscoveryRequest(1, 0);

                ImportListResponse response = FetchImportListResponse(discoveryRequest);

                if (response.HttpResponse.StatusCode != HttpStatusCode.OK)
                {
                    return new ValidationFailure(string.Empty, $"Connection failed with HTTP {(int)response.HttpResponse.StatusCode} ({response.HttpResponse.StatusCode})");
                }

                return null!;
            }
            catch (ImportListException ex)
            {
                _logger.Warn(ex, "Connection test failed");
                return new ValidationFailure(string.Empty, $"Connection error: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Test connection failed");
                return new ValidationFailure(string.Empty, "Configuration error, check logs for details");
            }
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "getPlaylists")
            {
                if (Settings.AccessToken.IsNullOrWhiteSpace())
                {
                    return null!;
                }

                try
                {
                    List<dynamic> playlists = FetchAvailablePlaylists();

                    return new
                    {
                        options = new
                        {
                            user = Settings.AccessToken,
                            playlists = playlists.OrderBy(p => p.name)
                        }
                    };
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Error fetching playlists from ListenBrainz");
                    return null!;
                }
            }

            return null!;
        }

        private List<dynamic> FetchAvailablePlaylists()
        {
            List<dynamic> allPlaylists = new();
            int offset = 0;
            const int count = 100;

            object thisOperationToken = new();
            _currentOperation = thisOperationToken;

            Task.Delay(2000).Wait();

            if (thisOperationToken != _currentOperation)
                return null!;

            while (true)
            {
                if (thisOperationToken != _currentOperation)
                    break;

                ListenBrainzPlaylistRequestGenerator generator = new(Settings);
                ImportListRequest request = generator.CreateDiscoveryRequest(count, offset);

                ImportListResponse response = FetchImportListResponse(request);

                if (response.HttpResponse.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Warn("Failed to fetch playlists with HTTP {0}", response.HttpResponse.StatusCode);
                    break;
                }

                PlaylistsResponse? playlistsResponse = JsonSerializer.Deserialize<PlaylistsResponse>(response.Content, GetJsonOptions());
                IReadOnlyList<PlaylistInfo>? playlists = playlistsResponse?.Playlists;

                if (playlists?.Any() != true)
                {
                    break;
                }

                foreach (PlaylistInfo playlist in playlists)
                {
                    PlaylistData? playlistData = playlist.Playlist;
                    if (playlistData?.Identifier != null && playlistData.Title != null)
                    {
                        string id = ExtractPlaylistMbid(playlistData.Identifier);
                        string name = playlistData.Title;
                        allPlaylists.Add(new { id, name });
                    }
                }

                if (playlists.Count < count)
                {
                    break;
                }

                offset += count;
            }

            return allPlaylists;
        }

        private static string ExtractPlaylistMbid(string identifier) =>
            identifier.Split('/').Last();

        private static JsonSerializerOptions GetJsonOptions() => new()
        {
            PropertyNameCaseInsensitive = true
        };
    }
}