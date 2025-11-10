using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using System.Text;
using Tubifarry.Core.Records;

namespace Tubifarry.Metadata.Lyrics
{
    /// <summary>
    /// Helper methods for lyrics processing, matching, file operations, and track info extraction.
    /// </summary>
    public static class LyricsHelper
    {
        public static JToken? ScoreAndSelectBestMatch(List<JToken> artistMatches, List<JToken> songHits, string artistName, string trackTitle, Logger logger)
        {
            JToken? bestMatch = null;
            int bestScore = 0;

            List<JToken> candidatesToScore = artistMatches.Count > 0 ? artistMatches : songHits;

            logger.Trace("Beginning enhanced fuzzy matching process...");

            foreach (JToken hit in candidatesToScore)
            {
                string resultTitle = hit["result"]?["title"]?.ToString() ?? string.Empty;
                string resultArtist = hit["result"]?["primary_artist"]?["name"]?.ToString() ?? string.Empty;

                int tokenSetScore = FuzzySharp.Fuzz.TokenSetRatio(resultTitle, trackTitle);
                int tokenSortScore = FuzzySharp.Fuzz.TokenSortRatio(resultTitle, trackTitle);
                int partialRatio = FuzzySharp.Fuzz.PartialRatio(resultTitle, trackTitle);
                int weightedRatio = FuzzySharp.Fuzz.WeightedRatio(resultTitle, trackTitle);

                int titleScore = Math.Max(Math.Max(tokenSetScore, tokenSortScore), Math.Max(partialRatio, weightedRatio));

                int artistScore = artistMatches.Count > 0 ? 100 : FuzzySharp.Fuzz.WeightedRatio(resultArtist, artistName);

                int combinedScore = ((titleScore * 3) + (artistScore * 7)) / 10;

                logger.Debug($"Match candidate: '{resultTitle}' by '{resultArtist}' - " +
                             $"Title Score: {titleScore} (Token Set: {tokenSetScore}, Token Sort: {tokenSortScore}, " +
                             $"Partial: {partialRatio}, Weighted: {weightedRatio}), " +
                             $"Artist Score: {artistScore}, Combined: {combinedScore}");

                if (combinedScore > bestScore)
                {
                    bestScore = combinedScore;
                    bestMatch = hit;
                    logger.Debug($"New best match found with score: {combinedScore}");
                }
            }

            if (bestMatch == null || bestScore < 70)
            {
                logger.Warn($"Match score below threshold (70%). No lyrics will be selected for: '{trackTitle}' by '{artistName}'");
                return null;
            }

            return bestMatch;
        }


        public static string? CreateLrcContent(Lyric lyric, string artistName, string trackTitle, string albumName, int duration, Logger logger)
        {
            if (lyric.SyncedLyrics == null || lyric.SyncedLyrics.Count == 0)
                return null;

            try
            {
                StringBuilder lrcContent = new();

                lrcContent.AppendLine($"[ar:{artistName}]");
                if (!string.IsNullOrEmpty(albumName))
                    lrcContent.AppendLine($"[al:{albumName}]");
                lrcContent.AppendLine($"[ti:{trackTitle}]");

                if (duration > 0)
                {
                    TimeSpan ts = TimeSpan.FromSeconds(duration);
                    lrcContent.AppendLine($"[length:{ts.ToString(@"mm\:ss\.ff")}]");
                }

                lrcContent.AppendLine("[by:Tubifarry Lyrics Enhancer]");
                lrcContent.AppendLine();

                foreach (SyncLine syncLine in lyric.SyncedLyrics.Where(l => l != null && !string.IsNullOrEmpty(l.LrcTimestamp) && !string.IsNullOrEmpty(l.Line)).OrderBy(l => double.TryParse(l.Milliseconds ?? "0", out double ms) ? ms : 0))
                    lrcContent.AppendLine($"{syncLine.LrcTimestamp} {syncLine.Line}");

                return lrcContent.ToString();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to create LRC content");
                return null;
            }
        }



        public static void EmbedLyricsInAudioFile(string filePath, string lyrics, Logger logger, IRootFolderWatchingService rootFolderWatchingService)
        {
            try
            {
                rootFolderWatchingService.ReportFileSystemChangeBeginning(filePath);
                using (TagLib.File file = TagLib.File.Create(filePath))
                {
                    file.Tag.Lyrics = lyrics;
                    file.Save();
                }
                logger.Trace($"Embedded lyrics in file: {filePath}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Failed to embed lyrics in file: {filePath}");
            }
        }

        public static (string Artist, string Title, string Album, int Duration)? ExtractTrackInfo(TrackFile trackFile, Artist artist, Logger logger)
        {
            if (trackFile.Tracks?.Value == null || trackFile.Tracks.Value.Count == 0)
            {
                logger.Warn($"No tracks found for file: {trackFile.Path}");
                return null;
            }

            Track? track = trackFile.Tracks.Value.FirstOrDefault(x => x != null);
            if (track == null)
            {
                logger.Warn($"No track information found for file: {trackFile.Path}");
                return null;
            }

            Album? album = track.Album;
            string trackTitle = track.Title;
            string artistName = artist.Name;
            string albumName = album?.Title ?? track?.AlbumRelease?.Value?.Album?.Value?.Title ??
                trackFile.Tracks.Value.FirstOrDefault(x => !string.IsNullOrEmpty(x?.Album?.Title))?.Album?.Title ?? "";

            int trackDuration = 0;
            if (track!.Duration > 0)
                trackDuration = (int)Math.Round(TimeSpan.FromMilliseconds(track.Duration).TotalSeconds);

            return (artistName, trackTitle, albumName, trackDuration);
        }

        public static bool LrcFileExistsOnDisk(string trackFilePath, IDiskProvider diskProvider)
        {
            if (string.IsNullOrEmpty(trackFilePath))
                return false;

            string lrcPath = Path.ChangeExtension(trackFilePath, ".lrc");
            return diskProvider.FileExists(lrcPath);
        }
    }
}