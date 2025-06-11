using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Parser.Model;
using System.Net;
using System.Text.Json;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzRecommendations
{
    public class ListenBrainzRecommendationsParser : IParseImportListResponse
    {
        private readonly ListenBrainzRecommendationsSettings _settings;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public ListenBrainzRecommendationsParser(ListenBrainzRecommendationsSettings settings, IHttpClient httpClient)
        {
            _settings = settings;
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
                items.AddRange(ParseRecommendationPlaylists(importListResponse.Content));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error parsing ListenBrainz recommendation playlists response");
                throw new ImportListException(importListResponse, "Error parsing response", ex);
            }

            _logger.Debug($"Parsed {items.Count} items from ListenBrainz recommendation playlists");
            return items;
        }

        private IList<ImportListItemInfo> ParseRecommendationPlaylists(string content)
        {
            List<ImportListItemInfo> items = new();
            RecommendationPlaylistsResponse? response = JsonSerializer.Deserialize<RecommendationPlaylistsResponse>(content, GetJsonOptions());

            if (response?.Playlists == null)
            {
                _logger.Debug("No recommendation playlists found");
                return items;
            }

            _logger.Debug($"Found {response.Playlists.Count} recommendation playlists");

            foreach (RecommendationPlaylistInfo playlist in response.Playlists)
            {
                try
                {
                    string mbid = playlist.Playlist.Identifier.Split('/').Last();
                    _logger.Debug($"Processing recommendation playlist: {playlist.Playlist.Title} ({mbid})");

                    IList<ImportListItemInfo> playlistItems = FetchPlaylistItems(mbid);
                    items.AddRange(playlistItems);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Error processing recommendation playlist");
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

                _logger.Debug($"Fetching playlist details for {mbid}");
                HttpResponse response = _httpClient.Execute(httpRequest);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Warn($"Failed to fetch playlist {mbid}: HTTP {response.StatusCode}");
                    return items;
                }

                PlaylistResponse? playlistResponse = JsonSerializer.Deserialize<PlaylistResponse>(response.Content, GetJsonOptions());

                if (playlistResponse?.Playlist?.Tracks == null)
                {
                    _logger.Debug($"No tracks found in playlist {mbid}");
                    return items;
                }

                _logger.Debug($"Processing {playlistResponse.Playlist.Tracks.Count} tracks from playlist {mbid}");

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
                        _logger.Debug(ex, "Error processing track in recommendation playlist");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Error fetching recommendation playlist {mbid}");
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
            if (importListResponse.HttpResponse.StatusCode == HttpStatusCode.NoContent)
            {
                _logger.Info("No recommendation playlists available for this user");
                return false;
            }

            if (importListResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new ImportListException(importListResponse, "Unexpected StatusCode [{0}]", importListResponse.HttpResponse.StatusCode);
            }

            return true;
        }
    }
}