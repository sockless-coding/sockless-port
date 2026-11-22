using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Audit;
using Sockless.Port.Engines;
using Sockless.Port.Settings;

namespace Sockless.Port.Web.Pages.Audit;

public sealed class IndexModel(AuditLog audit, EngineConnections connections, PortSettings settings) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "user")]
    public string? UserName { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? Endpoint { get; set; }

    [BindProperty(SupportsGet = true)]
    public AuditOutcome? Outcome { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public AuditPage Entries { get; private set; } = new([], 1, false);

    public IReadOnlyList<string> UserNames { get; private set; } = [];

    public IReadOnlyList<EngineConnection> Endpoints { get; } = connections.All;

    public int RetentionDays => settings.Current.AuditRetentionDays;

    public bool Filtered => UserName is { Length: > 0 } || Endpoint is not null || Outcome is not null || Q is { Length: > 0 };

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Entries = await audit.QueryAsync(new AuditQuery(UserName, Endpoint, Outcome, Q, PageNumber), cancellationToken);
        UserNames = await audit.UserNamesAsync(cancellationToken);
    }

    public object Route(int page) => new { user = UserName, Endpoint, Outcome, Q, p = page == 1 ? (int?)null : page };
}
