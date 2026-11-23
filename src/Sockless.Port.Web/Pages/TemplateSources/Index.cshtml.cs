using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Templates;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.TemplateSources;

/// <summary>The template feeds every endpoint's Templates page offers. Administrators only.</summary>
public sealed class IndexModel(TemplateCatalog catalog) : PageModel
{
    public IReadOnlyList<TemplateSourceInfo> Sources { get; private set; } = [];

    /// <summary>Suggested feeds that aren't added yet.</summary>
    public IReadOnlyList<SuggestedSource> Suggestions { get; private set; } = [];

    [BindProperty]
    public SourceInput Input { get; set; } = new();

    public sealed class SourceInput
    {
        public string? Name { get; set; }

        public string? Url { get; set; }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        AuditTrail.Action(HttpContext, "Template sources: add");
        AuditTrail.Target(HttpContext, Input.Name?.Trim());
        try
        {
            var source = await catalog.AddAsync(Input.Name, Input.Url, cancellationToken);
            Flash.Success(TempData, $"Added {source.Name}: {source.TemplateCount} {(source.TemplateCount == 1 ? "template" : "templates")}.");
            return RedirectToPage();
        }
        catch (TemplateSourceRejectedException ex)
        {
            ModelState.AddModelError($"Input.{ex.Field}", ex.Message);
            await LoadAsync(cancellationToken);
            return Page();
        }
    }

    /// <param name="url">One of <see cref="TemplateCatalog.Suggestions"/>.</param>
    public async Task<IActionResult> OnPostSuggestedAsync(string? url, CancellationToken cancellationToken)
    {
        AuditTrail.Action(HttpContext, "Template sources: add");
        if (TemplateCatalog.Suggestions.FirstOrDefault(s => s.Url == url) is not { } suggestion)
            return NotFound();
        AuditTrail.Target(HttpContext, suggestion.Name);
        try
        {
            var source = await catalog.AddAsync(suggestion.Name, suggestion.Url, cancellationToken);
            Flash.Success(TempData, $"Added {source.Name}: {source.TemplateCount} {(source.TemplateCount == 1 ? "template" : "templates")}.");
        }
        catch (TemplateSourceRejectedException ex)
        {
            Flash.Error(TempData, $"Couldn't add {suggestion.Name}. {ex.Message}");
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRefreshAsync(int id, CancellationToken cancellationToken)
    {
        AuditTrail.Action(HttpContext, "Template sources: refresh");
        var source = await catalog.RefreshAsync(id, cancellationToken);
        if (source is null)
            return NotFound();
        AuditTrail.Target(HttpContext, source.Name);
        if (source.Error is { } error)
            Flash.Error(TempData, $"Couldn't download {source.Name} again: {error} Its templates from the last download stay available.");
        else
            Flash.Success(TempData, $"Downloaded {source.Name} again: {source.TemplateCount} {(source.TemplateCount == 1 ? "template" : "templates")}.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id, CancellationToken cancellationToken)
    {
        AuditTrail.Action(HttpContext, "Template sources: remove");
        var source = (await catalog.SourcesAsync(cancellationToken)).FirstOrDefault(s => s.Id == id && !s.BuiltIn);
        if (source is null || !await catalog.RemoveAsync(id, cancellationToken))
            return NotFound();
        AuditTrail.Target(HttpContext, source.Name);
        Flash.Success(TempData, $"Removed {source.Name}. Stacks made from its templates are left as they are.");
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Sources = await catalog.SourcesAsync(cancellationToken);
        Suggestions = TemplateCatalog.Suggestions.Where(s => Sources.All(source => source.Url != s.Url)).ToList();
    }
}
