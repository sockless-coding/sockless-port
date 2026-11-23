using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Engines;
using Sockless.Port.Notifications;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Notifications;

/// <summary>Channels, rules and what was sent. Administrators only.</summary>
public sealed class IndexModel(Notifier notifier, EngineConnections connections) : PageModel
{
    public IReadOnlyList<ChannelInfo> Channels { get; private set; } = [];

    public IReadOnlyList<RuleInfo> Rules { get; private set; } = [];

    public NotificationPage History { get; private set; } = new([], 1, false);

    public IReadOnlyCollection<Alert> Active { get; private set; } = [];

    [BindProperty]
    public string? PublicUrl { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public string EndpointName(int? id) => id is null ? "Every endpoint" : connections.Get(id.Value)?.Name ?? $"Endpoint {id}";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        PublicUrl = await notifier.PublicUrlAsync(cancellationToken);
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostPublicUrlAsync(CancellationToken cancellationToken)
    {
        AuditTrail.Action(HttpContext, "Notifications: set address");
        AuditTrail.Target(HttpContext, PublicUrl?.Trim());
        if (await notifier.SavePublicUrlAsync(PublicUrl, cancellationToken) is { } problem)
        {
            ModelState.AddModelError(nameof(PublicUrl), problem);
            await LoadAsync(cancellationToken);
            return Page();
        }
        Flash.Success(TempData, string.IsNullOrWhiteSpace(PublicUrl) ? "Notifications won't carry links." : "Notifications link to pages at that address from now on.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(int id, CancellationToken cancellationToken)
    {
        AuditTrail.Action(HttpContext, "Notifications: test channel");
        if (await notifier.GetChannelAsync(id, cancellationToken) is not { } channel)
            return NotFound();
        AuditTrail.Target(HttpContext, channel.Name);
        if (await notifier.SendTestAsync(id, cancellationToken) is { } error)
            Flash.Error(TempData, $"Couldn't send a test to {channel.Name}: {error}");
        else
            Flash.Success(TempData, $"Sent a test notification to {channel.Name}. Check that it arrived.");
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Channels = await notifier.ChannelsAsync(cancellationToken);
        Rules = await notifier.RuleInfosAsync(cancellationToken);
        History = await notifier.HistoryAsync(PageNumber, cancellationToken);
        Active = notifier.Active;
    }
}
