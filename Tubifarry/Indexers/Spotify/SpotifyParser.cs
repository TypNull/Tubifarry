using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;
using Tubifarry.Core.Model;

namespace Tubifarry.Indexers.Spotify
{
    public interface ISpotifyParser : IParseIndexerResponse
    {
        void SetYouTubeAuth(SpotifyIndexerSettings settings);
    }

    /// <summary>
    /// Parses Spotify API responses and converts them to album data.
    /// </summary>
    internal class SpotifyParser : ISpotifyParser
    {
        private readonly Logger _logger;
        private readonly ISpotifyToYouTubeEnricher _enricher;
        private SpotifyIndexerSettings? _currentSettings;

        public SpotifyParser(Logger logger, ISpotifyToYouTubeEnricher enricher)
        {
            _logger = logger;
            _enricher = enricher;
        }

        public void SetYouTubeAuth(SpotifyIndexerSettings settings)
        {
            _currentSettings = settings;
            _enricher.SetAuth(settings);
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = new();

            try
            {
                if (string.IsNullOrEmpty(indexerResponse.Content))
                {
                    _logger.Warn("Received empty response content from Spotify API");
                    return releases;
                }

                using JsonDocument jsonDoc = JsonDocument.Parse(indexerResponse.Content);
                JsonElement root = jsonDoc.RootElement;
                List<AlbumData> albums = new();

                if (root.TryGetProperty("albums", out JsonElement albumsElement))
                {
                    if (albumsElement.TryGetProperty("items", out JsonElement itemsElement))
                        albums.AddRange(ParseAlbumItems(itemsElement));
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    albums.AddRange(ParseAlbumItems(root));
                }

                foreach (AlbumData album in _enricher.EnrichWithYouTubeData(albums.Take(_currentSettings?.MaxSearchResults ?? 20).ToList()).Where(x => x.Bitrate > 0))
                    releases.Add(album.ToReleaseInfo());

                _logger.Debug($"Successfully converted {releases.Count} albums to releases");
                return releases.DistinctBy(x => x.DownloadUrl).OrderByDescending(o => o.PublishDate).ToList();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error parsing Spotify response. Response length: {indexerResponse.Content?.Length ?? 0}");
                return releases;
            }
        }

        private List<AlbumData> ParseAlbumItems(JsonElement itemsElement)
        {
            List<AlbumData> albums = new();

            foreach (JsonElement albumElement in itemsElement.EnumerateArray())
            {
                try
                {
                    AlbumData albumData = ExtractAlbumInfo(albumElement);
                    albumData.ParseReleaseDate();
                    albums.Add(albumData);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, $"Failed to parse album from Spotify response: {albumElement}");
                }
            }

            return albums;
        }

        private static AlbumData ExtractAlbumInfo(JsonElement album) => new("Spotify", nameof(YoutubeDownloadProtocol))
        {
            AlbumId = album.TryGetProperty("id", out JsonElement idProp) ? idProp.GetString() ?? "UnknownAlbumId" : "UnknownAlbumId",
            AlbumName = album.TryGetProperty("name", out JsonElement nameProp) ? nameProp.GetString() ?? "UnknownAlbum" : "UnknownAlbum",
            ArtistName = ExtractArtistName(album),
            InfoUrl = album.TryGetProperty("external_urls", out JsonElement externalUrlsProp) && externalUrlsProp.TryGetProperty("spotify", out JsonElement spotifyUrlProp) ? spotifyUrlProp.GetString() ?? string.Empty : string.Empty,
            ReleaseDate = album.TryGetProperty("release_date", out JsonElement releaseDateProp) ? releaseDateProp.GetString() ?? "0000-01-01" : "0000-01-01",
            ReleaseDatePrecision = album.TryGetProperty("release_date_precision", out JsonElement precisionProp) ? precisionProp.GetString() ?? "day" : "day",
            TotalTracks = album.TryGetProperty("total_tracks", out JsonElement totalTracksProp) ? totalTracksProp.GetInt32() : 0,
            ExplicitContent = album.TryGetProperty("explicit", out JsonElement explicitProp) && explicitProp.GetBoolean(),
            CustomString = ExtractAlbumArtUrl(album),
            CoverResolution = ExtractCoverResolution(album)
        };

        private static string ExtractArtistName(JsonElement album)
        {
            if (!album.TryGetProperty("artists", out JsonElement artistsProp) || artistsProp.GetArrayLength() == 0)
                return "UnknownArtist";
            return artistsProp[0].TryGetProperty("name", out JsonElement nameProp) ? nameProp.GetString() ?? "UnknownArtist" : "UnknownArtist";
        }

        private static string ExtractAlbumArtUrl(JsonElement album)
        {
            if (!album.TryGetProperty("images", out JsonElement imagesProp) || imagesProp.GetArrayLength() == 0)
                return string.Empty;
            return imagesProp[0].TryGetProperty("url", out JsonElement urlProp) ? urlProp.GetString() ?? string.Empty : string.Empty;
        }

        private static string ExtractCoverResolution(JsonElement album)
        {
            if (!album.TryGetProperty("images", out JsonElement imagesProp) || imagesProp.GetArrayLength() == 0)
                return "Unknown";

            JsonElement firstImage = imagesProp[0];
            bool hasWidth = firstImage.TryGetProperty("width", out JsonElement widthProp);
            bool hasHeight = firstImage.TryGetProperty("height", out JsonElement heightProp);

            if (hasWidth && hasHeight)
                return $"{widthProp.GetInt32()}x{heightProp.GetInt32()}";
            return "Unknown";
        }
    }
}
