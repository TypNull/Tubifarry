using FuzzySharp;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Tubifarry.Metadata.Lyrics.Converters;

namespace Tubifarry.Metadata.Lyrics
{
    public sealed record TrackInfo(string Artist, string Title, string Album, int DurationSeconds);

    public static class LyricsHelper
    {
        public const string LrcExtension = ".lrc";
        public const string ElrcExtension = ".elrc";
        public const string TtmlExtension = ".ttml";
        public const string LyricsfileExtension = ".lyricsfile";

        public static readonly string[] AdditionalCoreExtensions = [ElrcExtension, TtmlExtension, LyricsfileExtension];

        private const int MinimumMatchScore = 70;

        public static (LyricConverterBase Converter, string Extension) ResolveWordSynced(LyricsEnhancerSettings settings) =>
            (WordSyncedFileType)settings.WordSyncedFileTypeOption switch
            {
                WordSyncedFileType.Ttml => (new TtmlConverter(), TtmlExtension),
                WordSyncedFileType.Lyricsfile => (new LyricsfileConverter(), LyricsfileExtension),
                _ => (new ElrcConverter(), ElrcExtension)
            };

        public static (LyricConverterBase Converter, string Extension) ResolveLineSynced(LyricsEnhancerSettings settings) =>
            (LineSyncedFileType)settings.LineSyncedFileTypeOption == LineSyncedFileType.UseSameAsWordSynced
                ? ResolveWordSynced(settings)
                : (new LrcConverter(), LrcExtension);

        public static bool IsLyricFile(ExtraFile file)
        {
            string? extension = !string.IsNullOrEmpty(file.Extension) ? file.Extension : Path.GetExtension(file.RelativePath);
            return !string.IsNullOrEmpty(extension) && LyricFileExtensions.Extensions.Contains(extension);
        }

        public static JToken? ScoreAndSelectBestMatch(List<JToken> artistMatches, List<JToken> songHits, string artistName, string trackTitle, Logger logger)
        {
            bool artistConfirmed = artistMatches.Count > 0;
            List<JToken> candidates = artistConfirmed ? artistMatches : songHits;

            JToken? bestMatch = null;
            int bestScore = 0;

            foreach (JToken hit in candidates)
            {
                string title = hit["result"]?["title"]?.ToString() ?? string.Empty;
                string artist = hit["result"]?["primary_artist"]?["name"]?.ToString() ?? string.Empty;

                int titleScore = Math.Max(
                    Math.Max(Fuzz.TokenSetRatio(title, trackTitle), Fuzz.TokenSortRatio(title, trackTitle)),
                    Math.Max(Fuzz.PartialRatio(title, trackTitle), Fuzz.WeightedRatio(title, trackTitle)));
                int artistScore = artistConfirmed ? 100 : Fuzz.WeightedRatio(artist, artistName);
                int combinedScore = ((titleScore * 3) + (artistScore * 7)) / 10;

                logger.Debug($"Match candidate: '{title}' by '{artist}' - Title: {titleScore}, Artist: {artistScore}, Combined: {combinedScore}");

                if (combinedScore > bestScore)
                {
                    bestScore = combinedScore;
                    bestMatch = hit;
                }
            }

            if (bestScore < MinimumMatchScore)
            {
                logger.Warn($"Best match score {bestScore} is below threshold {MinimumMatchScore}, no lyrics selected for: '{trackTitle}' by '{artistName}'");
                return null;
            }

            return bestMatch;
        }

        public static bool EmbedLyricsInAudioFile(string filePath, string lyrics, Logger logger, IRootFolderWatchingService rootFolderWatchingService)
        {
            try
            {
                rootFolderWatchingService.ReportFileSystemChangeBeginning(filePath);

                using (TagLib.File file = TagLib.File.Create(filePath))
                {
                    file.Tag.Lyrics = lyrics;
                    file.Save();
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Failed to embed lyrics in file: {filePath}");
                return false;
            }
        }

        public static TrackInfo? ExtractTrackInfo(TrackFile trackFile, Artist artist, Logger logger)
        {
            Track? track = trackFile.Tracks?.Value?.FirstOrDefault(x => x != null);
            if (track == null)
            {
                logger.Warn($"No track information found for file: {trackFile.Path}");
                return null;
            }

            string albumName = track.Album?.Title
                ?? track.AlbumRelease?.Value?.Album?.Value?.Title
                ?? trackFile.Tracks!.Value.FirstOrDefault(x => !string.IsNullOrEmpty(x?.Album?.Title))?.Album?.Title
                ?? string.Empty;

            int durationSeconds = track.Duration > 0
                ? (int)Math.Round(TimeSpan.FromMilliseconds(track.Duration).TotalSeconds)
                : 0;

            return new TrackInfo(artist.Name, track.Title, albumName, durationSeconds);
        }

        public static bool LyricFileExistsOnDisk(string trackFilePath, IDiskProvider diskProvider) =>
            TryFindLyricFileOnDisk(trackFilePath, diskProvider, out _);

        public static bool TryFindLyricFileOnDisk(string trackFilePath, IDiskProvider diskProvider, out string lyricFilePath)
        {
            lyricFilePath = string.Empty;

            if (string.IsNullOrEmpty(trackFilePath))
                return false;

            foreach (string extension in LyricFileExtensions.Extensions)
            {
                string candidate = Path.ChangeExtension(trackFilePath, extension);
                if (diskProvider.FileExists(candidate))
                {
                    lyricFilePath = candidate;
                    return true;
                }
            }

            return false;
        }
    }
}