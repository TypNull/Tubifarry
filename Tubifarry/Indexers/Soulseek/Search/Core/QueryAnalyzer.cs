using System.Text.RegularExpressions;

namespace Tubifarry.Indexers.Soulseek.Search.Core;

public static partial class QueryAnalyzer
{
    private static readonly HashSet<string> VariousArtistsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Various Artists", "VA", "V.A.", "V/A", "Various", "Soundtrack", "OST",
        "Original Soundtrack", "Compilation", "Mixed By", "DJ Mix"
    };

    private const int SelfTitledFuzzyThreshold = 90;
    private const int ShortNameThreshold = 4;

    public static QueryType Analyze(SearchContext context)
    {
        QueryType type = QueryType.Normal;

        if (IsVariousArtists(context.Artist))
            type |= QueryType.VariousArtists;

        if (IsSelfTitled(context.Artist, context.Album))
            type |= QueryType.SelfTitled;

        if (IsShortName(context.Album))
            type |= QueryType.ShortName;

        if (NeedsTypeDisambiguation(context))
            type |= QueryType.NeedsTypeDisambiguation;

        if (HasVolumeReference(context.Album))
            type |= QueryType.HasVolume;

        if (HasStandaloneRomanNumeral(context.Album))
            type |= QueryType.HasRomanNumeral;

        if (NeedsNormalization(context.Artist, context.Album))
            type |= QueryType.NeedsNormalization;

        return type;
    }

    public static bool IsVariousArtists(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return false;
        return VariousArtistsNames.Contains(artist.Trim());
    }

    public static bool IsSelfTitled(string? artist, string? album)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            return false;

        string normArtist = NormalizeName(artist);
        string normAlbum = NormalizeName(album);

        if (normArtist.Equals(normAlbum, StringComparison.OrdinalIgnoreCase))
            return true;

        // Use token set ratio for better handling of word order differences
        int similarity = FuzzySharp.Fuzz.TokenSetRatio(normAlbum, normArtist);
        if (similarity >= SelfTitledFuzzyThreshold)
            return true;

        // Check if one fully contains the other (for cases like "Weezer" / "Weezer (Blue Album)")
        if (normArtist.Length >= 3 && normAlbum.Length >= 3)
        {
            string shorter = normArtist.Length < normAlbum.Length ? normArtist : normAlbum;
            string longer = normArtist.Length < normAlbum.Length ? normAlbum : normArtist;

            if (longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IsShortName(string? album)
    {
        if (string.IsNullOrWhiteSpace(album))
            return false;
        return album.Trim().Length < ShortNameThreshold;
    }

    public static bool NeedsTypeDisambiguation(SearchContext context)
    {
        if (context.PrimaryType != NzbDrone.Core.Music.PrimaryAlbumType.EP &&
            context.PrimaryType != NzbDrone.Core.Music.PrimaryAlbumType.Single)
            return false;

        // Short names always need disambiguation
        if (IsShortName(context.Album))
            return true;

        // Self-titled EPs/Singles need disambiguation
        if (IsSelfTitled(context.Artist, context.Album))
            return true;

        // Common album-like names that could conflict
        string? album = context.Album?.Trim();
        if (string.IsNullOrEmpty(album))
            return true;

        // Single-word titles are ambiguous
        if (!album.Contains(' '))
            return true;

        return false;
    }

    public static bool HasVolumeReference(string? album) =>
        !string.IsNullOrWhiteSpace(album) && VolumeRegex().IsMatch(album);

    public static bool HasStandaloneRomanNumeral(string? album)
    {
        if (string.IsNullOrWhiteSpace(album))
            return false;

        Match romanMatch = StandaloneRomanRegex().Match(album);
        if (!romanMatch.Success)
            return false;

        Match volumeMatch = VolumeRegex().Match(album);
        if (volumeMatch.Success &&
            volumeMatch.Index <= romanMatch.Index &&
            romanMatch.Index + romanMatch.Length <= volumeMatch.Index + volumeMatch.Length)
            return false;

        return true;
    }

    public static bool NeedsNormalization(string? artist, string? album) =>
        HasSpecialCharacters(artist) || HasSpecialCharacters(album) ||
        HasPunctuation(artist) || HasPunctuation(album);

    private static bool HasSpecialCharacters(string? text) =>
        !string.IsNullOrEmpty(text) && SpecialCharRegex().IsMatch(text);

    private static bool HasPunctuation(string? text) =>
        !string.IsNullOrEmpty(text) && PunctuationRegex().IsMatch(text);

    private static string NormalizeName(string name)
    {
        string normalized = ArticleRegex().Replace(name, "");
        normalized = PunctuationRegex().Replace(normalized, "");
        return CollapseWhitespaceRegex().Replace(normalized, " ").Trim();
    }

    [GeneratedRegex(@"\b(the|a|an)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ArticleRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespaceRegex();

    [GeneratedRegex(@"[^\w\s]", RegexOptions.Compiled)]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"[àáâãäåæçèéêëìíîïðñòóôõöøùúûüýþÿÀÁÂÃÄÅÆÇÈÉÊËÌÍÎÏÐÑÒÓÔÕÖØÙÚÛÜÝÞ]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SpecialCharRegex();

    [GeneratedRegex(@"\b([IVXLCDM]{1,4})\b", RegexOptions.Compiled)]
    private static partial Regex StandaloneRomanRegex();

    [GeneratedRegex(@"\b(?:Vol(?:ume)?\.?)\s*([0-9]+|[IVXLCDM]+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VolumeRegex();
}
