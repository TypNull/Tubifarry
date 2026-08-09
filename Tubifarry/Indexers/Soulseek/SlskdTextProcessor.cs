using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tubifarry.Indexers.Soulseek
{
    /// <summary>
    /// Handles text processing, normalization, and variations for search queries
    /// </summary>
    public static partial class SlskdTextProcessor
    {
        private static readonly Dictionary<string, int> RomanNumerals = new(StringComparer.OrdinalIgnoreCase)
        {
            { "I", 1 }, { "II", 2 }, { "III", 3 }, { "IV", 4 }, { "V", 5 },
            { "VI", 6 }, { "VII", 7 }, { "VIII", 8 }, { "IX", 9 }, { "X", 10 },
            { "XI", 11 }, { "XII", 12 }, { "XIII", 13 }, { "XIV", 14 }, { "XV", 15 },
            { "XVI", 16 }, { "XVII", 17 }, { "XVIII", 18 }, { "XIX", 19 }, { "XX", 20 }
        };

        private static readonly string[] VolumeFormats = { "Volume", "Vol.", "Vol", "v", "V" };

        private static readonly HashSet<string> BlockedSearchTerms = new(StringComparer.OrdinalIgnoreCase)
        {
            "beyonce",
            "jay-z",
            "Beyoncé",
            "beyoncè",
            "gorillaz",
            "depeche mode",
            "village people",
            "chicane",
            "bryan",
            "cat power",
            "lady gaga",
            "michael jackson",
            "beatles",
            "adele",
            "ymca",
            "lemonade",
            "macho man",
            "in the navy",
            "purple rain",
            "rihanna",
            "weeknd",
            "kanye west",
            "kendrick lamar",
            "frank ocean",
            "minaj",
            "linkin park"
        };

        private static readonly string[][] BlockedTermWords = [.. BlockedSearchTerms
            .OrderByDescending(t => t.Length)
            .Select(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries))];


        public static string BuildSearchText(string? artist, string? album)
            => string.Join(" ", new[] { album, artist }.Where(term => !string.IsNullOrWhiteSpace(term)).Select(term => term?.Trim()));

        public static bool ShouldNormalizeCharacters(string? artist, string? album)
        {
            string? normalizedArtist = artist != null ? NormalizeSpecialCharacters(artist) : null;
            string? normalizedAlbum = album != null ? NormalizeSpecialCharacters(album) : null;
            return (normalizedArtist != null && !string.Equals(normalizedArtist, artist, StringComparison.OrdinalIgnoreCase)) ||
                   (normalizedAlbum != null && !string.Equals(normalizedAlbum, album, StringComparison.OrdinalIgnoreCase));
        }

        public static bool ShouldStripPunctuation(string? artist, string? album)
        {
            string? strippedArtist = artist != null ? StripPunctuation(artist) : null;
            string? strippedAlbum = album != null ? StripPunctuation(album) : null;
            return (strippedArtist != null && !string.Equals(strippedArtist, artist, StringComparison.OrdinalIgnoreCase)) ||
                   (strippedAlbum != null && !string.Equals(strippedAlbum, album, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsVariousArtists(string artist)
            => artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase) || artist.Equals("VA", StringComparison.OrdinalIgnoreCase);

        public static bool ContainsVolumeReference(string album)
            => album.Contains("Volume", StringComparison.OrdinalIgnoreCase) || album.Contains("Vol", StringComparison.OrdinalIgnoreCase);

        public static bool ShouldGenerateRomanVariations(string album)
        {
            Match romanMatch = RomanNumeralRegex().Match(album);
            if (!romanMatch.Success) return false;

            Match volumeMatch = VolumeRegex().Match(album);
            return !(volumeMatch.Success && volumeMatch.Groups[2].Value.Equals(romanMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
        }

        public static string StripPunctuation(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            string stripped = PunctuationRegex().Replace(input, "");
            return StripPunctuationRegex().Replace(stripped, " ").Trim();
        }

        public static string NormalizeSpecialCharacters(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            bool isAscii = true;
            foreach (char c in input)
            {
                if (c > 127)
                {
                    isAscii = false;
                    break;
                }
            }
            if (isAscii) return input;

            string decomposed = input.Normalize(NormalizationForm.FormD);
            StringBuilder sb = new(decomposed.Length);

            foreach (char c in decomposed)
            {
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat != UnicodeCategory.NonSpacingMark && cat != UnicodeCategory.SpacingCombiningMark && cat != UnicodeCategory.EnclosingMark)
                    sb.Append(c);
            }

            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        public static IEnumerable<string> GenerateVolumeVariations(string album)
        {
            if (string.IsNullOrEmpty(album)) yield break;

            Match volumeMatch = VolumeRegex().Match(album);
            if (!volumeMatch.Success) yield break;

            string volumeFormat = volumeMatch.Groups[1].Value;
            string volumeNumber = volumeMatch.Groups[2].Value;

            if (RomanNumerals.TryGetValue(volumeNumber, out int arabicNumber))
            {
                yield return album.Replace(volumeMatch.Value, $"{volumeFormat} {arabicNumber}");
            }
            else if (int.TryParse(volumeNumber, out arabicNumber) && arabicNumber > 0 && arabicNumber <= 20)
            {
                KeyValuePair<string, int> romanPair = RomanNumerals.FirstOrDefault(x => x.Value == arabicNumber);
                if (!string.IsNullOrEmpty(romanPair.Key))
                    yield return album.Replace(volumeMatch.Value, $"{volumeFormat} {romanPair.Key}");
            }
            foreach (string format in VolumeFormats)
            {
                if (!format.Equals(volumeFormat, StringComparison.OrdinalIgnoreCase))
                    yield return album.Replace(volumeMatch.Value, $"{format} {volumeNumber}");
            }
            if (album.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3)
            {
                string withoutVolume = album.Replace(volumeMatch.Value, "").Trim();
                if (withoutVolume.Length > 10)
                    yield return withoutVolume;
            }
        }

        public static IEnumerable<string> GenerateRomanNumeralVariations(string album)
        {
            if (string.IsNullOrEmpty(album)) yield break;

            Match romanMatch = RomanNumeralRegex().Match(album);
            if (!romanMatch.Success) yield break;
            Match volumeMatch = VolumeRegex().Match(album);
            if (volumeMatch.Success && volumeMatch.Groups[2].Value.Equals(romanMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                yield break;

            string romanNumeral = romanMatch.Groups[1].Value;
            if (RomanNumerals.TryGetValue(romanNumeral, out int arabicNumber))
                yield return album.Replace(romanMatch.Value, arabicNumber.ToString());
        }

        public static string GetMergedDirectoryKey(string directory)
        {
            int separatorIndex = directory.LastIndexOfAny(['\\', '/']);
            if (separatorIndex <= 0)
                return directory;

            string lastSegment = directory[(separatorIndex + 1)..].Trim();
            return DiscFolderRegex().IsMatch(lastSegment) ? directory[..separatorIndex] : directory;
        }

        public static List<IGrouping<string, SlskdFileData>> MergeDiscSubdirectories(IEnumerable<IGrouping<string, SlskdFileData>> directoryGroups) =>
            directoryGroups
                .SelectMany(group => group.Select(file => (Key: GetMergedDirectoryKey(group.Key), File: file)))
                .GroupBy(x => x.Key, x => x.File)
                .ToList();

        public static string RemoveBlockedTerms(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return string.Empty;

            string[] words = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool[] removed = new bool[words.Length];

            foreach (string[] termWords in BlockedTermWords)
            {
                for (int i = 0; i + termWords.Length <= words.Length; i++)
                {
                    bool match = !removed[i];
                    for (int j = 0; match && j < termWords.Length; j++)
                        match = !removed[i + j] && string.Equals(words[i + j], termWords[j], StringComparison.OrdinalIgnoreCase);
                    if (match)
                        for (int j = 0; j < termWords.Length; j++)
                            removed[i + j] = true;
                }
            }

            return string.Join(' ', words.Where((_, i) => !removed[i]));
        }

        public static bool ContainsBlockedTerms(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return false;

            string[] words = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return RemoveBlockedTerms(searchText).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != words.Length;
        }

        private static readonly Dictionary<char, string> AccentInjectionMap = new()
        {
            { 'a', "àáâä" }, { 'A', "ÀÁÂÄ" },
            { 'e', "èéêë" }, { 'E', "ÈÉÊË" },
            { 'i', "ìíîï" }, { 'I', "ÌÍÎÏ" },
            { 'o', "òóôö" }, { 'O', "ÒÓÔÖ" },
            { 'u', "ùúûü" }, { 'U', "ÙÚÛÜ" },
            { 'y', "ýÿ" }, { 'Y', "ÝŸ" },
            { 'c', "çć" }, { 'C', "ÇĆ" },
            { 'n', "ñń" }, { 'N', "ÑŃ" },
            { 's', "śş" }, { 'S', "ŚŞ" },
        };

        public static string RewriteRestrictedTerms(string searchText, int variant = 0)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return string.Empty;

            string[] words = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] normalized = [.. words.Select(NormalizeSpecialCharacters)];
            bool[] keep = [.. words.Select(_ => true)];

            foreach (string[] termWords in BlockedTermWords)
                for (int i = 0; i + termWords.Length <= words.Length; i++)
                {
                    bool match = true;
                    for (int j = 0; match && j < termWords.Length; j++)
                        match = string.Equals(normalized[i + j], termWords[j], StringComparison.OrdinalIgnoreCase);
                    if (!match)
                        continue;

                    bool injected = false;
                    for (int j = 0; j < termWords.Length && !injected; j++)
                        injected = TryInjectAccent(ref words[i + j], variant);

                    if (!injected)
                        for (int j = 0; j < termWords.Length; j++)
                            keep[i + j] = false;
                }

            return string.Join(' ', words.Where((_, i) => keep[i]));
        }

        private static bool TryInjectAccent(ref string word, int variant)
        {
            for (int c = 0; c < word.Length; c++)
            {
                if (AccentInjectionMap.TryGetValue(word[c], out string? variants))
                {
                    word = word[..c] + variants[variant % variants.Length] + word[(c + 1)..];
                    return true;
                }
            }
            return false;
        }

        public static IEnumerable<string> GetBlockedTermEvidenceTracks(IEnumerable<string> tracks, string? album)
        {
            return tracks
                .Select(CleanEvidenceTitle)
                .Where(t => t.Length >= 3
                            && !ContainsBlockedTerms(t)
                            && !string.Equals(t, album?.Trim(), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(t => t.Length);

            static string CleanEvidenceTitle(string title)
            {
                string cleaned = BracketedContentRegex().Replace(title, " ");
                cleaned = StripPunctuation(cleaned);
                return cleaned.Trim();
            }
        }

        public static string GetDirectoryFromFilename(string? filename)
        {
            if (string.IsNullOrEmpty(filename))
                return "";
            int lastBackslashIndex = filename.LastIndexOf('\\');
            return lastBackslashIndex >= 0 ? filename[..lastBackslashIndex] : "";
        }

        public static HashSet<string> ParseListContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return content
                .Split(['\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(username => !string.IsNullOrWhiteSpace(username))
                .Select(username => username.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        [GeneratedRegex(@"\s+")]
        private static partial Regex StripPunctuationRegex();

        [GeneratedRegex(@"^(cd|disc|disk|dvd)\s*[-_. ]?\s*\d{1,2}$", RegexOptions.IgnoreCase)]
        private static partial Regex DiscFolderRegex();

        [GeneratedRegex(@"[\(\[\{].*?[\)\]\}]")]
        private static partial Regex BracketedContentRegex();

        [GeneratedRegex(@"[^\w\s-&]")]
        private static partial Regex PunctuationRegex();

        [GeneratedRegex(@"(Vol(?:ume)?\.?)\s*([0-9]+|[IVXLCDM]+)", RegexOptions.IgnoreCase)]
        private static partial Regex VolumeRegex();

        [GeneratedRegex(@"\b([IVXLCDM]+)\b", RegexOptions.IgnoreCase)]
        private static partial Regex RomanNumeralRegex();
    }
}