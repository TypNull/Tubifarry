using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.TripleTriple
{
    public interface ITripleTripleParser : IParseIndexerResponse { }

    public class TripleTripleParser(Logger logger) : ITripleTripleParser
    {
        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = [];
            try
            {
                bool isSingle = false;
                TripleTripleCodec codec = TripleTripleCodec.FLAC;
                if (!string.IsNullOrEmpty(indexerResponse.Request.HttpRequest.ContentSummary))
                {
                    TripleTripleRequestData? requestData = JsonSerializer.Deserialize<TripleTripleRequestData>(
                        indexerResponse.Request.HttpRequest.ContentSummary,
                        IndexerParserHelper.StandardJsonOptions);
                    isSingle = requestData?.IsSingle ?? false;
                    if (requestData?.Codec != null && Enum.TryParse(requestData.Codec, true, out TripleTripleCodec parsed))
                        codec = parsed;
                }

                TripleTripleSearchResponse? response = JsonSerializer.Deserialize<TripleTripleSearchResponse>(
                    indexerResponse.Content,
                    IndexerParserHelper.StandardJsonOptions);

                if (response?.Data == null)
                {
                    logger.Trace("No results found in response");
                    return releases;
                }

                foreach (TripleTripleSearchEdge edge in response.Data.SearchAlbums?.Edges ?? [])
                {
                    if (edge.Node == null)
                        continue;
                    AlbumData albumData = CreateAlbumRelease(edge.Node, codec);
                    albumData.ParseReleaseDate();
                    releases.Add(albumData.ToReleaseInfo());
                }

                if (isSingle)
                {
                    foreach (TripleTripleSearchEdge edge in response.Data.SearchTracks?.Edges ?? [])
                    {
                        if (edge.Node == null)
                            continue;
                        AlbumData trackData = CreateTrackRelease(edge.Node, codec);
                        trackData.ParseReleaseDate();
                        releases.Add(trackData.ToReleaseInfo());
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error parsing TripleTriple search response");
            }

            return releases;
        }

        private AlbumData CreateAlbumRelease(TripleTripleSearchNode album, TripleTripleCodec codec)
        {
            (AudioFormat format, int bitrate, int bitDepth) = GetQualityForCodec(codec);
            int trackCount = album.TrackCount > 0 ? album.TrackCount : 10;
            long estimatedSize = IndexerParserHelper.EstimateSize(0, album.Duration, bitrate, trackCount);
            bool hasDate = DateTime.TryParse(album.ReleaseDate, out DateTime releaseDate);

            return new("TripleTriple", nameof(AmazonMusicDownloadProtocol))
            {
                AlbumId = $"album/{album.Id}",
                AlbumName = album.Title,
                ArtistName = album.ArtistName,
                InfoUrl = $"https://music.amazon.com/albums/{album.Id}",
                TotalTracks = trackCount,
                ReleaseDate = hasDate ? releaseDate.ToString("yyyy-MM-dd") : DateTime.Now.Year.ToString(),
                ReleaseDatePrecision = hasDate ? "day" : "year",
                Duration = album.Duration,
                CustomString = album.CoverUrl ?? string.Empty,
                Codec = format,
                Bitrate = bitrate,
                BitDepth = bitDepth,
                Size = estimatedSize
            };
        }

        private AlbumData CreateTrackRelease(TripleTripleSearchNode track, TripleTripleCodec codec)
        {
            (AudioFormat format, int bitrate, int bitDepth) = GetQualityForCodec(codec);
            long estimatedSize = IndexerParserHelper.EstimateSize(0, track.Duration, bitrate);
            bool hasDate = DateTime.TryParse(track.ReleaseDate, out DateTime releaseDate);

            return new("TripleTriple", nameof(AmazonMusicDownloadProtocol))
            {
                AlbumId = $"track/{track.Id}",
                AlbumName = track.Album?.Title ?? track.Title,
                ArtistName = track.ArtistName,
                InfoUrl = $"https://music.amazon.com/tracks/{track.Id}",
                TotalTracks = 1,
                ReleaseDate = hasDate ? releaseDate.ToString("yyyy-MM-dd") : DateTime.Now.Year.ToString(),
                ReleaseDatePrecision = hasDate ? "day" : "year",
                Duration = track.Duration,
                CustomString = track.CoverUrl ?? track.Album?.CoverUrl ?? string.Empty,
                Codec = format,
                Bitrate = bitrate,
                BitDepth = bitDepth,
                Size = estimatedSize
            };
        }

        private static (AudioFormat Format, int Bitrate, int BitDepth) GetQualityForCodec(TripleTripleCodec codec) => codec switch
        {
            TripleTripleCodec.FLAC => (AudioFormat.FLAC, 1411, 0),
            TripleTripleCodec.OPUS => (AudioFormat.Opus, 320, 0),
            TripleTripleCodec.EAC3 => (AudioFormat.EAC3, 640, 0),
            _ => (AudioFormat.FLAC, 1411, 24)
        };
    }
}
