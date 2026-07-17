using NLog;
using NzbDrone.Core.Indexers;
using Tubifarry.Core.Utilities;
using Tubifarry.Indexers.Soulseek.Search.Transformers;

namespace Tubifarry.Indexers.Soulseek.Search.Core;

public interface ISlskdSearchChain
{
    LazyIndexerPageableRequestChain BuildChain(SearchContext context, SearchExecutor searchExecutor);
}

public sealed class SearchPipeline : ISlskdSearchChain
{
    private readonly IReadOnlyList<ISearchStrategy> _strategies;
    private readonly Logger _logger;

    public SearchPipeline(IEnumerable<ISearchStrategy> strategies, Logger logger)
    {
        _logger = logger;
        _strategies = strategies
            .OrderBy(s => s.Tier)
            .ThenBy(s => s.Priority)
            .ToList()
            .AsReadOnly();

        _logger.Debug($"SearchPipeline: {_strategies.Count} strategies loaded");
    }

    public LazyIndexerPageableRequestChain BuildChain(SearchContext context, SearchExecutor searchExecutor)
    {
        var chain = new LazyIndexerPageableRequestChain(context.Settings.MinimumResults);

        QueryType queryType = QueryAnalyzer.Analyze(context);
        SearchContext ctx = context with { QueryType = queryType };

        _logger.Debug($"Search: Artist='{ctx.Artist}', Album='{ctx.Album}', Type={queryType}");

        bool isFirst = true;
        foreach (var strategy in _strategies)
        {
            if (!strategy.IsEnabled(ctx.Settings) || !strategy.CanExecute(ctx, queryType))
                continue;

            Func<IEnumerable<IndexerRequest>> factory = () => ExecuteStrategy(strategy, ctx, queryType, searchExecutor);

            if (isFirst)
            {
                chain.AddFactory(factory);
                isFirst = false;
            }
            else
            {
                chain.AddTierFactory(factory);
            }
        }

        return chain;
    }

    private IEnumerable<IndexerRequest> ExecuteStrategy(
        ISearchStrategy strategy,
        SearchContext context,
        QueryType queryType,
        SearchExecutor searchExecutor)
    {
        string? query = strategy.GetQuery(context, queryType);

        if (string.IsNullOrWhiteSpace(query))
            return [];

        query = ResolveRestrictedTerms(query);

        if (string.IsNullOrWhiteSpace(query))
            return [];

        if (context.ProcessedSearches.Contains(query))
        {
            _logger.Trace($"[{strategy.Name}] Skip duplicate: '{query}'");
            return [];
        }

        context.ProcessedSearches.Add(query);
        _logger.Debug($"[{strategy.Name}] Search: '{query}'");

        try
        {
            var searchQuery = SearchQuery.FromContext(context) with { SearchText = query };
            return searchExecutor(searchQuery).ToList();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"[{strategy.Name}] Error: '{query}'");
            return [];
        }
    }

    private string ResolveRestrictedTerms(string query)
    {
        if (!SlskdTextProcessor.ContainsBlockedTerms(query))
            return query;

        for (int variant = 0; variant < 3; variant++)
        {
            string candidate = SlskdTextProcessor.RewriteRestrictedTerms(query, variant);
            if (candidate != query && !SlskdTextProcessor.ContainsBlockedTerms(candidate))
                return candidate;
        }

        return string.Empty;
    }
}
