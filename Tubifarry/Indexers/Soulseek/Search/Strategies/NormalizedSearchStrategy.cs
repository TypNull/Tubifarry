using Tubifarry.Indexers.Soulseek.Search.Core;
using Tubifarry.Indexers.Soulseek.Search.Transformers;

namespace Tubifarry.Indexers.Soulseek.Search.Strategies;

public sealed class NormalizedSearchStrategy : SearchStrategyBase
{
    public override string Name => "Normalized Search";
    public override SearchTier Tier => SearchTier.Variation;
    public override int Priority => 20;

    public override bool IsEnabled(SlskdSettings settings) => settings.NormalizedSeach;

    public override bool CanExecute(SearchContext context, QueryType queryType) =>
        queryType.HasFlag(QueryType.NeedsNormalization) &&
        (!string.IsNullOrWhiteSpace(context.SearchArtist) || !string.IsNullOrWhiteSpace(context.SearchAlbum));

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        string? artist = QueryNormalizer.NormalizeText(context.SearchArtist);
        string? album = QueryNormalizer.NormalizeText(context.SearchAlbum);

        if (artist == context.SearchArtist && album == context.SearchAlbum)
            return null;

        return QueryBuilder.Build(artist, album);
    }
}
