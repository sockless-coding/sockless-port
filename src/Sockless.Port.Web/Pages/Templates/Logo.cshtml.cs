using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Net.Http.Headers;
using Sockless.Port.Templates;

namespace Sockless.Port.Web.Pages.Templates;

/// <summary>
/// A template's logo, served from this origin. Only logos that a template names are fetched, so
/// this can't be used to download anything else. SVG files are images here, but opened on their
/// own they'd be documents of this origin: they're served sandboxed, with no scripts.
/// </summary>
public sealed class LogoModel(TemplateCatalog catalog, TemplateLogos logos) : PageModel
{
    public async Task<IActionResult> OnGetAsync(int sourceId, string key, CancellationToken cancellationToken)
    {
        if (await catalog.FindAsync(sourceId, key, cancellationToken) is not { Template.Logo: { } url })
            return NotFound();
        if (await logos.GetAsync(url, cancellationToken) is not { } logo)
            return NotFound();

        var headers = Response.Headers;
        headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        headers[HeaderNames.CacheControl] = "private, max-age=86400";
        headers.ContentDisposition = "inline; filename=\"logo\"";
        return File(logo.Bytes, logo.MediaType);
    }
}
