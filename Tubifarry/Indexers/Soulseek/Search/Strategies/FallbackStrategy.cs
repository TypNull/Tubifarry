using Tubifarry.Indexers.Soulseek.Search.Core;
using Tubifarry.Indexers.Soulseek.Search.Transformers;

namespace Tubifarry.Indexers.Soulseek.Search.Strategies;

public sealed class WildcardStrategy : SearchStrategyBase
{
    public override string Name => "Wildcard";
    public override SearchTier Tier => SearchTier.Fallback;
    public override int Priority => 0;

    public override bool IsEnabled(SlskdSettings settings) => settings.UseFallbackSearch;

    public override bool CanExecute(SearchContext context, QueryType queryType)
    {
        bool hasArtist = !string.IsNullOrWhiteSpace(context.SearchArtist) && context.SearchArtist.Length > 3;
        bool hasAlbum = !string.IsNullOrWhiteSpace(context.SearchAlbum) && context.SearchAlbum.Length > 3;
        return hasArtist || hasAlbum;
    }

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        string artistWildcard = QueryBuilder.BuildWildcard(context.SearchArtist);
        string albumWildcard = QueryBuilder.BuildWildcard(context.SearchAlbum);

        return QueryBuilder.Build(artistWildcard, albumWildcard);
    }
}

public sealed class PartialAlbumStrategy : SearchStrategyBase
{
    public override string Name => "Partial Album";
    public override SearchTier Tier => SearchTier.Fallback;
    public override int Priority => 10;

    public override bool IsEnabled(SlskdSettings settings) => settings.UseFallbackSearch;

    public override bool CanExecute(SearchContext context, QueryType queryType) =>
        !string.IsNullOrWhiteSpace(context.SearchAlbum) && context.SearchAlbum.Length >= 15;

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        string? partial = QueryBuilder.BuildPartial(context.SearchAlbum);
        if (string.IsNullOrWhiteSpace(partial))
            return null;

        return QueryBuilder.Build(context.SearchArtist, partial);
    }
}

public sealed class AliasStrategy : SearchStrategyBase
{
    private const int MinAliasLength = 4;

    public override string Name => "Artist Alias";
    public override SearchTier Tier => SearchTier.Fallback;
    public override int Priority => 20;

    public override bool IsEnabled(SlskdSettings settings) => settings.UseFallbackSearch;

    public override bool CanExecute(SearchContext context, QueryType queryType) =>
        !context.IsVariousArtists &&
        context.Aliases.Count > 0 &&
        context.Aliases.Any(a => !string.IsNullOrWhiteSpace(a) && a.Length >= MinAliasLength);

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        string? alias = context.Aliases
            .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) &&
                                 a.Length >= MinAliasLength &&
                                 !a.Equals(context.Artist, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(alias))
            return null;

        return QueryBuilder.Build(alias, context.SearchAlbum);
    }
}

public sealed class TrackFallbackStrategy : SearchStrategyBase
{
    private const int MinTrackLength = 5;

    public override string Name => "Track Fallback";
    public override SearchTier Tier => SearchTier.Fallback;
    public override int Priority => 30;

    public override bool IsEnabled(SlskdSettings settings) => settings.UseTrackFallback;

    public override bool CanExecute(SearchContext context, QueryType queryType) =>
        context.Tracks.Count > 0 &&
        context.Tracks.Any(t => !string.IsNullOrWhiteSpace(t) && t.Trim().Length >= MinTrackLength);

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        // Find most distinctive track (longer, fewer common words)
        string? track = context.Tracks
            .Where(t => !string.IsNullOrWhiteSpace(t) && t.Trim().Length >= MinTrackLength)
            .OrderByDescending(t => t.Length)
            .FirstOrDefault()?
            .Trim();

        if (string.IsNullOrWhiteSpace(track))
            return null;

        return QueryBuilder.Build(context.SearchArtist, track);
    }
}

public sealed class DistinctiveAlbumStrategy : SearchStrategyBase
{
    public override string Name => "Distinctive Album";
    public override SearchTier Tier => SearchTier.Fallback;
    public override int Priority => 15;

    public override bool IsEnabled(SlskdSettings settings) => settings.UseFallbackSearch;

    public override bool CanExecute(SearchContext context, QueryType queryType) =>
        !string.IsNullOrWhiteSpace(context.SearchAlbum) && context.SearchAlbum.Length >= 10;

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        string distinctive = QueryBuilder.ExtractDistinctive(context.SearchAlbum);
        
        if (string.IsNullOrWhiteSpace(distinctive) ||
            distinctive.Equals(context.SearchAlbum, StringComparison.OrdinalIgnoreCase))
            return null;

        return QueryBuilder.Build(context.SearchArtist, distinctive);
    }
}
