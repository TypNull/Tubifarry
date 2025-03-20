using FuzzySharp;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Indexers;
using System.Text.RegularExpressions;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Soulseek
{
    public static class SlskdItemsParser
    {
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(SlskdItemsParser));

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
        private static readonly string[] _nonArtistFolders = new[]
        {
            "music", "mp3", "flac", "audio", "compilations", "soundtracks",
            "pop", "rock", "jazz", "classical", "various", "downloads"
        };

        private static readonly Regex RomanNumeralRegex = new(@"^[IVXLCDM]+$", RegexOptions.Compiled);
        private static readonly Regex VolumeRangeRegex = new(@"(\d+)(?:-|to|\s?&\s?)", RegexOptions.Compiled);
        private static readonly Regex RemoveSpecialCharsRegex = new(@"[^\w\s]", RegexOptions.Compiled);
        private static readonly Regex ReduceWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex RemoveWordsRegex = new(
            string.Join("|", _wordsToRemove.Select(word => $@"\b{word}\b")),
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ArtistAlbumYearPattern = new(
            @"^(?<artist>.+?)\s*-\s*(?<album>.+?)(?:\s*[\(\[]\s*(?<year>(?:19|20)\d{2})\s*[\)\]])?(?:\s*[\(\[].+?[\)\]])*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex YearArtistAlbumPattern = new(
            @"^(?<year>(?:19|20)\d{2})\s*-\s*(?<artist>.+?)\s*-\s*(?<album>.+?)(?:\s*[\(\[].+?[\)\]])*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AlbumYearPattern = new(
            @"^(?<album>.+?)(?:\s*[\(\[]\s*(?<year>(?:19|20)\d{2})\s*[\)\]])?(?:\s*[\(\[].+?[\)\]])*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex YearExtractionRegex = new(
            @"[\(\[\s_](?<year>(?:19|20)\d{2})[\)\]\s_]",
            RegexOptions.Compiled);

        private static readonly Regex VolumeRegex = new(
           @"(?ix)(?:
            # Volume patterns with explicit indicator capture group
            \b(volume|vol|part|pt|chapter|ep|sampler|remix(?:es)?|mix(?:es)?|edition|ed|version|ver|v|release|issue|series|no|num|phase|stage|book|side|disc|cd|dvd|track|season|installment|\#)\b[\s.,\-_:]*(?:\#)?[\s.,\-_:]* |
            # Match numbers at end of string
            (?=\s*\d+$))
            (\d+(?:\.\d+)?|[IVXLCDM]+|\d+(?:-\d+|\s?to\s?\d+|\s?&\s?\d+)?|one|two|three|four|five|six|seven|eight|nine|ten)(?!\w)",
           RegexOptions.Compiled);

        public static SlskdFolderData ParseFolderName(string folderPath)
        {
            string[] pathComponents = SplitPathIntoComponents(folderPath);
            (string? artist, string? album, string? year) = ParseFromRegexPatterns(pathComponents);

            if (string.IsNullOrEmpty(artist) && pathComponents.Length >= 2)
                artist = GetArtistFromParentFolder(pathComponents);
            if (string.IsNullOrEmpty(album) && pathComponents.Length > 0)
                album = CleanComponent(pathComponents[^1]);
            if (string.IsNullOrEmpty(year))
                year = ExtractYearFromPath(folderPath);

            return new SlskdFolderData(
                Path: folderPath,
                Artist: artist ?? "Unknown Artist",
                Album: album ?? "Unknown Album",
                Year: year ?? string.Empty,
                Username: string.Empty,
                HasFreeUploadSlot: false,
                UploadSpeed: 0,
                LockedFileCount: 0,
                LockedFiles: new List<string>()
            );
        }

        public static AlbumData CreateAlbumData(string searchId, IGrouping<string, SlskdFileData> directory, SlskdSearchData searchData, SlskdFolderData folderData, SlskdSettings? settings = null)
        {
            string dirNameNorm = NormalizeString(directory.Key);
            string searchArtistNorm = NormalizeString(searchData.Artist ?? "");
            string searchAlbumNorm = NormalizeString(searchData.Album ?? "");

            Logger.Trace($"Creating album data - Dir: '{dirNameNorm}', Search artist: '{searchArtistNorm}', Search album: '{searchAlbumNorm}'");

            bool isVolumeSearch = !string.IsNullOrEmpty(searchData.Album) && VolumeRegex.Match(searchData.Album).Success;

            bool isAlbumMatch = isVolumeSearch ? CheckVolumeSeriesMatch(directory.Key, searchData.Album) : !string.IsNullOrEmpty(searchAlbumNorm) && (Fuzz.PartialRatio(dirNameNorm, searchAlbumNorm) > 85 || Fuzz.TokenSortRatio(dirNameNorm, searchAlbumNorm) > 80);
            bool isArtistMatch = IsFuzzyArtistMatch(dirNameNorm, searchArtistNorm);

            if (!isArtistMatch && !isAlbumMatch && !string.IsNullOrEmpty(searchData.Artist) && !string.IsNullOrEmpty(searchData.Album))
            {
                string combinedSearch = NormalizeString($"{searchData.Artist} {searchData.Album}");
                isAlbumMatch = Fuzz.PartialRatio(dirNameNorm, combinedSearch) > 85;
            }

            Logger.Debug($"Match results - Artist: {isArtistMatch}, Album: {isAlbumMatch}");

            // Determine final values for artist, album, year
            string finalArtist = DetermineFinalArtist(isArtistMatch, folderData, searchData);
            string finalAlbum = DetermineFinalAlbum(isAlbumMatch, folderData, searchData);
            string finalYear = folderData.Year;

            (AudioFormat Codec, int? BitRate, int? BitDepth, int? SampleRate, long TotalSize, int TotalDuration) audioInfo = AnalyzeAudioQuality(directory);
            string qualityInfo = FormatQualityInfo(audioInfo.Codec, audioInfo.BitRate, audioInfo.BitDepth, audioInfo.SampleRate);

            List<SlskdFileData>? filesToDownload = directory.GroupBy(f => f.Filename?[..f.Filename.LastIndexOf('\\')]).FirstOrDefault(g => g.Key == directory.Key)?.ToList();

            Logger.Trace($"Audio: {audioInfo.Codec}, BitRate: {audioInfo.BitRate}, BitDepth: {audioInfo.BitDepth}, Files: {filesToDownload?.Count ?? 0}");

            string infoUrl = settings != null ? $"{(string.IsNullOrEmpty(settings.ExternalUrl) ? settings.BaseUrl : settings.ExternalUrl)}/searches/{searchId}" : "";

            return new AlbumData("Slskd", nameof(SoulseekDownloadProtocol))
            {
                AlbumId = $"/api/v0/transfers/downloads/{folderData.Username}",
                ArtistName = finalArtist,
                AlbumName = finalAlbum,
                ReleaseDate = finalYear,
                ReleaseDateTime = string.IsNullOrEmpty(finalYear) || !int.TryParse(finalYear, out int yearInt)
                    ? DateTime.MinValue
                    : new DateTime(yearInt, 1, 1),
                Codec = audioInfo.Codec,
                BitDepth = audioInfo.BitDepth ?? 0,
                Bitrate = (audioInfo.Codec == AudioFormat.MP3
                          ? AudioFormatHelper.RoundToStandardBitrate(audioInfo.BitRate ?? 0)
                          : audioInfo.BitRate) ?? 0,
                Size = audioInfo.TotalSize,
                InfoUrl = infoUrl,
                Priotity = folderData.CalculatePriority(),
                CustomString = JsonConvert.SerializeObject(filesToDownload),
                ExtraInfo = $"User: {folderData.Username}",
                Duration = audioInfo.TotalDuration
            };
        }

        private static string[] SplitPathIntoComponents(string path) => path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

        private static (string? artist, string? album, string? year) ParseFromRegexPatterns(string[] pathComponents)
        {
            if (pathComponents.Length == 0)
                return (null, null, null);

            string lastComponent = pathComponents[^1];

            // Try artist-album-year pattern
            Match? match = TryMatchRegex(lastComponent, ArtistAlbumYearPattern);
            if (match != null)
            {
                return (
                    match.Groups["artist"].Success ? match.Groups["artist"].Value.Trim() : null,
                    match.Groups["album"].Success ? match.Groups["album"].Value.Trim() : null,
                    match.Groups["year"].Success ? match.Groups["year"].Value.Trim() : null);
            }

            // Try year-artist-album pattern
            match = TryMatchRegex(lastComponent, YearArtistAlbumPattern);
            if (match != null)
            {
                return (
                    match.Groups["artist"].Success ? match.Groups["artist"].Value.Trim() : null,
                    match.Groups["album"].Success ? match.Groups["album"].Value.Trim() : null,
                    match.Groups["year"].Success ? match.Groups["year"].Value.Trim() : null);
            }

            // Try album-year pattern
            match = TryMatchRegex(lastComponent, AlbumYearPattern);
            if (match?.Groups["album"].Success == true)
            {
                string? artist = null;
                if (pathComponents.Length >= 2)
                    artist = GetArtistFromParentFolder(pathComponents);

                return (artist,
                    match.Groups["album"].Value.Trim(),
                    match.Groups["year"].Success ? match.Groups["year"].Value.Trim() : null);
            }

            return (null, null, null);
        }

        private static string? GetArtistFromParentFolder(string[] pathComponents)
        {
            if (pathComponents.Length < 2) return null;
            string parentFolder = pathComponents[^2];
            if (!_nonArtistFolders.Contains(parentFolder.ToLowerInvariant()))
                return parentFolder;

            return null;
        }

        private static bool CheckVolumeSeriesMatch(string directoryPath, string? searchAlbum)
        {
            if (string.IsNullOrEmpty(searchAlbum))
                return false;

            bool isVolumeSeries = VolumeRegex.Match(searchAlbum).Success;
            if (!isVolumeSeries)
                return false;

            Match? searchMatch = VolumeRegex.Match(searchAlbum);
            Match dirMatch = VolumeRegex.Match(directoryPath);

            if (!dirMatch.Success)
                return false;

            string? normSearchVol = NormalizeVolume(searchMatch.Groups[2].Value);
            string? normDirVol = NormalizeVolume(dirMatch.Groups[2].Value);

            string? searchBaseAlbum = VolumeRegex.Replace(searchAlbum, "").Trim();
            string? dirBaseAlbum = VolumeRegex.Replace(directoryPath, "").Trim();

            bool baseAlbumMatch = Fuzz.PartialRatio(
                NormalizeString(dirBaseAlbum),
                NormalizeString(searchBaseAlbum)) > 85;

            return baseAlbumMatch && normSearchVol == normDirVol;
        }

        public static string NormalizeVolume(string volume)
        {
            Logger.Trace($"Normalizing volume: '{volume}'");

            if (_textNumbers.TryGetValue(volume, out string? textNum))
                return textNum;
            if (int.TryParse(volume, out int num))
                return num.ToString();

            if (!string.IsNullOrEmpty(volume))
            {
                string roman = NormalizeRomanNumeral(volume);
                if (RomanNumeralRegex.IsMatch(roman))
                {
                    int value = ConvertRomanToNumber(roman);
                    if (value > 0 && value < 5000)
                        return value.ToString();
                }
            }
            Match rangeMatch = VolumeRangeRegex.Match(volume);
            if (rangeMatch.Success && int.TryParse(rangeMatch.Groups[1].Value, out int firstNum))
                return firstNum.ToString();
            return volume.Trim().ToUpper();
        }

        private static string NormalizeRomanNumeral(string roman) => roman.Trim().ToUpper()
                .Replace("IIII", "IV").Replace("VIIII", "IX")
                .Replace("XXXX", "XL").Replace("LXXXX", "XC")
                .Replace("CCCC", "CD").Replace("DCCCC", "CM");

        private static int ConvertRomanToNumber(string roman)
        {
            int total = 0, prev = 0;
            foreach (char c in roman.Reverse())
            {
                int current = _romanNumerals[c];
                total += (current < prev) ? -current : current;
                prev = current;
            }
            return total;
        }

        public static string NormalizeString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            Logger.Trace($"Normalizing: '{input}'");

            string normalized = RemoveSpecialCharsRegex.Replace(input, " ");
            normalized = ReduceWhitespaceRegex.Replace(normalized, " ").Trim().ToLowerInvariant();
            normalized = RemoveWordsRegex.Replace(normalized, " ");
            return ReduceWhitespaceRegex.Replace(normalized, " ").Trim();
        }

        private static string CleanComponent(string component)
        {
            if (string.IsNullOrEmpty(component)) return string.Empty;

            component = Regex.Replace(component, @"\[(FLAC|MP3|320|WEB|CD).*?\]", "", RegexOptions.IgnoreCase);
            component = Regex.Replace(component, @"\(\d{5,}\)", ""); // Catalog numbers
            component = Regex.Replace(component, @"\(\d+bit\/\d+.*?\)", "", RegexOptions.IgnoreCase); // Bit depth / sample rate
            component = Regex.Replace(component, @"\(DELUXE_EDITION\)", "", RegexOptions.IgnoreCase); // Edition info
            component = Regex.Replace(component, @"\(Album\)", "", RegexOptions.IgnoreCase); // Album indicator
            component = Regex.Replace(component, @"\(Single\)", "", RegexOptions.IgnoreCase); // Single indicator

            return component.Trim();
        }

        private static string? ExtractYearFromPath(string path)
        {
            Match yearMatch = YearExtractionRegex.Match(path);
            return yearMatch.Success ? yearMatch.Groups["year"].Value : null;
        }

        private static Match? TryMatchRegex(string input, Regex regex)
        {
            Match match = regex.Match(input);
            return match.Success ? match : null;
        }

        private static bool IsFuzzyArtistMatch(string dirNameNorm, string searchArtistNorm) =>
            !string.IsNullOrEmpty(searchArtistNorm) &&
            (Fuzz.PartialRatio(dirNameNorm, searchArtistNorm) > 90 || Fuzz.TokenSortRatio(dirNameNorm, searchArtistNorm) > 85);

        private static bool IsFuzzyAlbumMatch(string dirNameNorm, string searchAlbumNorm, bool volumeMatch) =>
            !string.IsNullOrEmpty(searchAlbumNorm) &&
            (volumeMatch || Fuzz.PartialRatio(dirNameNorm, searchAlbumNorm) > 85 || Fuzz.TokenSortRatio(dirNameNorm, searchAlbumNorm) > 80);


        private static string DetermineFinalArtist(bool isArtistMatch, SlskdFolderData folderData, SlskdSearchData searchData)
        {
            if (isArtistMatch && !string.IsNullOrEmpty(searchData.Artist))
                return searchData.Artist;
            if (!string.IsNullOrEmpty(folderData.Artist))
                return folderData.Artist;
            return searchData.Artist ?? "Unknown Artist";
        }

        private static string DetermineFinalAlbum(bool isAlbumMatch, SlskdFolderData folderData, SlskdSearchData searchData)
        {
            if (isAlbumMatch && !string.IsNullOrEmpty(searchData.Album))
                return searchData.Album;
            if (!string.IsNullOrEmpty(folderData.Album))
                return folderData.Album;
            return searchData.Album ?? "Unknown Album";
        }

        private static (AudioFormat Codec, int? BitRate, int? BitDepth, int? SampleRate, long TotalSize, int TotalDuration) AnalyzeAudioQuality(IGrouping<string, SlskdFileData> directory)
        {
            string? commonExt = GetMostCommonExtension(directory);
            long totalSize = directory.Sum(f => f.Size);
            int totalDuration = directory.Sum(f => f.Length ?? 0);

            int? commonBitRate = directory.GroupBy(f => f.BitRate).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            int? commonBitDepth = directory.GroupBy(f => f.BitDepth).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            int? commonSampleRate = directory.GroupBy(f => f.SampleRate).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

            if (!commonBitRate.HasValue && totalDuration > 0)
            {
                commonBitRate = (int)(totalSize * 8 / (totalDuration * 1000));
                Logger.Trace($"Calculated bitrate: {commonBitRate}");
            }

            AudioFormat codec = AudioFormatHelper.GetAudioCodecFromExtension(commonExt ?? "");

            return (codec, commonBitRate, commonBitDepth, commonSampleRate, totalSize, totalDuration);
        }

        public static string? GetMostCommonExtension(IEnumerable<SlskdFileData> files)
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

        private static string FormatQualityInfo(AudioFormat codec, int? bitRate, int? bitDepth, int? sampleRate)
        {
            if (codec == AudioFormat.MP3 && bitRate.HasValue)
                return $"{codec} {bitRate}kbps";

            if (bitDepth.HasValue && sampleRate.HasValue)
                return $"{codec} {bitDepth}bit/{sampleRate / 1000}kHz";

            return codec.ToString();
        }
    }
}