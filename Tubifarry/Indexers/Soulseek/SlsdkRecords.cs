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
                    continue;

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
        public int CalculatePriority()
        {
            if (LockedFileCount >= FileCount && FileCount > 0)
                return 0;

            int score = 0;
            if (FileCount > 0)
            {
                double availabilityRatio = (FileCount - LockedFileCount) / (double)FileCount;
                score += (int)(Math.Pow(availabilityRatio, 0.6) * 4000);
            }

            if (UploadSpeed > 0)
            {
                double speedMbps = UploadSpeed / (1024.0 * 1024.0 / 8.0);
                score += (int)(Math.Log10(Math.Max(0.1, speedMbps) + 1) * 1200);
            }
            double queuePenalty = Math.Pow(0.9, Math.Min(QueueLength, 30));
            score += (int)(queuePenalty * 2000);
            if (HasFreeUploadSlot)
                score += 1000;
            if (FileCount >= 15)
                score += 200;

            return Math.Clamp(score, 0, 10000);
        }
    }

    public record SlskdSearchData(
        [property: JsonPropertyName("artist")] string? Artist,
        [property: JsonPropertyName("album")] string? Album,
        [property: JsonPropertyName("interactive")] bool Interactive,
        [property: JsonPropertyName("mimimumFiles")] int MinimumFiles)
    {
        public static SlskdSearchData FromJson(string jsonString) => JsonSerializer.Deserialize<SlskdSearchData>(jsonString, new JsonSerializerOptions() { PropertyNameCaseInsensitive = true })!;
    }
}