using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Invidious
{
    public interface IInvidiousParser : IParseIndexerResponse { }

    public partial class InvidiousParser(Logger logger) : IInvidiousParser
    {
        private const int DEFAULT_BITRATE = 128;

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = [];
            try
            {
                string baseUrl = string.Empty;
                if (!string.IsNullOrEmpty(indexerResponse.Request.HttpRequest.ContentSummary))
                {
                    InvidiousRequestData? requestData = JsonSerializer.Deserialize<InvidiousRequestData>(
                        indexerResponse.Request.HttpRequest.ContentSummary,
                        IndexerParserHelper.StandardJsonOptions);
                    baseUrl = requestData?.BaseUrl ?? string.Empty;
                }

                List<InvidiousSearchResult>? results = JsonSerializer.Deserialize<List<InvidiousSearchResult>>(
                    indexerResponse.Content,
                    IndexerParserHelper.StandardJsonOptions);

                if (results == null || results.Count == 0)
                {
                    logger.Trace("No results found in Invidious response");
                    return releases;
                }

                foreach (InvidiousSearchResult result in results)
                {
                    try
                    {
                        if (result.Type != "playlist" || !IsAlbumPlaylistId(result.PlaylistId))
                            continue;

                        AlbumData albumData = CreatePlaylistRelease(result, baseUrl);
                        releases.Add(albumData.ToReleaseInfo());
                    }
                    catch (Exception ex)
                    {
                        logger.Debug(ex, $"Failed to process Invidious search result: {result.Title}");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error parsing Invidious search response");
            }

            return releases;
        }

        private static bool IsAlbumPlaylistId(string? playlistId) =>
            !string.IsNullOrEmpty(playlistId) && playlistId.StartsWith("OLAK5uy_", StringComparison.Ordinal);

        private static AlbumData CreatePlaylistRelease(InvidiousSearchResult result, string baseUrl)
        {
            string rawTitle = result.Title ?? "Unknown";
            string playlistCreator = result.Author ?? "Unknown Artist";

            (string artist, string albumTitle) = ExtractArtistAndTitle(rawTitle, playlistCreator);

            int trackCount = result.VideoCount > 0 ? result.VideoCount : 1;
            int estimatedDurationPerTrack = 210;
            int totalDuration = trackCount * estimatedDurationPerTrack;
            long estimatedSize = IndexerParserHelper.EstimateSize(0, totalDuration, DEFAULT_BITRATE);

            string thumbnailUrl = result.PlaylistThumbnail ?? string.Empty;
            if (!string.IsNullOrEmpty(thumbnailUrl) && thumbnailUrl.StartsWith('/'))
                thumbnailUrl = baseUrl.TrimEnd('/') + thumbnailUrl;

            string downloadId = result.PlaylistId!;

            return new AlbumData("Invidious", nameof(YoutubeDownloadProtocol))
            {
                AlbumId = downloadId,
                AlbumName = albumTitle,
                ArtistName = artist,
                InfoUrl = $"{baseUrl.TrimEnd('/')}/playlist?list={result.PlaylistId}",
                TotalTracks = trackCount,
                Duration = totalDuration,
                CustomString = thumbnailUrl,
                CoverResolution = "Unknown Resolution",
                Codec = AudioFormat.AAC,
                Bitrate = DEFAULT_BITRATE,
                Size = estimatedSize
            };
        }

        private static string StripMetadataTags(string title) => MetadataTagPattern().Replace(title, " ").Trim();

        private static (string artist, string title) ExtractArtistAndTitle(string rawTitle, string fallbackArtist)
        {
            string[] separators = [" - ", " – ", " — ", " | "];
            foreach (string sep in separators)
            {
                int idx = rawTitle.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    string left = StripMetadataTags(rawTitle[..idx].Trim());
                    string right = StripMetadataTags(rawTitle[(idx + sep.Length)..].Trim());
                    return (left, right);
                }
            }

            return (fallbackArtist, StripMetadataTags(rawTitle));
        }

        [GeneratedRegex(@"\s*[\(\[](?:official\s+)?(?:music\s+)?(?:audio|video|lyric(?:\s*video)?|lyrics|full\s+album|complete\s+album|album|hd|hq|4k|visualizer|explicit|remastered|\d{4})[\)\]]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled, "de-DE")]
        private static partial Regex MetadataTagPattern();
    }
}
