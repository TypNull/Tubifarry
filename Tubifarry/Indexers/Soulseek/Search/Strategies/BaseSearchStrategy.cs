using Tubifarry.Indexers.Soulseek.Search.Core;
using Tubifarry.Indexers.Soulseek.Search.Transformers;

namespace Tubifarry.Indexers.Soulseek.Search.Strategies;

public sealed class BaseSearchStrategy : SearchStrategyBase
{
    public override string Name => "Base Search";
    public override SearchTier Tier => SearchTier.Base;
    public override int Priority => 0;

    public override bool CanExecute(SearchContext context, QueryType queryType)
    {
        // Skip if special case already handled it
        if (queryType.HasFlag(QueryType.VariousArtists) ||
            queryType.HasFlag(QueryType.SelfTitled) ||
            queryType.HasFlag(QueryType.ShortName))
            return false;

        return !string.IsNullOrWhiteSpace(context.SearchArtist) ||
               !string.IsNullOrWhiteSpace(context.SearchAlbum);
    }

    // Build: Artist + Album + Year (if valid) + Type (if needed)
    public override string? GetQuery(SearchContext context, QueryType queryType) =>
        QueryBuilder.Build(
            context.SearchArtist,
            context.SearchAlbum,
            context.Settings.AppendYear && context.HasValidYear ? context.Year : null,
            context.NeedsTypeDisambiguation ? context.ReleaseTypeTag : null);
}
