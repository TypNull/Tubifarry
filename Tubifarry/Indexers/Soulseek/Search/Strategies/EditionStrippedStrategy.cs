using System.Text.RegularExpressions;
using Tubifarry.Indexers.Soulseek.Search.Core;
using Tubifarry.Indexers.Soulseek.Search.Transformers;

namespace Tubifarry.Indexers.Soulseek.Search.Strategies;

public sealed partial class EditionStrippedStrategy : SearchStrategyBase
{
    public override string Name => "Edition Stripped";
    public override SearchTier Tier => SearchTier.Variation;
    public override int Priority => 15;

    public override bool CanExecute(SearchContext context, QueryType queryType) =>
        !string.IsNullOrWhiteSpace(context.SearchAlbum) &&
        StripEditions(context.SearchAlbum) != context.SearchAlbum;

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        string stripped = StripEditions(context.SearchAlbum!);
        if (string.IsNullOrWhiteSpace(stripped))
            return null;

        return QueryBuilder.Build(context.SearchArtist, stripped);
    }

    internal static string StripEditions(string album)
    {
        string result = BracketedContentRegex().Replace(album, " ");
        result = EditionSuffixRegex().Replace(result, " ");
        result = WhitespaceRegex().Replace(result, " ").Trim(' ', '-', ':');
        return result;
    }

    [GeneratedRegex(@"\s*[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex BracketedContentRegex();

    [GeneratedRegex(@"(?i)\s*[-–:]?\s*\b(\d+(st|nd|rd|th)\s+anniversary|deluxe|remaster(ed)?|expanded|special|collector'?s?|limited|bonus\s+track|anniversary|legacy|super\s+deluxe|ultimate)\b(\s+(edition|version))?\s*$")]
    private static partial Regex EditionSuffixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
