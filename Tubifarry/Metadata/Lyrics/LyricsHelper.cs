using FuzzySharp;
using FuzzySharp.PreProcess;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using System.Globalization;
using System.Text;
using Tubifarry.Core.Records;
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

        private const int MinimumTitleScore = 80;
        private const int MinimumArtistScore = 75;
        private const int MaximumDurationDifference = 5;
        private const int CloseDurationDifference = 2;

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

        public static JToken? SelectBestGeniusHit(List<JToken> hits, string artistName, string trackTitle, Logger logger)
        {
            JToken? bestMatch = null;
            int bestScore = 0;

            foreach (JToken hit in hits)
            {
                string title = hit["result"]?["title"]?.ToString() ?? string.Empty;
                string artist = hit["result"]?["primary_artist"]?["name"]?.ToString() ?? string.Empty;
                int titleScore = TitleScore(title, trackTitle);
                int artistScore = ArtistScore(artist, artistName);

                logger.Debug($"Match candidate: '{title}' by '{artist}' - Title: {titleScore}, Artist: {artistScore}");

                int score = titleScore + artistScore + ExactTitleScore(title, trackTitle);
                if (titleScore < MinimumTitleScore || artistScore < MinimumArtistScore || score <= bestScore)
                    continue;

                bestScore = score;
                bestMatch = hit;
            }

            if (bestMatch == null)
                logger.Debug($"No Genius result matches '{trackTitle}' by '{artistName}' closely enough");

            return bestMatch;
        }

        public static bool IsMatch(Lyric lyric, string artistName, string trackTitle, int duration)
        {
            int difference = lyric.Duration > 0 && duration > 0 ? Math.Abs(lyric.Duration - duration) : -1;
            if (difference > MaximumDurationDifference)
                return false;

            bool titleMatches = string.IsNullOrWhiteSpace(lyric.Title) || TitleScore(lyric.Title, trackTitle) >= MinimumTitleScore;
            bool artistMatches = string.IsNullOrWhiteSpace(lyric.Artist) || ArtistScore(lyric.Artist, artistName) >= MinimumArtistScore;
            return titleMatches && (artistMatches || (difference >= 0 && difference <= CloseDurationDifference));
        }

        public static int TitleScore(string candidate, string title) => Similarity(candidate, title);

        public static int ExactTitleScore(string candidate, string title) => Fuzz.Ratio(Normalize(candidate), Normalize(title), PreprocessMode.None);

        public static int ArtistScore(string candidate, string artist) => Similarity(candidate, artist);

        private static int Similarity(string left, string right)
        {
            string a = Normalize(left);
            string b = Normalize(right);
            if (a.Length == 0 || b.Length == 0)
                return 0;
            return Math.Max(Fuzz.TokenSetRatio(a, b, PreprocessMode.None), Fuzz.Ratio(a, b, PreprocessMode.None));
        }

        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            StringBuilder builder = new(value.Length);
            foreach (char c in value.Normalize(NormalizationForm.FormD).ToLowerInvariant())
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark)
                    continue;
                builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
            }

            return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Normalize(NormalizationForm.FormC);
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