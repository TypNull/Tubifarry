using System.Text.Json;

namespace NzbDrone.Core.Indexers.Soulseek
{
    public class SlskdFileData
    {
        public string? Filename { get; }
        public int? BitRate { get; }
        public int? BitDepth { get; }
        public long Size { get; }
        public int? Length { get; }
        public string? Extension { get; }

        public SlskdFileData(string? filename, int? bitRate, int? bitDepth, long size, int? length, string? extension)
        {
            Filename = filename;
            BitRate = bitRate;
            BitDepth = bitDepth;
            Size = size;
            Length = length;
            Extension = extension;
        }

        public static IEnumerable<SlskdFileData> GetFiles(JsonElement filesElement)
        {
            if (filesElement.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (JsonElement file in filesElement.EnumerateArray())
            {
                string? filename = file.GetProperty("filename").GetString();
                int? bitRate = file.TryGetProperty("bitRate", out JsonElement bitRateElement) ? bitRateElement.GetInt32() : null;
                int? bitDepth = file.TryGetProperty("bitDepth", out JsonElement bitDepthElement) ? bitDepthElement.GetInt32() : null;
                long size = file.GetProperty("size").GetInt64();
                int? length = file.TryGetProperty("length", out JsonElement lengthElement) ? lengthElement.GetInt32() : null;

                string? extension = file.TryGetProperty("extension", out JsonElement extensionElement)
                    ? extensionElement.GetString()
                    : Path.GetExtension(filename)?.TrimStart('.').ToLowerInvariant();

                yield return new SlskdFileData(filename, bitRate, bitDepth, size, length, extension);
            }
        }
    }
    public class SlskdFolderData
    {
        public string Artist { get; }
        public string Album { get; }
        public string Year { get; }

        public SlskdFolderData(string artist, string album, string year)
        {
            Artist = artist;
            Album = album;
            Year = year;
        }

        public static SlskdFolderData ParseFolderName(string folderName)
        {
            string[] parts = folderName.Split(new[] { '-', ' ', '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

            string artist = parts.Length > 0 ? parts[0].Replace("_", " ") : "Unknown Artist";
            string album = parts.Length > 1 ? parts[1].Replace("_", " ") : "Unknown Album";
            string year = parts.FirstOrDefault(p => p.Length == 4 && p.All(char.IsDigit)) ?? string.Empty;

            return new SlskdFolderData(artist, album, year);
        }
    }

    public class SlskdSearchTextData
    {
        public string? Artist { get; }
        public string? Album { get; }

        public SlskdSearchTextData(string? artist, string? album)
        {
            Artist = artist;
            Album = album;
        }

        public static SlskdSearchTextData ParseSearchText(string searchText)
        {
            string[] parts = searchText.Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries);
            string? album = parts.Length > 0 ? parts[0].Trim() : null;
            string? artist = parts.Length > 1 ? parts[1].Trim() : null;
            return new SlskdSearchTextData(artist, album);
        }
    }
}

