using FuzzySharp;
using Newtonsoft.Json;
using NLog;
using System.Text.RegularExpressions;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Soulseek
{
    public interface ISlskdItemsParser
    {
        AlbumData CreateAlbumData(string searchId, IGrouping<string, SlskdFileData> directory, SlskdSearchData searchData, SlskdFolderData folderData, SlskdSettings settings);
        string NormalizeString(string input);
        string NormalizeVolume(string volume);
        string? GetMostCommonExtension(IEnumerable<SlskdFileData> files);
    }

    public class SlskdItemsParser : ISlskdItemsParser
    {
        private readonly Logger _logger;

        private static readonly Dictionary<string, string> _textNumbers = new(StringComparer.OrdinalIgnoreCase)
        {
            { "one", "1" }, { "two", "2" }, { "three", "3" }, { "four", "4" },
            { "five", "5" }, { "six", "6" }, { "seven", "7" }, { "eight", "8" },
            { "nine", "9" }, { "ten", "10" }
        };

        private static readonly Dictionary<char, int> _romanNumerals = new()
        {
            { 'I', 1 }, { 'V', 5 }, { 'X', 10 }, { 'L', 50 },
            { 'C', 100 }, { 'D', 500 }, { 'M', 1000 }
        };

        private static readonly string[] _wordsToRemove =
        {
            "the", "a", "an", "feat", "featuring", "ft",
            "presents", "pres", "with", "and"
        };

        private static readonly Regex RomanNumeralRegex = new(@"^[IVXLCDM]+$", RegexOptions.Compiled);
        private static readonly Regex VolumeRangeRegex = new(@"(\d+)(?:-|to|\s?&\s?)", RegexOptions.Compiled);
        private static readonly Regex RemoveSpecialCharsRegex = new(@"[^\w\s]", RegexOptions.Compiled);
        private static readonly Regex ReduceWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex RemoveWordsRegex = new(
            string.Join("|", _wordsToRemove.Select(word => $@"\b{word}\b")),
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Regex VolumeRegex = new(
           @"(?ix)
            (?:
                # Volume patterns with explicit indicator capture group
                \b(volume|vol|part|pt|chapter|ep|sampler|remix(?:es)?|mix(?:es)?|edition|ed|version|ver|v|release|issue|series|no|num|phase|stage|book|side|disc|cd|dvd|track|season|installment|\#)\b[\s.,\-_:]*(?:\#)?[\s.,\-_:]* |
                # Match numbers at end of string
                (?=\s*\d+$)
            )
            (\d+(?:\.\d+)?|[IVXLCDM]+|\d+(?:-\d+|\s?to\s?\d+|\s?&\s?\d+)?|one|two|three|four|five|six|seven|eight|nine|ten)(?!\w)",
           RegexOptions.Compiled);

        public SlskdItemsParser(Logger logger)
        {
            _logger = logger;
        }

        public AlbumData CreateAlbumData(string searchId, IGrouping<string, SlskdFileData> directory, SlskdSearchData searchData, SlskdFolderData folderData, SlskdSettings? settings = null)
        {
            string hash = $"{folderData.Username} {directory.Key}".GetHashCode().ToString("X");
            string dirNameNorm = NormalizeString(directory.Key);
            string searchArtistNorm = NormalizeString(searchData.Artist ?? "");
            string searchAlbumNorm = NormalizeString(searchData.Album ?? "");

            bool isVolumeSeries = VolumeRegex.Match(searchData.Album ?? "").Success;
            Match? searchMatch = isVolumeSeries ? VolumeRegex.Match(searchData.Album!) : null;
            Match dirMatch = VolumeRegex.Match(directory.Key);

            string? normSearchVol = isVolumeSeries ? NormalizeVolume(searchMatch!.Groups[2].Value) : null;
            string? normDirVol = dirMatch.Success ? NormalizeVolume(dirMatch.Groups[2].Value) : null;
            string? searchBaseAlbum = isVolumeSeries ? VolumeRegex.Replace(searchData.Album!, "").Trim() : searchData.Album;
            string? dirBaseAlbum = dirMatch.Success ? VolumeRegex.Replace(directory.Key, "").Trim() : directory.Key;

            _logger.Trace($"Matching - Dir: '{dirNameNorm}', Artist: '{searchArtistNorm}', Album: '{searchAlbumNorm}'");

            if (isVolumeSeries)
            {
                _logger.Trace($"Volume series - Search vol: '{normSearchVol}', Dir vol: '{normDirVol}'");
            }

            bool isArtistMatch = !string.IsNullOrEmpty(searchData.Artist) &&
                (Fuzz.PartialRatio(dirNameNorm, searchArtistNorm) > 80 ||
                 Fuzz.TokenSortRatio(dirNameNorm, searchArtistNorm) > 75);

            bool isAlbumMatch = isVolumeSeries && normSearchVol != null && normDirVol != null
                ? Fuzz.PartialRatio(NormalizeString(dirBaseAlbum!), NormalizeString(searchBaseAlbum!)) > 80 &&
                  normSearchVol == normDirVol
                : !string.IsNullOrEmpty(searchData.Album) &&
                  (Fuzz.PartialRatio(dirNameNorm, searchAlbumNorm) > 80 ||
                   Fuzz.TokenSortRatio(dirNameNorm, searchAlbumNorm) > 75);

            if (!isArtistMatch && !isAlbumMatch && !string.IsNullOrEmpty(searchData.Artist) && !string.IsNullOrEmpty(searchData.Album))
            {
                string combinedSearch = NormalizeString($"{searchData.Artist} {searchData.Album}");
                isAlbumMatch = Fuzz.PartialRatio(dirNameNorm, combinedSearch) > 85;
            }

            _logger.Debug($"Match results - Artist: {isArtistMatch}, Album: {isAlbumMatch}");

            string? artist = isArtistMatch ? searchData.Artist : folderData.Artist ?? searchData.Artist;
            string? album = isAlbumMatch ? searchData.Album : folderData.Album ?? searchData.Album;

            string? commonExt = GetMostCommonExtension(directory);
            long totalSize = directory.Sum(f => f.Size);
            int totalDuration = directory.Sum(f => f.Length ?? 0);
            int? commonBitRate = directory.GroupBy(f => f.BitRate).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            int? commonBitDepth = directory.GroupBy(f => f.BitDepth).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

            if (!commonBitRate.HasValue && totalDuration > 0)
            {
                commonBitRate = (int)(totalSize * 8 / (totalDuration * 1000));
                _logger.Trace($"Calculated bitrate: {commonBitRate}");
            }

            List<SlskdFileData>? filesToDownload = directory.GroupBy(f => f.Filename?[..f.Filename.LastIndexOf('\\')])
                                       .FirstOrDefault(g => g.Key == directory.Key)?.ToList();

            AudioFormat codec = AudioFormatHelper.GetAudioCodecFromExtension(commonExt ?? "");
            _logger.Debug($"Audio: {codec}, BitRate: {commonBitRate}, BitDepth: {commonBitDepth}, Files: {filesToDownload?.Count ?? 0}");

            string infoUrl = settings != null ? $"{(string.IsNullOrEmpty(settings.ExternalUrl) ? settings.BaseUrl : settings.ExternalUrl)}/searches/{searchId}" : "";

            return new("Slskd")
            {
                AlbumId = $"/api/v0/transfers/downloads/{folderData.Username}",
                ArtistName = artist ?? "Unknown Artist",
                AlbumName = album ?? "Unknown Album",
                ReleaseDate = folderData.Year,
                ReleaseDateTime = string.IsNullOrEmpty(folderData.Year) || !int.TryParse(folderData.Year, out int yearInt)
                    ? DateTime.MinValue
                    : new DateTime(yearInt, 1, 1),
                Codec = codec,
                BitDepth = commonBitDepth ?? 0,
                Bitrate = (codec == AudioFormat.MP3 ? AudioFormatHelper.RoundToStandardBitrate(commonBitRate ?? 0) : commonBitRate) ?? 0,
                Size = totalSize,
                InfoUrl = infoUrl,
                Priotity = folderData.CalculatePriority(),
                CustomString = JsonConvert.SerializeObject(filesToDownload),
                ExtraInfo = $"User: {folderData.Username}",
                Duration = totalDuration
            };
        }

        public string NormalizeString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            _logger.Trace($"Normalizing: '{input}'");
            string normalized = RemoveSpecialCharsRegex.Replace(input, " ");
            normalized = ReduceWhitespaceRegex.Replace(normalized, " ").Trim().ToLowerInvariant();
            normalized = RemoveWordsRegex.Replace(normalized, " ");
            return ReduceWhitespaceRegex.Replace(normalized, " ").Trim();
        }

        public string NormalizeVolume(string volume)
        {
            _logger.Trace($"Normalizing volume: '{volume}'");

            if (_textNumbers.TryGetValue(volume, out string? textNum))
                return textNum;

            if (int.TryParse(volume, out int num))
                return num.ToString();

            if (!string.IsNullOrEmpty(volume))
            {
                string roman = volume.Trim().ToUpper()
                    .Replace("IIII", "IV").Replace("VIIII", "IX")
                    .Replace("XXXX", "XL").Replace("LXXXX", "XC")
                    .Replace("CCCC", "CD").Replace("DCCCC", "CM");

                if (RomanNumeralRegex.IsMatch(roman))
                {
                    int total = 0, prev = 0;
                    foreach (char c in roman.Reverse())
                    {
                        int current = _romanNumerals[c];
                        total += (current < prev) ? -current : current;
                        prev = current;
                    }

                    if (total > 0 && total < 5000)
                        return total.ToString();
                }
            }

            Match rangeMatch = VolumeRangeRegex.Match(volume);
            if (rangeMatch.Success && int.TryParse(rangeMatch.Groups[1].Value, out int firstNum))
                return firstNum.ToString();

            return volume.Trim().ToUpper();
        }

        public string? GetMostCommonExtension(IEnumerable<SlskdFileData> files)
        {
            List<string?> extensions = files
                .Select(f => string.IsNullOrEmpty(f.Extension)
                    ? Path.GetExtension(f.Filename)?.TrimStart('.').ToLowerInvariant()
                    : f.Extension)
                .Where(ext => !string.IsNullOrEmpty(ext))
                .ToList();

            if (!extensions.Any())
                return null;

            return extensions
                .GroupBy(ext => ext)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
        }
    }
}