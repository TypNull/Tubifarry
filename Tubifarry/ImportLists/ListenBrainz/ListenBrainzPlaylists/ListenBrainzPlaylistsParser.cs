using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Parser.Model;
using System.Net;
using System.Text.Json;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzPlaylists
{
    public class ListenBrainzPlaylistsParser : IParseImportListResponse
    {
        private readonly ListenBrainzPlaylistsSettings _settings;
        private readonly ListenBrainzPlaylistsRequestGenerator _requestGenerator;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public ListenBrainzPlaylistsParser(ListenBrainzPlaylistsSettings settings,
                                         ListenBrainzPlaylistsRequestGenerator requestGenerator,
                                         IHttpClient httpClient)
        {
            _settings = settings;
            _requestGenerator = requestGenerator;
            _httpClient = httpClient;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IList<ImportListItemInfo> ParseResponse(ImportListResponse importListResponse)
        {
            List<ImportListItemInfo> items = new();

            if (!PreProcess(importListResponse))
                return items;

            try
            {
                items.AddRange(ParseCreatedForPlaylists(importListResponse.Content));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error parsing ListenBrainz created-for playlists response");
                throw new ImportListException(importListResponse, "Error parsing response", ex);
            }

            _logger.Debug($"Parsed {items.Count} items from ListenBrainz created-for playlists");
            return items;
        }

        private IList<ImportListItemInfo> ParseCreatedForPlaylists(string content)
        {
            List<ImportListItemInfo> items = new();
            PlaylistsResponse? response = JsonSerializer.Deserialize<PlaylistsResponse>(content, GetJsonOptions());

            if (response?.Playlists == null) return items;

            string targetPlaylistType = _requestGenerator.GetPlaylistTypeName();

            foreach (PlaylistInfo playlist in response.Playlists)
            {
                try
                {
                    if (!playlist.Playlist.Extension.ContainsKey("https://musicbrainz.org/doc/jspf#playlist"))
                        continue;

                    JsonElement meta = playlist.Playlist.Extension["https://musicbrainz.org/doc/jspf#playlist"];
                    string? sourcePatch = meta.GetProperty("additional_metadata")
                                         .GetProperty("algorithm_metadata")
                                         .GetProperty("source_patch")
                                         .GetString();

                    if (sourcePatch != targetPlaylistType)
                        continue;

                    _logger.Debug($"Found matching playlist of type '{sourcePatch}': {playlist.Playlist.Identifier}");

                    // Fetch playlist details
                    string mbid = playlist.Playlist.Identifier.Split('/').Last();
                    IList<ImportListItemInfo> playlistItems = FetchPlaylistItems(mbid);
                    items.AddRange(playlistItems);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Error processing created-for playlist");
                }
            }

            return items;
        }

        private IList<ImportListItemInfo> FetchPlaylistItems(string mbid)
        {
            List<ImportListItemInfo> items = new();

            try
            {
                HttpRequestBuilder request = new HttpRequestBuilder(_settings.BaseUrl)
                    .Accept(HttpAccept.Json);

                if (!string.IsNullOrEmpty(_settings.UserToken))
                {
                    request.SetHeader("Authorization", $"Token {_settings.UserToken}");
                }

                HttpRequest httpRequest = request.Build();
                httpRequest.Url = new HttpUri($"{_settings.BaseUrl}/1/playlist/{mbid}");

                HttpResponse response = _httpClient.Execute(httpRequest);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Warn($"Failed to fetch playlist {mbid}: HTTP {response.StatusCode}");
                    return items;
                }

                PlaylistResponse? playlistResponse = JsonSerializer.Deserialize<PlaylistResponse>(response.Content, GetJsonOptions());

                if (playlistResponse?.Playlist?.Tracks == null) return items;

                foreach (TrackData track in playlistResponse.Playlist.Tracks)
                {
                    try
                    {
                        if (!track.Extension.ContainsKey("https://musicbrainz.org/doc/jspf#track"))
                            continue;

                        JsonElement trackMeta = track.Extension["https://musicbrainz.org/doc/jspf#track"];
                        JsonElement.ArrayEnumerator artists = trackMeta.GetProperty("additional_metadata")
                                              .GetProperty("artists")
                                              .EnumerateArray();

                        foreach (JsonElement artist in artists)
                        {
                            if (artist.TryGetProperty("artist_mbid", out JsonElement mbidElement) &&
                                mbidElement.ValueKind == JsonValueKind.String)
                            {
                                string? artistMbid = mbidElement.GetString();
                                if (!string.IsNullOrEmpty(artistMbid))
                                {
                                    string? artistName = artist.TryGetProperty("artist_name", out JsonElement nameElement) ?
                                                   nameElement.GetString() : "";

                                    items.Add(new ImportListItemInfo
                                    {
                                        Artist = artistName,
                                        ArtistMusicBrainzId = artistMbid
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Error processing track in playlist");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Error fetching playlist {mbid}");
            }

            return items;
        }

        private JsonSerializerOptions GetJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        private bool PreProcess(ImportListResponse importListResponse)
        {
            if (importListResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new ImportListException(importListResponse, "Unexpected StatusCode [{0}]", importListResponse.HttpResponse.StatusCode);
            }

            return true;
        }
    }
}