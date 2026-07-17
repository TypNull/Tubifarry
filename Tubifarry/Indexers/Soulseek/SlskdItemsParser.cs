using FuzzySharp;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Core.Indexers;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Tubifarry.Core.Model;
using Tubifarry.Core.Telemetry;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Soulseek
{
    public partial class SlskdItemsParser : ISlskdItemsParser
    {
        private readonly Logger _logger;
        private readonly ISentryHelper _sentry;

        // Fuzzy matching threshold constants
        private const int FuzzyArtistPartialThreshold = 90;
        private const int FuzzyArtistTokenSortThreshold = 85;
        private const int FuzzyAlbumPartialThreshold = 85;
        private const int FuzzyAlbumTokenSortThreshold = 80;
        private const int FuzzyCombinedThreshold = 85;

        private static readonly ConcurrentDictionary<(string, string), (int Partial, int TokenSort)> FuzzyCache = new();
        private const int MaxCacheSize = 10000;

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

        public SlskdItemsParser(ISentryHelper sentry, Logger logger)
        {
            _sentry = sentry;
            _logger = logger;
        }

        public SlskdFolderData ParseFolderName(string folderPath)
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

        public AlbumData CreateAlbumData(string searchId, IGrouping<string, SlskdFileData> directory, SlskdSearchData searchData, SlskdFolderData folderData, SlskdSettings? settings = null, int expectedTrackCount = 0)
        {
            string dirNameNorm = NormalizeString(directory.Key);
            string searchArtistNorm = NormalizeString(searchData.Artist ?? "");
            string searchAlbumNorm = NormalizeString(searchData.Album ?? "");

            _logger.Trace($"Creating album data - Dir: '{dirNameNorm}', Search artist: '{searchArtistNorm}', Search album: '{searchAlbumNorm}'");

            // Calculate fuzzy scores with caching for performance
            (int fuzzyArtistPartial, int fuzzyArtistTokenSort) = GetCachedFuzzyScores(dirNameNorm, searchArtistNorm);
            (int fuzzyAlbumPartial, int fuzzyAlbumTokenSort) = GetCachedFuzzyScores(dirNameNorm, searchAlbumNorm);

            bool isVolumeSearch = !string.IsNullOrEmpty(searchData.Album) && VolumeRegex().Match(searchData.Album).Success;

            bool isAlbumMatch = isVolumeSearch ? CheckVolumeSeriesMatch(directory.Key, searchData.Album) : !string.IsNullOrEmpty(searchAlbumNorm) && (fuzzyAlbumPartial > FuzzyAlbumPartialThreshold || fuzzyAlbumTokenSort > FuzzyAlbumTokenSortThreshold);
            bool isArtistMatch = IsFuzzyArtistMatch(dirNameNorm, searchArtistNorm);

            if (!isArtistMatch && !isAlbumMatch && !string.IsNullOrEmpty(searchData.Artist) && !string.IsNullOrEmpty(searchData.Album))
            {
                string combinedSearch = NormalizeString($"{searchData.Artist} {searchData.Album}");
                (int combinedPartial, _) = GetCachedFuzzyScores(dirNameNorm, combinedSearch);
                isAlbumMatch = combinedPartial > FuzzyCombinedThreshold;
            }

            // Path-tail match: album name often sits in the parent
            if (!isAlbumMatch && !string.IsNullOrEmpty(searchAlbumNorm))
            {
                string[] components = SplitPathIntoComponents(directory.Key);
                if (components.Length >= 2)
                {
                    string pathTailNorm = NormalizeString($"{components[^2]} {components[^1]}");
                    (int tailPartial, int tailTokenSort) = GetCachedFuzzyScores(pathTailNorm, searchAlbumNorm);
                    isAlbumMatch = tailPartial > FuzzyAlbumPartialThreshold || tailTokenSort > FuzzyAlbumTokenSortThreshold;
                }
            }

            double trackEvidence = CalculateTrackTitleEvidence(directory, searchData.Tracks);
            if (trackEvidence >= 0.35)
                isAlbumMatch = true;

            _logger.Debug($"Match results - Artist: {isArtistMatch}, Album: {isAlbumMatch}, TrackEvidence: {trackEvidence:P0}");

            // Determine final values for artist, album, year
            string finalArtist = DetermineFinalArtist(isArtistMatch, folderData, searchData);
            string finalAlbum = DetermineFinalAlbum(isAlbumMatch, folderData, searchData);
            string sourceTag = DetectSourceTag(folderData.Path);
            List<string> releaseTags = ExtractReleaseTags(folderData.Path);
            string finalYear = folderData.Year;

            (AudioFormat Codec, int? BitRate, int? BitDepth, int? SampleRate, long TotalSize, int TotalDuration) = AnalyzeAudioQuality(directory);
            string qualityInfo = FormatQualityInfo(Codec, BitRate, BitDepth, SampleRate);

            List<SlskdFileData>? filesToDownload = directory
                .Where(f =>
                {
                    int separatorIndex = f.Filename?.LastIndexOf('\\') ?? -1;
                    if (separatorIndex <= 0)
                        return false;

                    string parent = f.Filename![..separatorIndex];
                    return parent == directory.Key || SlskdTextProcessor.GetMergedDirectoryKey(parent) == directory.Key;
                })
                .ToList();
            int actualTrackCount = filesToDownload?.Count ?? 0;

            _logger.Trace($"Audio: {Codec}, BitRate: {BitRate}, BitDepth: {BitDepth}, Files: {actualTrackCount}");

            string infoUrl = settings != null ? $"{(string.IsNullOrEmpty(settings.ExternalUrl) ? settings.BaseUrl : settings.ExternalUrl)}/searches/{searchId}" : "";
            string? edition = ExtractEdition(folderData.Path)?.ToUpper();

            int priority = folderData.CalculatePriority(expectedTrackCount);
            priority += (int)(trackEvidence * 400);
            if (HasTrackNumberGaps(directory))
                priority /= 2;
            priority = Math.Clamp(priority, 0, 10000);

            string regexMatchType = DetermineRegexMatchType(folderData.Path);
            List<string>? directoryFiles = filesToDownload?.Select(f => f.Filename ?? "").Where(f => !string.IsNullOrEmpty(f)).ToList();

            _sentry.LogParseResult(
                searchId,
                folderData.Path,
                regexMatchType,
                fuzzyArtistPartial,
                fuzzyAlbumPartial,
                fuzzyArtistTokenSort,
                fuzzyAlbumTokenSort,
                priority,
                Codec.ToString(),
                BitRate ?? 0,
                BitDepth ?? 0,
                expectedTrackCount,
                actualTrackCount,
                folderData.Username,
                folderData.HasFreeUploadSlot,
                folderData.QueueLength,
                directoryFiles,
                searchData.Interactive);

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
                Priotity = priority,
                ShowPriorityInTitle = settings?.ShowPriorityInTitle ?? false,
                SourceTag = sourceTag,
                CustomString = JsonConvert.SerializeObject(filesToDownload),
                ExtraInfo = [edition, .. releaseTags.Where(t => !string.Equals(t, edition, StringComparison.OrdinalIgnoreCase)), $"👤 {folderData.Username} ", $"{(folderData.HasFreeUploadSlot ? "⚡" : "❌")} {folderData.UploadSpeed / 1024.0 / 1024.0:F2}MB/s ", folderData.QueueLength == 0 ? "" : $"📋 {folderData.QueueLength}"],
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

            // Try artist-year-album pattern
            match = TryMatchRegex(lastComponent, ArtistYearAlbumRegex());
            if (match?.Groups["album"].Success == true)
            {
                return (
                    match.Groups["artist"].Value.Trim(),
                    CleanComponent(match.Groups["album"].Value),
                    match.Groups["year"].Value.Trim());
            }

            // Try leading-year pattern
            match = TryMatchRegex(lastComponent, LeadingYearAlbumRegex());
            if (match?.Groups["album"].Success == true)
            {
                return (
                    GetArtistFromParentFolder(pathComponents),
                    CleanComponent(match.Groups["album"].Value),
                    match.Groups["year"].Value.Trim());
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

        private bool CheckVolumeSeriesMatch(string directoryPath, string? searchAlbum)
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
                NormalizeString(searchBaseAlbum)) > FuzzyAlbumPartialThreshold;

            return baseAlbumMatch && normSearchVol.Equals(normDirVol, StringComparison.OrdinalIgnoreCase);
        }

        public string NormalizeVolume(string volume)
        {
            _logger.Trace($"Normalizing volume: '{volume}'");

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

        private int ConvertRomanToNumber(string roman)
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

            _logger.Trace($"Roman numeral '{roman}' converted to: {total}");
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

        private bool ExtractExplicitTag(string path)
        {
            Match match = ExplicitTagRegex().Match(path);
            if (match.Success)
            {
                if (match.Groups["negation"].Success && !string.IsNullOrWhiteSpace(match.Groups["negation"].Value))
                {
                    _logger.Trace($"Found negated explicit tag in path, skipping: {match.Value}");
                    return false;
                }

                _logger.Trace($"Extracted explicit tag from path: {path}");
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

        private static bool IsFuzzyArtistMatch(string dirNameNorm, string searchArtistNorm)
        {
            if (string.IsNullOrEmpty(searchArtistNorm))
                return false;
            (int partial, int tokenSort) = GetCachedFuzzyScores(dirNameNorm, searchArtistNorm);
            return partial > FuzzyArtistPartialThreshold || tokenSort > FuzzyArtistTokenSortThreshold;
        }

        private static bool IsFuzzyAlbumMatch(string dirNameNorm, string searchAlbumNorm, bool volumeMatch)
        {
            if (string.IsNullOrEmpty(searchAlbumNorm))
                return false;
            if (volumeMatch)
                return true;
            (int partial, int tokenSort) = GetCachedFuzzyScores(dirNameNorm, searchAlbumNorm);
            return partial > FuzzyAlbumPartialThreshold || tokenSort > FuzzyAlbumTokenSortThreshold;
        }

        /// <summary>
        /// Gets fuzzy matching scores with caching to avoid redundant calculations.
        /// </summary>
        private static (int Partial, int TokenSort) GetCachedFuzzyScores(string str1, string str2)
        {
            if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2))
                return (0, 0);

            // Create a consistent cache key (alphabetically ordered to ensure cache hits)
            (string, string) cacheKey = string.Compare(str1, str2, StringComparison.Ordinal) < 0
                ? (str1, str2)
                : (str2, str1);

            return FuzzyCache.GetOrAdd(cacheKey, key =>
            {
                // Clear cache if it gets too large to prevent memory issues
                if (FuzzyCache.Count > MaxCacheSize)
                {
                    FuzzyCache.Clear();
                }

                return (
                    Fuzz.PartialRatio(key.Item1, key.Item2),
                    Fuzz.TokenSortRatio(key.Item1, key.Item2)
                );
            });
        }

        public static string DetermineRegexMatchType(string folderPath)
        {
            string[] pathComponents = SplitPathIntoComponents(folderPath);
            if (pathComponents.Length == 0)
                return "none";

            string lastComponent = pathComponents[^1];

            if (TryMatchRegex(lastComponent, ArtistAlbumYearRegex()) != null)
                return "ArtistAlbumYear";

            if (TryMatchRegex(lastComponent, YearArtistAlbumRegex()) != null)
                return "YearArtistAlbum";

            if (TryMatchRegex(lastComponent, ArtistYearAlbumRegex()) != null)
                return "ArtistYearAlbum";

            if (TryMatchRegex(lastComponent, LeadingYearAlbumRegex()) != null)
                return "LeadingYearAlbum";

            if (TryMatchRegex(lastComponent, AlbumYearRegex()) != null)
                return "AlbumYear";

            return "none";
        }

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
                return CleanFallbackAlbum(folderData.Album, searchData.Artist);
            return searchData.Album ?? "Unknown Album";
        }

        private static string CleanFallbackAlbum(string album, string? artist)
        {
            string cleaned = JunkTokenRegex().Replace(album, " ");

            string normArtist = string.IsNullOrEmpty(artist) ? string.Empty : NormalizeString(artist);
            if (normArtist.Length > 2)
            {
                string[] words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string[] artistWords = normArtist.Split(' ');
                List<string> output = [];
                int i = 0;
                while (i < words.Length)
                {
                    if (i + artistWords.Length <= words.Length &&
                        NormalizeString(string.Join(' ', words[i..(i + artistWords.Length)])) == normArtist)
                    {
                        i += artistWords.Length;
                        continue;
                    }
                    output.Add(words[i]);
                    i++;
                }
                if (output.Count > 0)
                    cleaned = string.Join(' ', output);
            }

            cleaned = ReduceWhitespaceRegex().Replace(cleaned, " ").Trim(' ', '-', ':', '–');
            return cleaned.Length >= 2 ? cleaned : album;
        }

        private static double CalculateTrackTitleEvidence(IEnumerable<SlskdFileData> files, List<string>? expectedTracks)
        {
            if (expectedTracks == null || expectedTracks.Count == 0)
                return 0;

            List<string> titles = expectedTracks.Select(NormalizeString).Where(t => t.Length >= 4).ToList();
            if (titles.Count == 0)
                return 0;

            string haystack = NormalizeString(string.Join(" ", files
                .Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Filename ?? string.Empty))
                .Select(n => TrackNumberPrefixRegex().Replace(n, string.Empty))));

            return titles.Count(haystack.Contains) / (double)titles.Count;
        }

        private static bool HasTrackNumberGaps(IEnumerable<SlskdFileData> files)
        {
            List<int> numbers = files
                .Select(f => TrackNumberPrefixCaptureRegex().Match(System.IO.Path.GetFileName(f.Filename ?? string.Empty)))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value))
                .Where(n => n is > 0 and <= 150)
                .Distinct()
                .Order()
                .ToList();

            return numbers.Count >= 3 && numbers[^1] - numbers[0] + 1 != numbers.Count;
        }

        private static List<string> ExtractReleaseTags(string path) =>
            ReleaseTagRegex().Matches(path)
                .Select(m => ReduceWhitespaceRegex().Replace(m.Groups["tag"].Value, " ").Trim().ToUpperInvariant())
                .Distinct()
                .Take(4)
                .ToList();

        private static string DetectSourceTag(string path)
        {
            Match match = SourceTagRegex().Match(path);
            if (!match.Success)
                return "WEB";

            string value = match.Value.ToUpperInvariant();
            return value switch
            {
                "VINYL" => "Vinyl",
                "WEB" => "WEB",
                "SACD" or "MFSL" or "MOFI" => "SACD",
                _ when value.StartsWith("DSD") => "SACD",
                _ => "CD"
            };
        }

        private (AudioFormat Codec, int? BitRate, int? BitDepth, int? SampleRate, long TotalSize, int TotalDuration) AnalyzeAudioQuality(IGrouping<string, SlskdFileData> directory)
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
                _logger.Trace($"Calculated bitrate: {commonBitRate}");
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

        [GeneratedRegex(@"^[\(\[]?(?<year>19\d{2}|20\d{2})[\)\]]?\s*[-–._]*\s*(?<album>.+)$", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex LeadingYearAlbumRegex();

        [GeneratedRegex(@"^(?<artist>[^-]+?)\s*-\s*(?:[\(\[](?<year>19\d{2}|20\d{2})[\)\]]|(?<year>19\d{2}|20\d{2})\s*-)\s*(?<album>.+)$", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex ArtistYearAlbumRegex();

        [GeneratedRegex(@"(?ix)\b(?<tag>
            remaster(?:ed)?(?:\s*(?:19|20)\d{2})? | (?:19|20)\d{2}\s*remaster(?:ed)? |
            (?:super\s+)?deluxe | \d{1,3}(?:th|st|nd|rd)\s+anniversary | anniversary |
            expanded | limited | collector'?s | box\s?set |
            live | unplugged | acoustic |
            vinyl | sacd | dsd\d* | mfsl | mofi | mono |
            24\s?[-_]?\s?bit(?:\s*/?\s*\d{2,3}(?:\.\d)?\s?khz)? | \d{2,3}(?:\.\d)?\s?khz |
            japan(?:ese)? | instrumental | bonus\s+tracks?
            )\b", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex ReleaseTagRegex();

        [GeneratedRegex(@"(?i)@\w+|\((?:19|20)\d{2}-\d{2}-\d{2}\)|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.Compiled)]
        private static partial Regex JunkTokenRegex();

        [GeneratedRegex(@"^\s*[a-dA-D]?\d{1,3}\s*[-._) ]+\s*", RegexOptions.Compiled)]
        private static partial Regex TrackNumberPrefixRegex();

        [GeneratedRegex(@"^\s*(\d{1,3})\b", RegexOptions.Compiled)]
        private static partial Regex TrackNumberPrefixCaptureRegex();

        [GeneratedRegex(@"(?i)\b(vinyl|sacd|dsd\d*|mfsl|mofi|web|cd)\b", RegexOptions.Compiled)]
        private static partial Regex SourceTagRegex();

        [GeneratedRegex(@"^(?<year>19\d{2}|20\d{2})\s*-\s*(?<artist>[^-]+)\s*-\s*(?<album>.+)(?:\s*[\(\[].+[\)\]])*$", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex YearArtistAlbumRegex();

        [GeneratedRegex(@"^(?<artist>[^-]+)\s*-\s*(?<album>[^(\[]+)(?:\s*[\(\[](?<year>19\d{2}|20\d{2})[\)\]])?", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex ArtistAlbumYearRegex();

        [GeneratedRegex(@"(?<year>19\d{2}|20\d{2})", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex YearExtractionRegex();

        [GeneratedRegex(@"(?:^|\s|\(|\[)(?<negation>Non-?|Not\s+)?Explicit(?:\s|\)|\]|$)", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.Compiled, "de-DE")]
        private static partial Regex ExplicitTagRegex();

        [GeneratedRegex(@"[\(\[](?<edition>(?:(?:Super\s+)?Deluxe|Limited|Special|Expanded|Extended|Anniversary|Remaster(?:ed)?|Live|Acoustic|Unplugged|Japanese|Bonus|Instrumental|Collector'?s|Metal|Platinum|Gold|Clean|Tour|Censored|Uncensored|\d*\s*CD)(?:\s+(?:Edition|Version|Album|Tracks?|Exclusive))?|[^)\]]+?\s+(?:Edition|Version))[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
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