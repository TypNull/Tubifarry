using Tubifarry.Indexers.Soulseek.Search.Core;
using Tubifarry.Indexers.Soulseek.Search.Templates;

namespace Tubifarry.Indexers.Soulseek.Search.Strategies;

/// <summary>
/// Search strategy that uses user-configured templates with reflection-based placeholder resolution.
/// Placeholders use {{Property}} syntax and can access nested properties up to 3 levels deep.
/// Examples: {{ArtistQuery}}, {{AlbumTitle}}, {{Artist.Metadata.Value.Aliases[0]}}
/// </summary>
public sealed class TemplateSearchStrategy : SearchStrategyBase
{
    public override string Name => "Template";
    public override SearchTier Tier => SearchTier.Special;
    public override int Priority => 0;

    public override bool IsEnabled(SlskdSettings settings) => 
        !string.IsNullOrWhiteSpace(settings.SearchTemplates);

    public override bool CanExecute(SearchContext context, QueryType queryType) => 
        context.SearchCriteria != null;

    public override string? GetQuery(SearchContext context, QueryType queryType)
    {
        IReadOnlyList<string> templates = TemplateEngine.ParseTemplates(context.Settings.SearchTemplates);
        
        foreach (string template in templates)
        {
            string? result = TemplateEngine.Apply(template, context.SearchCriteria);
            if (!string.IsNullOrWhiteSpace(result))
                return result;
        }
        
        return null;
    }
}
