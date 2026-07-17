using Tubifarry.Indexers.Soulseek.Search.Core;

namespace Tubifarry.Indexers.Soulseek.Search.Strategies;

public sealed class BlockedTermEvidenceStrategy : SearchStrategyBase
{
    public override string Name => "Blocked Term Evidence";
    public override SearchTier Tier => SearchTier.Fallback;
    public override int Priority => 50;

    public override bool CanExecute(SearchContext context, QueryType queryType)
        => SlskdTextProcessor.ContainsBlockedTerms(BuildRawQuery(context));

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        string strippedQuery = SlskdTextProcessor.RemoveBlockedTerms(BuildRawQuery(context));

        foreach (string evidenceTrack in SlskdTextProcessor.GetBlockedTermEvidenceTracks(context.Tracks, context.SearchAlbum))
        {
            string candidate = CombineQueryWithEvidence(strippedQuery, evidenceTrack);
            if (!context.ProcessedSearches.Contains(candidate))
                return candidate;
        }

        if (context.HasValidYear && !string.IsNullOrWhiteSpace(strippedQuery))
        {
            string queryWithYear = $"{strippedQuery} {context.Year}";
            if (!context.ProcessedSearches.Contains(queryWithYear))
                return queryWithYear;
        }

        return string.IsNullOrWhiteSpace(strippedQuery) ? null : strippedQuery;
    }

    private static string CombineQueryWithEvidence(string strippedQuery, string evidenceTrack)
    {
        if (string.IsNullOrWhiteSpace(strippedQuery))
            return evidenceTrack;

        return strippedQuery.Contains(evidenceTrack, StringComparison.OrdinalIgnoreCase)
            ? strippedQuery
            : $"{strippedQuery} {evidenceTrack}";
    }

    private static string BuildRawQuery(SearchContext context)
        => SlskdTextProcessor.BuildSearchText(context.SearchArtist, context.SearchAlbum);
}
