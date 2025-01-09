using System.Text.Json;

namespace NzbDrone.Core.Indexers.Soulseek
{
    public record SlskdFileData(string? Filename, int? BitRate, int? BitDepth, long Size, int? Length, string? Extension, int? SampleRate, int Code, bool IsLocked)
    {
        public static IEnumerable<SlskdFileData> GetFiles(JsonElement filesElement)
        {
            if (filesElement.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (JsonElement file in filesElement.EnumerateArray())
            {
                yield return new SlskdFileData(
                    Filename: file.GetProperty("filename").GetString(),
                    BitRate: file.TryGetProperty("bitRate", out JsonElement bitRateElement) ? bitRateElement.GetInt32() : null,
                    BitDepth: file.TryGetProperty("bitDepth", out JsonElement bitDepthElement) ? bitDepthElement.GetInt32() : null,
                    Size: file.GetProperty("size").GetInt64(),
                    Length: file.TryGetProperty("length", out JsonElement lengthElement) ? lengthElement.GetInt32() : null,
                    Extension: file.TryGetProperty("extension", out JsonElement extensionElement) ? extensionElement.GetString() : Path.GetExtension(file.GetProperty("filename").GetString())?.TrimStart('.').ToLowerInvariant(),
                    SampleRate: file.TryGetProperty("sampleRate", out JsonElement sampleRateElement) ? sampleRateElement.GetInt32() : null,
                    Code: file.TryGetProperty("code", out JsonElement codeElement) ? codeElement.GetInt32() : 1,
                    IsLocked: file.TryGetProperty("isLocked", out JsonElement isLockedElement) && isLockedElement.GetBoolean()
                );
            }
        }
    }

    public record SlskdFolderData(string Path, string Artist, string Album, string Year)
    {
        public static SlskdFolderData ParseFolderName(string folderName)
        {
            string[] parts = folderName.Split(new[] { '-', ' ', '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return new SlskdFolderData(
                Path: folderName,
                Artist: parts.Length > 0 ? parts[0].Replace("_", " ") : "Unknown Artist",
                Album: parts.Length > 1 ? parts[1].Replace("_", " ") : "Unknown Album",
                Year: parts.FirstOrDefault(p => p.Length == 4 && p.All(char.IsDigit)) ?? string.Empty
            );
        }
    }

    public record SlskdSearchTextData(string? Artist, string? Album)
    {
        public static SlskdSearchTextData ParseSearchText(string searchText)
        {
            string[] parts = searchText.Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries);
            return new SlskdSearchTextData(
                Artist: parts.Length > 1 ? parts[1].Trim() : null,
                Album: parts.Length > 0 ? parts[0].Trim() : null
            );
        }
    }
}

