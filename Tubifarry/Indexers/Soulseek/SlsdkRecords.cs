using System.Text.Json;
using System.Text.Json.Serialization;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Soulseek
{
    public record SlskdFileData(string? Filename, int? BitRate, int? BitDepth, long Size, int? Length, string? Extension, int? SampleRate, int Code, bool IsLocked)
    {
        public static IEnumerable<SlskdFileData> GetFiles(JsonElement filesElement, bool onlyIncludeAudio = false, IEnumerable<string>? includedFileExtensions = null)
        {
            if (filesElement.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (JsonElement file in filesElement.EnumerateArray())
            {
                string? filename = file.GetProperty("filename").GetString();
                string? extension = !file.TryGetProperty("extension", out JsonElement extensionElement) || string.IsNullOrWhiteSpace(extensionElement.GetString()) ? Path.GetExtension(filename) : extensionElement.GetString();

                if (onlyIncludeAudio && AudioFormatHelper.GetAudioCodecFromExtension(extension ?? "") == AudioFormat.Unknown && !(includedFileExtensions?.Contains(null, StringComparer.OrdinalIgnoreCase) ?? false))
                    continue;

                yield return new SlskdFileData(
                    Filename: filename,
                    BitRate: file.TryGetProperty("bitRate", out JsonElement bitRateElement) ? bitRateElement.GetInt32() : null,
                    BitDepth: file.TryGetProperty("bitDepth", out JsonElement bitDepthElement) ? bitDepthElement.GetInt32() : null,
                    Size: file.GetProperty("size").GetInt64(),
                    Length: file.TryGetProperty("length", out JsonElement lengthElement) ? lengthElement.GetInt32() : null,
                    Extension: extension,
                    SampleRate: file.TryGetProperty("sampleRate", out JsonElement sampleRateElement) ? sampleRateElement.GetInt32() : null,
                    Code: file.TryGetProperty("code", out JsonElement codeElement) ? codeElement.GetInt32() : 1,
                    IsLocked: file.TryGetProperty("isLocked", out JsonElement isLockedElement) && isLockedElement.GetBoolean()
                );
            }
        }
    }

    public record SlskdFolderData(string Path, string Artist, string Album, string Year, string Username, bool HasFreeUploadSlot, long UploadSpeed, int LockedFileCount, List<string> LockedFiles)
    {
        public SlskdFolderData FillWithSlskdData(JsonElement jsonElement) => this with
        {
            Username = jsonElement.GetProperty("username").GetString() ?? string.Empty,
            HasFreeUploadSlot = jsonElement.GetProperty("hasFreeUploadSlot").GetBoolean(),
            UploadSpeed = jsonElement.GetProperty("uploadSpeed").GetInt64(),
            LockedFileCount = jsonElement.GetProperty("lockedFileCount").GetInt32(),
            LockedFiles = jsonElement.GetProperty("lockedFiles").EnumerateArray()
                .Select(file => file.GetProperty("filename").GetString() ?? string.Empty).ToList()
        };

        public int CalculatePriority()
        {
            if (LockedFileCount > 0)
                return 0;

            double uploadSpeedPriority = Math.Log(UploadSpeed + 1) * 100;
            double freeSlotPenalty = HasFreeUploadSlot ? 0 : -1000;
            return (int)(uploadSpeedPriority + freeSlotPenalty);
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

