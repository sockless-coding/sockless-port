using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Access;
using Sockless.Port.Engines;
using Sockless.Port.Templates;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Templates;

/// <summary>
/// The templates the endpoint can deploy, from every source: searchable, by category and source.
/// Operators only, since all a template does is create a stack.
/// </summary>
public sealed class IndexModel(EngineConnections connections, TemplateCatalog catalog) : EndpointPageModel(connections)
{
    public const int PageSize = 48;

    protected override EndpointPermission ReadPermission => EndpointPermission.Operate;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Category { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? Source { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<TemplateSourceInfo> Sources { get; private set; } = [];

    /// <summary>Categories of the templates offered here, most used first.</summary>
    public IReadOnlyList<string> Categories { get; private set; } = [];

    /// <summary>The templates on this page.</summary>
    public IReadOnlyList<SourcedTemplate> Templates { get; private set; } = [];

    /// <summary>Templates matching the filters.</summary>
    public int Total { get; private set; }

    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);

    /// <summary>Swarm templates left out because this isn't a swarm manager (or the user can't deploy them).</summary>
    public bool HidesSwarm { get; private set; }

    public bool Filtered => !string.IsNullOrWhiteSpace(Q) || !string.IsNullOrEmpty(Category) || Source is not null;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Sources = await catalog.SourcesAsync(cancellationToken);
        var all = await catalog.TemplatesAsync(cancellationToken);
        var offered = all.Where(t => TemplateDisplay.Offered(t.Template, Connection, Access, EndpointId)).ToList();
        HidesSwarm = all.Any(t => t.Template.Kind == TemplateKind.Swarm) && offered.All(t => t.Template.Kind != TemplateKind.Swarm);

        Categories = offered.SelectMany(t => t.Template.Categories)
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).Take(40).ToList();

        var terms = (Q ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = offered
            .Where(t => Source is null || t.Source.Id == Source)
            .Where(t => string.IsNullOrEmpty(Category) || t.Template.Categories.Contains(Category, StringComparer.OrdinalIgnoreCase))
            .Where(t => terms.All(term => Matches(t.Template, term)))
            .ToList();
        Total = matches.Count;
        PageNumber = Math.Clamp(PageNumber, 1, PageCount);
        Templates = matches.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
    }

    private static bool Matches(AppTemplate template, string term) =>
        template.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
        || template.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
        || template.Image?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
        || template.Categories.Any(c => c.Contains(term, StringComparison.OrdinalIgnoreCase));
}
