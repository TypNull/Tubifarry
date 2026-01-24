namespace Tubifarry.Indexers.Soulseek.Search.Core;

public interface ISearchStrategy
{
    string Name { get; }
    SearchTier Tier { get; }
    int Priority { get; }
    bool IsEnabled(SlskdSettings settings);
    bool CanExecute(SearchContext context, QueryType queryType);
    string? GetQuery(SearchContext context, QueryType queryType);
}

public abstract class SearchStrategyBase : ISearchStrategy
{
    public abstract string Name { get; }
    public abstract SearchTier Tier { get; }
    public virtual int Priority => 0;

    public virtual bool IsEnabled(SlskdSettings settings) => true;
    public abstract bool CanExecute(SearchContext context, QueryType queryType);
    public abstract string? GetQuery(SearchContext context, QueryType queryType);
}
