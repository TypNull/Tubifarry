using Tubifarry.Indexers.Soulseek.Search.Core;
using Tubifarry.Indexers.Soulseek.Search.Transformers;

namespace Tubifarry.Indexers.Soulseek.Search.Strategies;

public sealed class VariousArtistsStrategy : SearchStrategyBase
{
    public override string Name => "Various Artists";
    public override SearchTier Tier => SearchTier.Special;
    public override int Priority => 10;

    public override bool CanExecute(SearchContext context, QueryType queryType) =>
        queryType.HasFlag(QueryType.VariousArtists) &&
        !string.IsNullOrWhiteSpace(context.SearchAlbum);

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        if (context.HasValidYear)
            return QueryBuilder.Build(context.SearchAlbum, context.Year, context.ReleaseTypeTag);

        if (context.NeedsTypeDisambiguation)
            return QueryBuilder.Build(context.SearchAlbum, context.ReleaseTypeTag);

        return context.SearchAlbum;
    }
}

public sealed class SelfTitledStrategy : SearchStrategyBase
{
    public override string Name => "Self-Titled";
    public override SearchTier Tier => SearchTier.Special;
    public override int Priority => 20;

    public override bool CanExecute(SearchContext context, QueryType queryType) =>
        queryType.HasFlag(QueryType.SelfTitled) &&
        !queryType.HasFlag(QueryType.VariousArtists) &&
        !string.IsNullOrWhiteSpace(context.SearchArtist);

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        if (context.HasValidYear)
            return QueryBuilder.Build(context.SearchArtist, context.Year, context.ReleaseTypeTag);

        if (context.NeedsTypeDisambiguation)
            return QueryBuilder.Build(context.SearchArtist, context.ReleaseTypeTag);

        return context.SearchArtist;
    }
}

public sealed class ShortNameStrategy : SearchStrategyBase
{
    public override string Name => "Short Name";
    public override SearchTier Tier => SearchTier.Special;
    public override int Priority => 30;

    public override bool CanExecute(SearchContext context, QueryType queryType) =>
        queryType.HasFlag(QueryType.ShortName) &&
        !queryType.HasFlag(QueryType.VariousArtists) &&
        !queryType.HasFlag(QueryType.SelfTitled);

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        // Short album names need maximum context
        return QueryBuilder.Build(
            context.SearchArtist,
            context.SearchAlbum,
            context.HasValidYear ? context.Year : null,
            context.ReleaseTypeTag);
    }
}
