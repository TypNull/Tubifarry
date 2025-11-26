using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Parser.Model;
using System.Net;
using System.Text.Json;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzCreatedForPlaylist
{
    public class ListenBrainzCreatedForPlaylistParser : IParseImportListResponse
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
        private readonly ListenBrainzCreatedForPlaylistSettings _settings;
        private readonly ListenBrainzCreatedForPlaylistRequestGenerator _requestGenerator;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public ListenBrainzCreatedForPlaylistParser(ListenBrainzCreatedForPlaylistSettings settings,
                                         ListenBrainzCreatedForPlaylistRequestGenerator requestGenerator,
                                         IHttpClient httpClient)
        {
            _settings = settings;
            _requestGenerator = requestGenerator;
            _httpClient = httpClient;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IList<ImportListItemInfo> ParseResponse(ImportListResponse importListResponse)
        {
            if (!PreProcess(importListResponse))
                return [];

            try
            {
                IList<ImportListItemInfo> items = ParseCreatedForPlaylists(importListResponse.Content);
                _logger.Debug("Successfully parsed {0} items from ListenBrainz playlists", items.Count);
                return items;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse ListenBrainz playlists response");
                throw new ImportListException(importListResponse, "Failed to parse response", ex);
            }
        }

        private IList<ImportListItemInfo> ParseCreatedForPlaylists(string content)
        {
            PlaylistsResponse? response = JsonSerializer.Deserialize<PlaylistsResponse>(content, _jsonOptions);
            IReadOnlyList<PlaylistInfo>? playlists = response?.Playlists;

            if (playlists?.Any() != true)
            {
                _logger.Debug("No playlists found in response");
                return [];
            }

            string targetPlaylistType = _requestGenerator.GetPlaylistTypeName();
            List<PlaylistInfo> matchingPlaylists = playlists
                .Where(p => IsTargetPlaylistType(p, targetPlaylistType))
                .ToList();

            if (matchingPlaylists.Count == 0)
            {
                _logger.Debug("No playlists of type {0} found", targetPlaylistType);
                return [];
            }

            List<ImportListItemInfo> allItems = [];
            foreach (PlaylistInfo? playlist in matchingPlaylists)
            {
                try
                {
                    string? identifier = playlist.Playlist?.Identifier;
                    if (string.IsNullOrWhiteSpace(identifier))
                        continue;

                    _logger.Debug("Processing playlist of type {0}: {1}", targetPlaylistType, identifier);
                    IList<ImportListItemInfo> playlistItems = FetchPlaylistItems(ExtractPlaylistMbid(identifier));
                    allItems.AddRange(playlistItems);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to process playlist");
                }
            }

            return allItems;
        }

        private bool IsTargetPlaylistType(PlaylistInfo playlist, string targetType)
        {
            try
            {
                Dictionary<string, JsonElement>? extension = playlist.Playlist?.Extension;
                if (extension?.ContainsKey("https://musicbrainz.org/doc/jspf#playlist") != true)
                    return false;

                JsonElement meta = extension["https://musicbrainz.org/doc/jspf#playlist"];
                string? sourcePatch = meta.GetProperty("additional_metadata")
                                     .GetProperty("algorithm_metadata")
                                     .GetProperty("source_patch")
                                     .GetString();

                return sourcePatch == targetType;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Error checking playlist type");
                return false;
            }
        }

        private static string ExtractPlaylistMbid(string identifier) =>
            identifier.Split('/').Last();

        private List<ImportListItemInfo> FetchPlaylistItems(string mbid)
        {
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
                    _logger.Warn("Failed to fetch playlist {0} with HTTP {1}", mbid, response.StatusCode);
                    return [];
                }

                PlaylistResponse? playlistResponse = JsonSerializer.Deserialize<PlaylistResponse>(response.Content, _jsonOptions);
                IReadOnlyList<TrackData>? tracks = playlistResponse?.Playlist?.Tracks;

                if (tracks?.Any() != true)
                {
                    _logger.Debug("No tracks found in playlist {0}", mbid);
                    return [];
                }

                _logger.Trace("Processing {0} tracks from playlist {1}", tracks.Count, mbid);

                return tracks
                    .Select(ExtractAlbumInfo)
                    .Where(item => item != null)
                    .Cast<ImportListItemInfo>()
                    .GroupBy(item => new { item.Album, item.Artist, item.ArtistMusicBrainzId })
                    .Select(g => g.First())
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to fetch playlist {0}", mbid);
                return [];
            }
        }

        private ImportListItemInfo? ExtractAlbumInfo(TrackData track)
        {
            try
            {
                string? album = track.Album;
                string? artist = track.Creator;

                if (string.IsNullOrWhiteSpace(album) || string.IsNullOrWhiteSpace(artist))
                    return null;

                string? artistMbid = ExtractArtistMbid(track);

                return new ImportListItemInfo
                {
                    Album = album,
                    Artist = artist,
                    ArtistMusicBrainzId = artistMbid
                };
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to extract album info from track");
                return null;
            }
        }

        private string? ExtractArtistMbid(TrackData track)
        {
            try
            {
                Dictionary<string, JsonElement>? extension = track.Extension;
                if (extension?.ContainsKey("https://musicbrainz.org/doc/jspf#track") != true)
                    return null;

                JsonElement trackMeta = extension["https://musicbrainz.org/doc/jspf#track"];
                JsonElement.ArrayEnumerator artists = trackMeta.GetProperty("additional_metadata")
                                      .GetProperty("artists")
                                      .EnumerateArray();

                JsonElement firstArtist = artists.FirstOrDefault();
                if (firstArtist.ValueKind == JsonValueKind.Undefined)
                    return null;

                if (firstArtist.TryGetProperty("artist_mbid", out JsonElement mbidElement))
                {
                    return mbidElement.GetString();
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to extract artist MBID from track");
                return null;
            }
        }

        private static bool PreProcess(ImportListResponse importListResponse)
        {
            if (importListResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new ImportListException(importListResponse, "Unexpected status code {0}", importListResponse.HttpResponse.StatusCode);
            }
            return true;
        }
    }
}