using System.Text.Json;
using System.Text.Json.Serialization;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Soulseek
{
    public record SlskdSearchResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("searchText")] string SearchText,
        [property: JsonPropertyName("startedAt")] DateTime StartedAt,
        [property: JsonPropertyName("endedAt")] DateTime? EndedAt,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("isComplete")] bool IsComplete,
        [property: JsonPropertyName("fileCount")] int FileCount,
        [property: JsonPropertyName("responseCount")] int ResponseCount,
        [property: JsonPropertyName("token")] int Token,
        [property: JsonPropertyName("responses")] List<SlskdFolderData> Responses
    );

    public record SlskdLockedFile(
        [property: JsonPropertyName("filename")] string Filename
    );

    public record SlskdFileData(
        [property: JsonPropertyName("filename")] string? Filename,
        [property: JsonPropertyName("bitRate")] int? BitRate,
        [property: JsonPropertyName("bitDepth")] int? BitDepth,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("length")] int? Length,
        [property: JsonPropertyName("extension")] string? Extension,
        [property: JsonPropertyName("sampleRate")] int? SampleRate,
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("isLocked")] bool IsLocked)
    {
        public static IEnumerable<SlskdFileData> GetFilteredFiles(List<SlskdFileData> files, bool onlyIncludeAudio = false, IEnumerable<string>? includedFileExtensions = null)
        {
            foreach (SlskdFileData file in files)
            {
                string? extension = !string.IsNullOrWhiteSpace(file.Extension) ? file.Extension : Path.GetExtension(file.Filename);

                if (onlyIncludeAudio &&
                    AudioFormatHelper.GetAudioCodecFromExtension(extension ?? "") == AudioFormat.Unknown &&
                    !(includedFileExtensions?.Contains(extension, StringComparer.OrdinalIgnoreCase) ?? false))
                {
                    continue;
                }

                yield return file with { Extension = extension };
            }
        }
    }

    public record SlskdFolderData(
        string Path,
        string Artist,
        string Album,
        string Year,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("hasFreeUploadSlot")] bool HasFreeUploadSlot,
        [property: JsonPropertyName("uploadSpeed")] long UploadSpeed,
        [property: JsonPropertyName("lockedFileCount")] int LockedFileCount,
        [property: JsonPropertyName("lockedFiles")] List<SlskdLockedFile> LockedFiles,
        [property: JsonPropertyName("queueLength")] int QueueLength,
        [property: JsonPropertyName("token")] int Token,
        [property: JsonPropertyName("fileCount")] int FileCount,
        [property: JsonPropertyName("files")] List<SlskdFileData> Files)
    {
        public int CalculatePriority(int expectedTrackCount = 0)
        {
            // Early exit: completely locked folders
            if (LockedFileCount >= FileCount && FileCount > 0)
                return 0;

            // Early exit: more than 50% locked = useless source
            double availabilityRatio = FileCount > 0 ? (FileCount - LockedFileCount) / (double)FileCount : 1.0;
            if (availabilityRatio <= 0.5)
                return 0;

            int score = 0;

            // Get actual track count
            int actualTrackCount = 0;
            if (expectedTrackCount > 0)
            {
                actualTrackCount = Files.Count(f =>
                    AudioFormatHelper.GetAudioCodecFromExtension(
                        f.Extension ?? System.IO.Path.GetExtension(f.Filename) ?? "") != AudioFormat.Unknown);
            }

            // Early exit: Missing 50%+ of expected tracks
            if (expectedTrackCount > 0 && actualTrackCount > 0 && actualTrackCount <= expectedTrackCount * 0.5)
                return 0;

            // ===== TRACK COUNT MATCHING (0 to +2500) =====
            if (expectedTrackCount > 0 && actualTrackCount > 0)
            {
                int trackDiff = actualTrackCount - expectedTrackCount;

                if (trackDiff < 0) // -1: ~500 pts, -2: ~60 pts, -3+: near 0
                    score += (int)(2500 * Math.Exp(-Math.Pow(Math.Abs(trackDiff), 2) * 5));
                else if (trackDiff == 0)  // PERFECT MATCH
                    score += 2500;
                else   // EXTRA TRACKS: Less critical, just penalized: +1: ~1600 pts, +2: ~600 pts, +3: ~100 pts, +15: near 0
                    score += (int)(2500 * Math.Exp(-Math.Pow(trackDiff, 2) * 1.5));
            }

            // ===== AVAILABILITY RATIO (0 to +2000) =====
            score += (int)(Math.Pow(availabilityRatio, 2.0) * 2000);

            // ===== UPLOAD SPEED (0 to +1800) =====
            if (UploadSpeed > 0)
            {
                double speedMbps = UploadSpeed / (1024.0 * 1024.0 / 8.0);
                score += Math.Min(1800, (int)(Math.Log10(Math.Max(0.1, speedMbps) + 1) * 1100));
            }

            // ===== QUEUE LENGTH (50 to +1500) =====
            double queueFactor = Math.Pow(0.94, Math.Min(QueueLength, 40));
            score += (int)(queueFactor * 1500);

            // ===== FREE UPLOAD SLOT (0 or +800) =====
            score += HasFreeUploadSlot ? 800 : 0;

            // ===== COLLECTION SIZE (0 to +300) =====
            score += Math.Min(300, (int)(Math.Log10(Math.Max(1, FileCount) + 1) * 150));

            return Math.Clamp(score, 0, 10000);
        }
    }

    public record SlskdSearchData(
        [property: JsonPropertyName("artist")] string? Artist,
        [property: JsonPropertyName("album")] string? Album,
        [property: JsonPropertyName("interactive")] bool Interactive,
        [property: JsonPropertyName("expandDirectory")] bool ExpandDirectory,
        [property: JsonPropertyName("mimimumFiles")] int MinimumFiles,
        [property: JsonPropertyName("maximumFiles")] int? MaximumFiles)
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
        public static SlskdSearchData FromJson(string jsonString) => JsonSerializer.Deserialize<SlskdSearchData>(jsonString, _jsonOptions)!;
    }

    public record SlskdDirectoryApiResponse(
      [property: JsonPropertyName("files")] List<SlskdDirectoryApiFile> Files
    );

    public record SlskdDirectoryApiFile(
        [property: JsonPropertyName("filename")] string Filename,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("code")] int Code
    );
}