using System.Text.RegularExpressions;

namespace Tubifarry.Indexers.Soulseek.Search.Transformers;

public static partial class QueryBuilder
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "of", "at", "by", "for", "with",
        "as", "to", "in", "on", "is", "are", "was", "were", "be", "been",
        "being", "have", "has", "had", "do", "does", "did", "from", "into"
    };

    private const int MinWordLengthForWildcard = 4;
    private const int MinAlbumLengthForPartial = 15;
    private const int MinSignificantWordsForPartial = 2;

    public static string Build(params string?[] parts) =>
        string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p))).Trim();

    public static string BuildWildcard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length >= MinWordLengthForWildcard && !StopWords.Contains(words[i]))
                words[i] = words[i][..^1] + "*";
        }
        return string.Join(" ", words);
    }

    public static string? BuildPartial(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < MinAlbumLengthForPartial)
            return null;

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Get significant words (non-stopwords)
        List<string> significant = words.Where(w => !StopWords.Contains(w) && w.Length > 1).ToList();

        if (significant.Count < MinSignificantWordsForPartial)
            return null;

        // Strategy: Take unique/longer words first (more distinctive)
        List<string> prioritized = significant
            .OrderByDescending(w => w.Length)
            .ThenBy(w => w)
            .Take(Math.Max(MinSignificantWordsForPartial, (significant.Count + 1) / 2))
            .ToList();

        // Preserve original order
        List<string> result = words.Where(w => prioritized.Contains(w, StringComparer.OrdinalIgnoreCase)).ToList();

        string partial = string.Join(" ", result);

        // Don't return if it's the same as input or too short
        if (partial.Equals(text, StringComparison.OrdinalIgnoreCase) || partial.Length < 5)
            return null;

        return partial;
    }

    public static string ExtractDistinctive(string? text, int maxWords = 3)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Remove parenthetical content first (usually metadata like "(Deluxe Edition)")
        string cleaned = ParenthesesRegex().Replace(text, "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = text;

        string[] words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Filter and prioritize
        List<string> candidates = words
            .Where(w => !StopWords.Contains(w) && w.Length > 2)
            .OrderByDescending(w => w.Length)
            .Take(maxWords)
            .ToList();

        if (candidates.Count == 0)
            return cleaned;

        // Return in original order
        return string.Join(" ", words.Where(w => candidates.Contains(w, StringComparer.OrdinalIgnoreCase)));
    }

    public static string? ConvertVolumeFormat(string? album)
    {
        if (string.IsNullOrWhiteSpace(album))
            return null;

        Match match = VolumeRegex().Match(album);
        if (!match.Success)
            return null;

        string format = match.Groups[1].Value;
        string number = match.Groups[2].Value;

        // Convert number format
        string convertedNumber = ConvertNumber(number);
        if (convertedNumber == number)
        {
            // Number didn't change, try changing the format word
            string newFormat = format.Contains("ume", StringComparison.OrdinalIgnoreCase) ? "Vol." : "Volume";
            return album.Replace(match.Value, $"{newFormat} {number}");
        }

        return album.Replace(match.Value, $"{format} {convertedNumber}");
    }

    public static string? ConvertRomanNumeral(string? album)
    {
        if (string.IsNullOrWhiteSpace(album))
            return null;

        Match romanMatch = StandaloneRomanRegex().Match(album);
        if (!romanMatch.Success)
            return null;

        // Skip if part of volume reference
        Match volumeMatch = VolumeRegex().Match(album);
        if (volumeMatch.Success &&
            romanMatch.Index >= volumeMatch.Index &&
            romanMatch.Index + romanMatch.Length <= volumeMatch.Index + volumeMatch.Length)
            return null;

        string converted = ConvertNumber(romanMatch.Groups[1].Value);
        if (converted == romanMatch.Groups[1].Value)
            return null;

        return album.Replace(romanMatch.Value, converted);
    }

    private static readonly Dictionary<string, int> RomanToArabic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["I"] = 1,
        ["II"] = 2,
        ["III"] = 3,
        ["IV"] = 4,
        ["V"] = 5,
        ["VI"] = 6,
        ["VII"] = 7,
        ["VIII"] = 8,
        ["IX"] = 9,
        ["X"] = 10,
        ["XI"] = 11,
        ["XII"] = 12,
        ["XIII"] = 13,
        ["XIV"] = 14,
        ["XV"] = 15
    };

    private static string ConvertNumber(string number)
    {
        if (RomanToArabic.TryGetValue(number, out int arabic))
            return arabic.ToString();

        if (int.TryParse(number, out arabic) && arabic is > 0 and <= 15)
        {
            var roman = RomanToArabic.FirstOrDefault(x => x.Value == arabic);
            if (!string.IsNullOrEmpty(roman.Key))
                return roman.Key;
        }

        return number;
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.Compiled)]
    private static partial Regex ParenthesesRegex();
    [GeneratedRegex(@"\b([IVXLCDM]{1,4})\b", RegexOptions.Compiled)]
    private static partial Regex StandaloneRomanRegex();
    [GeneratedRegex(@"\b(Vol(?:ume)?\.?)\s*([0-9]+|[IVXLCDM]+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VolumeRegex();
}
