using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Text;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.DABMusic
{
    public interface IDABMusicParser : IParseIndexerResponse { }

    public class DABMusicParser : IDABMusicParser
    {
        private readonly Logger _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        };

        public DABMusicParser(Logger logger) => _logger = logger;

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = new();
            try
            {
                DABMusicSearchResponse? searchResponse = JsonSerializer.Deserialize<DABMusicSearchResponse>(indexerResponse.Content, JsonOptions);
                if (searchResponse == null)
                    return releases;

                ProcessItems(searchResponse.Albums, CreateAlbumData, releases);
                ProcessItems(searchResponse.Tracks, CreateTrackData, releases);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error parsing DABMusic search response");
            }
            return releases;
        }

        private static void ProcessItems<T>(IList<T>? items, Func<T, AlbumData> createData, List<ReleaseInfo> releases)
        {
            if ((items?.Count ?? 0) <= 0)
                return;

            foreach (T? item in items!)
            {
                AlbumData data = createData(item);
                data.ParseReleaseDate();
                releases.Add(data.ToReleaseInfo());
            }
        }

        private static AlbumData CreateAlbumData(DABMusicAlbum album)
        {
            (AudioFormat format, int bitrate, int bitDepth) = GetQuality(album.AudioQuality);
            long estimatedSize = album.TrackCount * 50 * 1024 * 1024; // ~50MB per track estimate

            return new("DABMusic", nameof(QobuzDownloadProtocol))
            {
                AlbumId = $"https://www.qobuz.com/us-en/album/{SanitizeForUrl(album.Title)}-{SanitizeForUrl(album.Artist)}/{album.Id}",
                AlbumName = album.Title,
                ArtistName = album.Artist,
                InfoUrl = $"https://www.qobuz.com/us-en/album/{SanitizeForUrl(album.Title)}-{SanitizeForUrl(album.Artist)}/{album.Id}",
                TotalTracks = album.TrackCount > 0 ? album.TrackCount : 1,
                ReleaseDate = album.ReleaseDate ?? DateTime.Now.Year.ToString(),
                ReleaseDatePrecision = "day",
                CustomString = album.Cover!,
                Codec = format,
                Bitrate = bitrate,
                BitDepth = bitDepth,
                Size = estimatedSize
            };
        }

        private static AlbumData CreateTrackData(DABMusicTrack track)
        {
            (AudioFormat format, int bitrate, int bitDepth) = GetQuality(track.AudioQuality);
            long estimatedSize = track.Duration * bitrate * 1000 / 8; // bitrate to bytes per second
            if (estimatedSize <= 0) estimatedSize = 50 * 1024 * 1024; // 50MB fallback

            return new("DABMusic", nameof(QobuzDownloadProtocol))
            {
                AlbumId = $"https://www.qobuz.com/us-en/track/{SanitizeForUrl(track.DisplayAlbum)}-{SanitizeForUrl(track.Artist)}/{track.Id}",
                AlbumName = track.DisplayAlbum,
                ArtistName = track.Artist,
                InfoUrl = $"https://www.qobuz.com/us-en/track/{SanitizeForUrl(track.DisplayAlbum)}-{SanitizeForUrl(track.Artist)}/{track.Id}",
                TotalTracks = 1,
                ReleaseDate = track.ReleaseDate ?? DateTime.Now.Year.ToString(),
                ReleaseDatePrecision = "day",
                Duration = track.Duration,
                CustomString = track.Cover ?? track.Images?.Large ?? track.Images?.Thumbnail!,
                Codec = format,
                Bitrate = bitrate,
                BitDepth = bitDepth,
                Size = estimatedSize
            };
        }

        private static string SanitizeForUrl(string input) => input.ToLowerInvariant()
                .Replace(" ", "-")
                .Replace("&", "and")
                .Where(c => char.IsLetterOrDigit(c) || c == '-')
                .Aggregate(new StringBuilder(), (sb, c) => sb.Append(c))
                .ToString()
                .Trim('-');

        private static (AudioFormat Format, int Bitrate, int BitDepth) GetQuality(DABMusicAudioQuality? audioQuality)
        {
            if (audioQuality == null)
                return (AudioFormat.Unknown, 320, 0);

            int bitDepth = audioQuality.MaximumBitDepth;

            if (audioQuality.IsHiRes)
                return (AudioFormat.FLAC, 1411, bitDepth);

            if (bitDepth <= 16 && bitDepth > 0)
                return (AudioFormat.FLAC, 1000, bitDepth);

            return (AudioFormat.MP3, 320, 0);
        }
    }
}