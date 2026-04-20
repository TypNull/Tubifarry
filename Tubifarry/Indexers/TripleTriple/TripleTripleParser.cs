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

                if (response?.Results == null)
                {
                    logger.Trace("No results found in response");
                    return releases;
                }

                foreach (TripleTripleResult result in response.Results)
                {
                    if (result.Hits == null)
                        continue;

                    foreach (TripleTripleSearchHit hit in result.Hits)
                    {
                        TripleTripleDocument? document = hit.Document;
                        if (document == null)
                            continue;

                        if (document.IsAlbum)
                        {
                            AlbumData albumData = CreateAlbumRelease(document, codec);
                            albumData.ParseReleaseDate();
                            releases.Add(albumData.ToReleaseInfo());
                        }
                        else if (document.IsTrack && isSingle)
                        {
                            AlbumData trackData = CreateTrackRelease(document, codec);
                            trackData.ParseReleaseDate();
                            releases.Add(trackData.ToReleaseInfo());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error parsing TripleTriple search response");
            }

            return releases;
        }

        private AlbumData CreateAlbumRelease(TripleTripleDocument album, TripleTripleCodec codec)
        {
            (AudioFormat format, int bitrate, int bitDepth) = GetQualityForCodec(codec);
            int trackCount = album.TrackNum > 0 ? album.TrackNum : 10;
            long estimatedSize = IndexerParserHelper.EstimateSize(0, 0, bitrate, trackCount);

            return new("TripleTriple", nameof(AmazonMusicDownloadProtocol))
            {
                AlbumId = $"album/{album.Asin}",
                AlbumName = album.Title,
                ArtistName = album.ArtistName,
                InfoUrl = $"https://music.amazon.com/albums/{album.Asin}",
                TotalTracks = trackCount,
                ReleaseDate = album.OriginalReleaseDate.HasValue && album.OriginalReleaseDate.Value > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(album.OriginalReleaseDate.Value).ToString("yyyy-MM-dd")
                    : DateTime.Now.Year.ToString(),
                ReleaseDatePrecision = album.OriginalReleaseDate.HasValue && album.OriginalReleaseDate.Value > 0 ? "day" : "year",
                CustomString = album.ArtOriginal?.Url ?? album.ArtOriginal?.ArtUrl ?? string.Empty,
                Codec = format,
                Bitrate = bitrate,
                BitDepth = bitDepth,
                Size = estimatedSize
            };
        }

        private AlbumData CreateTrackRelease(TripleTripleDocument track, TripleTripleCodec codec)
        {
            (AudioFormat format, int bitrate, int bitDepth) = GetQualityForCodec(codec);
            long estimatedSize = IndexerParserHelper.EstimateSize(0, track.Duration, bitrate);

            return new("TripleTriple", nameof(AmazonMusicDownloadProtocol))
            {
                AlbumId = $"track/{track.Asin}",
                AlbumName = track.AlbumName ?? track.Title,
                ArtistName = track.ArtistName,
                InfoUrl = $"https://music.amazon.com/tracks/{track.Asin}",
                TotalTracks = 1,
                ReleaseDate = track.OriginalReleaseDate.HasValue && track.OriginalReleaseDate.Value > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(track.OriginalReleaseDate.Value).ToString("yyyy-MM-dd")
                    : DateTime.Now.Year.ToString(),
                ReleaseDatePrecision = track.OriginalReleaseDate.HasValue && track.OriginalReleaseDate.Value > 0 ? "day" : "year",
                Duration = track.Duration,
                CustomString = track.ArtOriginal?.Url ?? track.ArtOriginal?.ArtUrl ?? string.Empty,
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
