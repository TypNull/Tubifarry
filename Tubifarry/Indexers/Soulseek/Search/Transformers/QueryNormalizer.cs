using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tubifarry.Indexers.Soulseek.Search.Core;

namespace Tubifarry.Indexers.Soulseek.Search.Transformers;

public static partial class QueryNormalizer
{
    public static SearchContext Normalize(SearchContext context)
    {
        if (!context.QueryType.HasFlag(QueryType.NeedsNormalization))
            return context;

        string? normArtist = NormalizeText(context.Artist);
        string? normAlbum = NormalizeText(context.Album);

        bool artistChanged = !string.Equals(normArtist, context.Artist, StringComparison.Ordinal);
        bool albumChanged = !string.Equals(normAlbum, context.Album, StringComparison.Ordinal);

        if (!artistChanged && !albumChanged)
            return context;

        return context with
        {
            NormalizedArtist = artistChanged ? normArtist : null,
            NormalizedAlbum = albumChanged ? normAlbum : null
        };
    }

    public static string NormalizeText(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;

        // Decompose accented characters (é → e + ´)
        string decomposed = input.Normalize(NormalizationForm.FormD);
        StringBuilder sb = new(decomposed.Length);

        foreach (char c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark &&
                category != UnicodeCategory.SpacingCombiningMark &&
                category != UnicodeCategory.EnclosingMark)
            {
                sb.Append(c);
            }
        }

        string result = sb.ToString().Normalize(NormalizationForm.FormC);
        
        // Strip punctuation but keep letters, digits, spaces, hyphens, ampersands
        result = PunctuationRegex().Replace(result, "");
        result = WhitespaceRegex().Replace(result, " ").Trim();

        return result;
    }

    [GeneratedRegex(@"[^\w\s\-&]", RegexOptions.Compiled)]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
