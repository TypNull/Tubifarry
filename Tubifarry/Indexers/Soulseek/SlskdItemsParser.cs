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
    public static partial class SlskdItemsParser
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

        private static readonly string[] _nonArtistFolders =
        [
            "music", "mp3", "flac", "audio", "compilations", "soundtracks",
            "pop", "rock", "jazz", "classical", "various", "downloads"
        ];

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
                LockedFiles: [],
                QueueLength: 0,
                Token: 0,
                FileCount: 0,
                Files: []);
        }

        public static AlbumData CreateAlbumData(string searchId, IGrouping<string, SlskdFileData> directory, SlskdSearchData searchData, SlskdFolderData folderData, SlskdSettings? settings = null, int expectedTrackCount = 0)
        {
            string dirNameNorm = NormalizeString(directory.Key);
            string searchArtistNorm = NormalizeString(searchData.Artist ?? "");
            string searchAlbumNorm = NormalizeString(searchData.Album ?? "");

            Logger.Trace($"Creating album data - Dir: '{dirNameNorm}', Search artist: '{searchArtistNorm}', Search album: '{searchAlbumNorm}'");

            bool isVolumeSearch = !string.IsNullOrEmpty(searchData.Album) && VolumeRegex().Match(searchData.Album).Success;

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

            (AudioFormat Codec, int? BitRate, int? BitDepth, int? SampleRate, long TotalSize, int TotalDuration) = AnalyzeAudioQuality(directory);
            string qualityInfo = FormatQualityInfo(Codec, BitRate, BitDepth, SampleRate);

            List<SlskdFileData>? filesToDownload = directory.GroupBy(f => f.Filename?[..f.Filename.LastIndexOf('\\')]).FirstOrDefault(g => g.Key == directory.Key)?.ToList();

            Logger.Trace($"Audio: {Codec}, BitRate: {BitRate}, BitDepth: {BitDepth}, Files: {filesToDownload?.Count ?? 0}");

            string infoUrl = settings != null ? $"{(string.IsNullOrEmpty(settings.ExternalUrl) ? settings.BaseUrl : settings.ExternalUrl)}/searches/{searchId}" : "";
            string? edition = ExtractEdition(folderData.Path)?.ToUpper();

            return new AlbumData("Slskd", nameof(SoulseekDownloadProtocol))
            {
                AlbumId = $"/api/v0/transfers/downloads/{folderData.Username}",
                ArtistName = finalArtist,
                AlbumName = finalAlbum,
                ReleaseDate = finalYear,
                ReleaseDateTime = string.IsNullOrEmpty(finalYear) || !int.TryParse(finalYear, out int yearInt)
                    ? DateTime.MinValue
                    : new DateTime(yearInt, 1, 1),
                Codec = Codec,
                BitDepth = BitDepth ?? 0,
                Bitrate = (Codec == AudioFormat.MP3
                          ? AudioFormatHelper.RoundToStandardBitrate(BitRate ?? 0)
                          : BitRate) ?? 0,
                Size = TotalSize,
                InfoUrl = infoUrl,
                ExplicitContent = ExtractExplicitTag(folderData.Path),
                Priotity = folderData.CalculatePriority(expectedTrackCount),
                CustomString = JsonConvert.SerializeObject(filesToDownload),
                ExtraInfo = [edition, $"👤 {folderData.Username} ", $"{(folderData.HasFreeUploadSlot ? "⚡" : "❌")} {folderData.UploadSpeed / 1024.0 / 1024.0:F2}MB/s ", folderData.QueueLength == 0 ? "" : $"📋 {folderData.QueueLength}"],
                Duration = TotalDuration
            };
        }

        private static string[] SplitPathIntoComponents(string path) => path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        private static (string? artist, string? album, string? year) ParseFromRegexPatterns(string[] pathComponents)
        {
            if (pathComponents.Length == 0)
                return (null, null, null);

            string lastComponent = pathComponents[^1];

            // Try artist-album-year pattern
            Match? match = TryMatchRegex(lastComponent, ArtistAlbumYearRegex());
            if (match != null)
            {
                return (
                    match.Groups["artist"].Success ? match.Groups["artist"].Value.Trim() : null,
                    match.Groups["album"].Success ? match.Groups["album"].Value.Trim() : null,
                    match.Groups["year"].Success ? match.Groups["year"].Value.Trim() : null);
            }

            // Try year-artist-album pattern
            match = TryMatchRegex(lastComponent, YearArtistAlbumRegex());
            if (match != null)
            {
                return (
                    match.Groups["artist"].Success ? match.Groups["artist"].Value.Trim() : null,
                    match.Groups["album"].Success ? match.Groups["album"].Value.Trim() : null,
                    match.Groups["year"].Success ? match.Groups["year"].Value.Trim() : null);
            }

            // Try album-year pattern
            match = TryMatchRegex(lastComponent, AlbumYearRegex());
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

            bool isVolumeSeries = VolumeRegex().Match(searchAlbum).Success;
            if (!isVolumeSeries)
                return false;

            Match? searchMatch = VolumeRegex().Match(searchAlbum);
            Match dirMatch = VolumeRegex().Match(directoryPath);

            if (!dirMatch.Success)
                return false;

            string? normSearchVol = NormalizeVolume(searchMatch.Value);
            string? normDirVol = NormalizeVolume(dirMatch.Value);

            string? searchBaseAlbum = VolumeRegex().Replace(searchAlbum, "").Trim();
            string? dirBaseAlbum = VolumeRegex().Replace(directoryPath, "").Trim();

            bool baseAlbumMatch = Fuzz.PartialRatio(
                NormalizeString(dirBaseAlbum),
                NormalizeString(searchBaseAlbum)) > 85;

            return baseAlbumMatch && normSearchVol.Equals(normDirVol, StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeVolume(string volume)
        {
            Logger.Trace($"Normalizing volume: '{volume}'");

            if (_textNumbers.TryGetValue(volume, out string? textNum))
                return textNum;
            if (int.TryParse(volume, out int num))
                return num.ToString();

            string normalizedRoman = NormalizeRomanNumeral(volume);
            if (RomanNumeralRegex().IsMatch(normalizedRoman))
            {
                int value = ConvertRomanToNumber(normalizedRoman);
                if (value > 0)
                    return value.ToString();
            }
            Match rangeMatch = VolumeRangeRegex().Match(volume);
            if (rangeMatch.Success && int.TryParse(rangeMatch.Groups[1].Value, out int firstNum))
                return firstNum.ToString();
            return volume.Trim().ToUpperInvariant();
        }

        private static string NormalizeRomanNumeral(string roman)
        {
            if (string.IsNullOrEmpty(roman))
                return string.Empty;

            return roman.Trim().ToUpperInvariant()
                .Replace("IIII", "IV")    // 4
                .Replace("VIIII", "IX")   // 9
                .Replace("XXXX", "XL")    // 40
                .Replace("LXXXX", "XC")   // 90
                .Replace("CCCC", "CD")    // 400
                .Replace("DCCCC", "CM");  // 900
        }

        private static int ConvertRomanToNumber(string roman)
        {
            roman = roman.ToUpperInvariant();
            int total = 0;
            int prevValue = 0;

            for (int i = roman.Length - 1; i >= 0; i--)
            {
                if (!_romanNumerals.TryGetValue(roman[i], out int currentValue))
                    return 0;

                if (currentValue < prevValue)
                    total -= currentValue;
                else
                    total += currentValue;

                prevValue = currentValue;
            }

            if (total <= 0 || total > 5000)
                return 0;

            Logger.Trace($"Roman numeral '{roman}' converted to: {total}");
            return total;
        }

        public static string NormalizeString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;
            string normalized = NormalizeCharactersRegex().Replace(input, " ");
            normalized = RemoveNonAlphanumericRegex().Replace(normalized, "");
            normalized = ReduceWhitespaceRegex().Replace(normalized.ToLowerInvariant(), " ").Trim();
            normalized = RemoveWordsRegex().Replace(normalized, "");
            return ReduceWhitespaceRegex().Replace(normalized, " ").Trim();
        }


        private static string CleanComponent(string component)
        {
            if (string.IsNullOrEmpty(component))
                return string.Empty;
            component = CleanComponentRegex().Replace(component, "");
            return ReduceWhitespaceRegex().Replace(component.Trim(), " ");
        }

        private static bool ExtractExplicitTag(string path)
        {
            Match match = ExplicitTagRegex().Match(path);
            if (match.Success)
            {
                if (match.Groups["negation"].Success && !string.IsNullOrWhiteSpace(match.Groups["negation"].Value))
                {
                    Logger.Trace($"Found negated explicit tag in path, skipping: {match.Value}");
                    return false;
                }

                Logger.Trace($"Extracted explicit tag from path: {path}");
                return true;
            }
            return false;
        }

        private static string? ExtractEdition(string path)
        {
            Match match = EditionRegex().Match(path);
            return match.Success ? match.Groups["edition"].Value.Trim() : null;
        }

        private static string? ExtractYearFromPath(string path)
        {
            Match yearMatch = YearExtractionRegex().Match(path);
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
            {
                Match folderVersion = VolumeRegex().Match(folderData.Album ?? "");
                Match searchVersion = VolumeRegex().Match(searchData.Album);
                return folderVersion.Success && !searchVersion.Success ? $"{searchData.Album} {folderVersion.Value}" : searchData.Album;
            }
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

            if (extensions.Count == 0)
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


        [GeneratedRegex(@"(?ix)
            \[(?:FLAC|MP3|320|WEB|CD)[^\]]*\]|           # Audio format tags
            \(\d{5,}\)|                                  # Long numbers in parentheses
            \(\d+bit[\/\s]\d+[^\)]*\)|                   # Bit depth/sample rate
            \((?:DELUXE_)?EDITION\)|                     # Edition markers
            \s*\([^)]*edition[^)]*\)|                    # Any edition in parentheses
            \((?:Album|Single|EP|LP)\)|                  # Release type
            \s*\(remaster(?:ed)?\)|                      # Remaster tags
            \s*[\(\[][^)\]]*(?:version|reissue)[^)\]]*[\)\]]| # Version/reissue in brackets/parens
            \s*\d{4}\s*remaster|                        # Year remaster
            \s-\s.*$", RegexOptions.ExplicitCapture | RegexOptions.Compiled, "de-DE")]
        private static partial Regex CleanComponentRegex();

        [GeneratedRegex(@"(?ix)
            (?<=\b(?:volume|vol|part|pt|chapter|ep|sampler|remix(?:es)?|mix(?:es)?|edition|ed|version|ver|v|release|issue|series|no|num|phase|stage|book|side|disc|cd|dvd|track|season|installment|\#)\s*[.,\-_:#]*\s*)
            (\d+(?:\.\d+)?|[IVXLCDM]+|\d+(?:[-to&]\d+)?|one|two|three|four|five|six|seven|eight|nine|ten)(?!\w)|
            (\d+(?:\.\d+)?|[IVXLCDM]+)(?=\s*$)", RegexOptions.ExplicitCapture | RegexOptions.Compiled, "de-DE")]
        private static partial Regex VolumeRegex();


        [GeneratedRegex(@"^(?<album>[^(\[]+)(?:\s*[\(\[](?<year>19\d{2}|20\d{2})[\)\]])?", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex AlbumYearRegex();

        [GeneratedRegex(@"^(?<year>19\d{2}|20\d{2})\s*-\s*(?<artist>[^-]+)\s*-\s*(?<album>.+)(?:\s*[\(\[].+[\)\]])*$", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex YearArtistAlbumRegex();

        [GeneratedRegex(@"^(?<artist>[^-]+)\s*-\s*(?<album>[^(\[]+)(?:\s*[\(\[](?<year>19\d{2}|20\d{2})[\)\]])?", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex ArtistAlbumYearRegex();

        [GeneratedRegex(@"(?<year>19\d{2}|20\d{2})", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex YearExtractionRegex();

        [GeneratedRegex(@"(?:^|\s|\(|\[)(?<negation>Non-?|Not\s+)?Explicit(?:\s|\)|\]|$)", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.Compiled, "de-DE")]
        private static partial Regex ExplicitTagRegex();

        [GeneratedRegex(@"\((?<edition>(?:Super\s+)?Deluxe|Limited|Special|Expanded|Extended|Anniversary|Remaster(?:ed)?|Live|Acoustic|Unplugged|Japanese|Bonus|Instrumental|Collector'?s)(?:\s+(?:Edition|Version|Album|Tracks?|Exclusive))?\)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex EditionRegex();

        [GeneratedRegex(@"\b(?:the|a|an|feat|featuring|ft|presents|pres|with|and)\b", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.Compiled, "de-DE")]
        private static partial Regex RemoveWordsRegex();

        [GeneratedRegex(@"(\d+)(?:[-to&]\s*\d+)?", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex VolumeRangeRegex();

        [GeneratedRegex(@"^M{0,4}(?:CM|CD|D?C{0,3})(?:XC|XL|L?X{0,3})(?:IX|IV|V?I{0,3})$", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex RomanNumeralRegex();

        [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
        private static partial Regex ReduceWhitespaceRegex();

        [GeneratedRegex(@"[^\w\s$-]", RegexOptions.Compiled)]
        private static partial Regex RemoveNonAlphanumericRegex();

        [GeneratedRegex(@"[._/]+", RegexOptions.Compiled)]
        private static partial Regex NormalizeCharactersRegex();
    }
}