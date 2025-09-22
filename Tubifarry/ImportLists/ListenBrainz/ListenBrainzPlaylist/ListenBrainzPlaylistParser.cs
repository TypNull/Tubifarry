using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Parser.Model;
using System.Net;
using System.Text.Json;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzPlaylist
{
    public class ListenBrainzPlaylistParser : IParseImportListResponse
    {
        private readonly Logger _logger;

        public ListenBrainzPlaylistParser(ListenBrainzPlaylistSettings settings)
        {
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IList<ImportListItemInfo> ParseResponse(ImportListResponse importListResponse)
        {
            if (!PreProcess(importListResponse))
                return new List<ImportListItemInfo>();

            try
            {
                IList<ImportListItemInfo> items = ParsePlaylistTracks(importListResponse.Content);
                _logger.Debug("Successfully parsed {0} items from ListenBrainz playlist", items.Count);
                return items;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse ListenBrainz playlist response");
                throw new ImportListException(importListResponse, "Failed to parse response", ex);
            }
        }

        private IList<ImportListItemInfo> ParsePlaylistTracks(string content)
        {
            PlaylistResponse? playlistResponse = JsonSerializer.Deserialize<PlaylistResponse>(content, GetJsonOptions());
            IReadOnlyList<TrackData>? tracks = playlistResponse?.Playlist?.Tracks;

            if (tracks?.Any() != true)
            {
                _logger.Debug("No tracks found in playlist response");
                return new List<ImportListItemInfo>();
            }

            _logger.Trace("Processing {0} tracks from playlist", tracks.Count);

            return tracks
                .Select(ExtractAlbumInfo)
                .Where(item => item != null)
                .Cast<ImportListItemInfo>()
                .GroupBy(item => new { item.Album, item.Artist, item.ArtistMusicBrainzId })
                .Select(g => g.First())
                .ToList();
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
                _logger.Error(ex, "Failed to extract artist MBID from track");
                return null;
            }
        }

        private static JsonSerializerOptions GetJsonOptions() => new()
        {
            PropertyNameCaseInsensitive = true
        };

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
