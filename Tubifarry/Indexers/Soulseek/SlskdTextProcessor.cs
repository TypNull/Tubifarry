using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tubifarry.Indexers.Soulseek
{
    /// <summary>
    /// Handles text processing, normalization, and variations for search queries
    /// </summary>
    public static class SlskdTextProcessor
    {
        private static readonly Dictionary<string, int> RomanNumerals = new(StringComparer.OrdinalIgnoreCase)
        {
            { "I", 1 }, { "II", 2 }, { "III", 3 }, { "IV", 4 }, { "V", 5 },
            { "VI", 6 }, { "VII", 7 }, { "VIII", 8 }, { "IX", 9 }, { "X", 10 },
            { "XI", 11 }, { "XII", 12 }, { "XIII", 13 }, { "XIV", 14 }, { "XV", 15 },
            { "XVI", 16 }, { "XVII", 17 }, { "XVIII", 18 }, { "XIX", 19 }, { "XX", 20 }
        };

        private static readonly string[] VolumeFormats = { "Volume", "Vol.", "Vol", "v", "V" };
        private static readonly Regex PunctuationPattern = new(@"[^\w\s-&]", RegexOptions.Compiled);
        private static readonly Regex VolumePattern = new(@"(Vol(?:ume)?\.?)\s*([0-9]+|[IVXLCDM]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RomanNumeralPattern = new(@"\b([IVXLCDM]+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            Match romanMatch = RomanNumeralPattern.Match(album);
            if (!romanMatch.Success) return false;

            Match volumeMatch = VolumePattern.Match(album);
            return !(volumeMatch.Success && volumeMatch.Groups[2].Value.Equals(romanMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
        }

        public static string StripPunctuation(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            string stripped = PunctuationPattern.Replace(input, "");
            return Regex.Replace(stripped, @"\s+", " ").Trim();
        }

        public static string NormalizeSpecialCharacters(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

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

            Match volumeMatch = VolumePattern.Match(album);
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

            Match romanMatch = RomanNumeralPattern.Match(album);
            if (!romanMatch.Success) yield break;
            Match volumeMatch = VolumePattern.Match(album);
            if (volumeMatch.Success && volumeMatch.Groups[2].Value.Equals(romanMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                yield break;

            string romanNumeral = romanMatch.Groups[1].Value;
            if (RomanNumerals.TryGetValue(romanNumeral, out int arabicNumber))
                yield return album.Replace(romanMatch.Value, arabicNumber.ToString());
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
                .Split(new[] { '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(username => !string.IsNullOrWhiteSpace(username))
                .Select(username => username.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }
}