using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Engines;
using Sockless.Port.Notifications;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Endpoints;

public sealed class AddModel(EndpointRegistry registry, Notifier notifier) : PageModel
{
    /// <summary><c>edge</c>: the agent dials this server; otherwise this server connects to the agent.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Mode { get; set; }

    public bool Edge => string.Equals(Mode, "edge", StringComparison.OrdinalIgnoreCase);

    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public string Address { get; set; } = "";

    /// <summary>Edge mode: this server's address as the agent's host reaches it.</summary>
    [BindProperty]
    public string ServerUrl { get; set; } = "";

    /// <summary>A swarm node's page links here with its name and address filled in.</summary>
    public async Task OnGetAsync(string? name, string? address, CancellationToken cancellationToken)
    {
        Name = name?.Trim() ?? "";
        Address = address?.Trim() ?? "";
        ServerUrl = await DefaultServerUrlAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var (id, errors) = await registry.AddAgentAsync(new AgentEndpointInput(Name ?? "", Address ?? "", Edge, ServerUrl), cancellationToken);
        if (id is null)
        {
            foreach (var error in errors)
                ModelState.AddModelError(error.Field, error.Message);
            return Page();
        }
        Flash.Success(TempData, $"Added {Name!.Trim()}. Run the install command on its host within the hour.");
        return RedirectToPage("/Endpoints/Settings", new { endpointId = id });
    }

    /// <summary>The address links in notifications use, else the one this page was opened at.</summary>
    private async Task<string> DefaultServerUrlAsync(CancellationToken cancellationToken) =>
        await notifier.PublicUrlAsync(cancellationToken) is { Length: > 0 } configured
            ? configured.TrimEnd('/')
            : $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
}
