using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tubifarry.Core;

namespace NzbDrone.Core.Indexers.Soulseek
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
                string? extension = (!file.TryGetProperty("extension", out JsonElement extensionElement) || string.IsNullOrWhiteSpace(extensionElement.GetString())) ? Path.GetExtension(filename) : extensionElement.GetString();

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
        private static readonly Regex ArtistAlbumYearPattern = new(
            @"^(?<artist>.+?)\s*-\s*(?<album>.+?)\s*(\((?<year>\d{4})\))?\s*(\[.*\])?$",
            RegexOptions.IgnoreCase); // Matches: Artist - Album (Year) [Metadata]

        private static readonly Regex YearArtistAlbumPattern = new(
            @"^(?<year>\d{4})\s*-\s*(?<artist>.+?)\s*-\s*(?<album>.+?)$",
            RegexOptions.IgnoreCase); // Matches: Year - Artist - Album

        private static readonly Regex AlbumYearPattern = new(
            @"^(?<album>.+?)\s*(\((?<year>\d{4})\))?\s*(\[.*\])?$",
            RegexOptions.IgnoreCase); // Matches: Album (Year) [Metadata]

        public static SlskdFolderData ParseFolderName(string folderName)
        {
            string folder = System.IO.Path.GetFileName(folderName);

            Match? match = TryMatchRegex(folder, ArtistAlbumYearPattern) ?? TryMatchRegex(folder, YearArtistAlbumPattern) ?? TryMatchRegex(folder, AlbumYearPattern);

            string artist = match?.Groups["artist"].Value.Trim() ?? GetFallbackArtist(folderName, folder);
            string album = match?.Groups["album"].Value.Trim() ?? folder.Replace("_", " ").Trim();
            string year = match?.Groups["year"].Value.Trim() ?? string.Empty;

            return new SlskdFolderData(
                Path: folderName,
                Artist: artist,
                Album: album,
                Year: year,
                Username: string.Empty,
                HasFreeUploadSlot: false,
                UploadSpeed: 0,
                LockedFileCount: 0,
                LockedFiles: new List<string>()
            );
        }

        private static Match? TryMatchRegex(string input, Regex regex)
        {
            Match match = regex.Match(input);
            return match.Success ? match : null;
        }

        private static string GetFallbackArtist(string folderName, string folder)
        {
            string[] folders = folderName.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (folders.Length >= 2)
                return folders[^2].Replace("_", " ").Trim();
            return folder;
        }

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
            double freeSlotPenalty = HasFreeUploadSlot ? 0 : -200;
            return (int)Math.Clamp(uploadSpeedPriority + freeSlotPenalty, 0, 1000);
        }
    }

    public record SlskdSearchData(string? Artist, string? Album)
    {
        public static SlskdSearchData ParseSearchText(IndexerRequest request) => new(
            Artist: Encoding.UTF8.GetString(Convert.FromBase64String(request.HttpRequest.Headers["X-ARTIST"] ?? "")),
            Album: Encoding.UTF8.GetString(Convert.FromBase64String(request.HttpRequest.Headers["X-ALBUM"] ?? ""))
        );
    }
}

