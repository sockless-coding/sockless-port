using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Sockless.Port.Access;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Pages.Users;

public sealed class IndexModel(UserAdmin users, EngineConnections connections, IOptions<PortOptions> options) : PageModel
{
    public IReadOnlyList<UserSummary> Users { get; private set; } = [];

    public IReadOnlyList<EngineConnection> Endpoints { get; } = connections.All;

    public bool SingleSignOn => options.Value.Oidc.Enabled;

    public async Task OnGetAsync(CancellationToken cancellationToken) => Users = await users.ListAsync(cancellationToken);
}
