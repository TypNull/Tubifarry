using Tubifarry.Indexers.Soulseek.Search.Core;
using Tubifarry.Indexers.Soulseek.Search.Transformers;

namespace Tubifarry.Indexers.Soulseek.Search.Strategies;

public sealed class VolumeVariationStrategy : SearchStrategyBase
{
    public override string Name => "Volume Variation";
    public override SearchTier Tier => SearchTier.Variation;
    public override int Priority => 0;

    public override bool IsEnabled(SlskdSettings settings) => settings.HandleVolumeVariations;

    public override bool CanExecute(SearchContext context, QueryType queryType) =>
        queryType.HasFlag(QueryType.HasVolume) &&
        !string.IsNullOrWhiteSpace(context.SearchAlbum);

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        string? converted = QueryBuilder.ConvertVolumeFormat(context.SearchAlbum);
        if (string.IsNullOrWhiteSpace(converted))
            return null;

        return QueryBuilder.Build(context.SearchArtist, converted);
    }
}

public sealed class RomanNumeralVariationStrategy : SearchStrategyBase
{
    public override string Name => "Roman Numeral";
    public override SearchTier Tier => SearchTier.Variation;
    public override int Priority => 10;

    public override bool IsEnabled(SlskdSettings settings) => settings.HandleVolumeVariations;

    public override bool CanExecute(SearchContext context, QueryType queryType) =>
        queryType.HasFlag(QueryType.HasRomanNumeral) &&
        !string.IsNullOrWhiteSpace(context.SearchAlbum);

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        string? converted = QueryBuilder.ConvertRomanNumeral(context.SearchAlbum);
        if (string.IsNullOrWhiteSpace(converted))
            return null;

        return QueryBuilder.Build(context.SearchArtist, converted);
    }
}
