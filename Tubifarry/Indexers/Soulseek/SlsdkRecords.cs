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
        [property: JsonPropertyName("files")] List<SlskdFileData> Files,
        int RecentFailures = 0)
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

                if (trackDiff < 0) // MISSING TRACKS: +2500 perfect, linear to max +1500 at 1 missing, -2000 at 75%, flat -2000 below
                {
                    double ratio = actualTrackCount / (double)expectedTrackCount;
                    if (ratio < 0.75)
                        score -= 2000;
                    else
                        score += (int)(-2000 + (ratio - 0.75) / 0.25 * 3500);
                }
                else if (trackDiff == 0)  // PERFECT MATCH
                    score += 2500;
                else   // EXTRA TRACKS: Less critical, just penalized: +1: ~1600 pts, +2: ~600 pts, +3: ~100 pts, +15: near 0
                    score += (int)(2500 * Math.Exp(-Math.Pow(trackDiff, 2) * 1.5));
            }

            // ===== AVAILABILITY RATIO (0 to +2000) =====
            score += (int)(Math.Pow(availabilityRatio, 2.0) * 2000);

            // ===== UPLOAD SPEED (0 to +1800) =====
            double speedMBps = UploadSpeed / (1024.0 * 1024.0);
            if (speedMBps > 0)
                score += Math.Min(1800, (int)(Math.Log10(speedMBps + 1) * 1800));

            // ===== ESTIMATED QUEUE WAIT (0 to +2300) =====
            const double assumedBytesPerQueuedItem = 40.0 * 1024 * 1024;
            double effectiveSpeed = Math.Max(UploadSpeed, 128 * 1024);
            double estimatedWaitSeconds = QueueLength * assumedBytesPerQueuedItem / effectiveSpeed;
            double queueFactor = Math.Exp(-estimatedWaitSeconds / 900.0);
            score += (int)(queueFactor * 2300);

            // ===== FILE CONSISTENCY (0 to +300) =====
            if (Files.Count > 0)
            {
                int distinctExtensions = Files
                    .Select(f => (f.Extension ?? System.IO.Path.GetExtension(f.Filename) ?? string.Empty).ToLowerInvariant())
                    .Where(ext => ext.Length > 0)
                    .Distinct()
                    .Count();

                double durationCoverage = Files.Count(f => f.Length is > 0) / (double)Files.Count;
                score += (int)((distinctExtensions <= 1 ? 150 : 0) + durationCoverage * 150);
            }

            // ===== COLLECTION SIZE (0 to +300) =====
            score += Math.Min(300, (int)(Math.Log10(Math.Max(1, FileCount) + 1) * 150));

            if (!HasFreeUploadSlot)
                score = (int)(score * 0.6 * Math.Pow(0.97, Math.Min(QueueLength, 100)));

            if (RecentFailures > 0)
                score = (int)(score * Math.Pow(0.7, Math.Min(RecentFailures, 8)));

            return Math.Clamp(score, 0, 10000);
        }
    }

    public record SlskdSearchData(
        [property: JsonPropertyName("artist")] string? Artist,
        [property: JsonPropertyName("album")] string? Album,
        [property: JsonPropertyName("interactive")] bool Interactive,
        [property: JsonPropertyName("expandDirectory")] bool ExpandDirectory,
        [property: JsonPropertyName("mimimumFiles")] int MinimumFiles,
        [property: JsonPropertyName("maximumFiles")] int? MaximumFiles,
        [property: JsonPropertyName("tracks")] List<string>? Tracks = null,
        [property: JsonPropertyName("trackDurations")] List<int>? TrackDurations = null)
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
        public static SlskdSearchData FromJson(string jsonString) => JsonSerializer.Deserialize<SlskdSearchData>(jsonString, _jsonOptions)!;
    }

    public record SlskdEnqueueFailure(
        [property: JsonPropertyName("filename")] string Filename,
        [property: JsonPropertyName("message")] string Message
    );

    public record SlskdEnqueueResult(string? BatchId, List<string> Enqueued, List<SlskdEnqueueFailure> Failed)
    {
        public bool AllFailed => Enqueued.Count == 0 && Failed.Count > 0;
    }

    public record SlskdDestinationConfig(string DownloadsDirectory, string? SubdirectoryPattern)
    {
        public const string DefaultPattern = "${SOURCE_DIRECTORY}";

        public bool UsesDefaultPattern =>
            SubdirectoryPattern is null ||
            string.Equals(SubdirectoryPattern, DefaultPattern, StringComparison.OrdinalIgnoreCase);

        public static SlskdDestinationConfig? FromOptions(JsonElement options)
        {
            if (!options.TryGetProperty("directories", out JsonElement directories) ||
                !directories.TryGetProperty("downloads", out JsonElement downloads) ||
                downloads.GetString() is not { Length: > 0 } downloadsDirectory)
                return null;

            string? pattern = null;
            if (options.TryGetProperty("transfers", out JsonElement transfers) &&
                transfers.TryGetProperty("download", out JsonElement download) &&
                download.TryGetProperty("destination", out JsonElement destination) &&
                destination.TryGetProperty("subdirectory", out JsonElement subdirectory))
                pattern = subdirectory.GetString();

            return new SlskdDestinationConfig(downloadsDirectory, pattern);
        }
    }

    public sealed record SlskdSearchRequestBody
    {
        public required string Id { get; init; }
        public int FileLimit { get; init; }
        public bool FilterResponses { get; init; }
        public long MaximumPeerQueueLength { get; init; }
        public int MinimumPeerUploadSpeed { get; init; }
        public int MinimumResponseFileCount { get; init; }
        public int ResponseLimit { get; init; }
        public required string SearchText { get; init; }
        public int SearchTimeout { get; init; }
    }

    public record SlskdDirectoryApiResponse(
      [property: JsonPropertyName("files")] List<SlskdDirectoryApiFile> Files,
      [property: JsonPropertyName("name")] string? Name = null
    );

    public record SlskdDirectoryApiFile(
        [property: JsonPropertyName("filename")] string Filename,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("code")] int Code
    );
}