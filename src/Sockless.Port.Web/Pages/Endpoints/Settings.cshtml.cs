using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Sockless.Port.Agents;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Endpoints;

/// <summary>An endpoint's settings: name, the agent's install command and certificate, re-enrolment and removal.</summary>
public sealed class SettingsModel(EngineConnections connections, EndpointRegistry registry, AgentRenewals renewals, IOptions<PortOptions> options, TimeProvider time)
    : PageModel
{
    public EngineConnection Connection { get; private set; } = null!;

    /// <summary>Which install command to show.</summary>
    [BindProperty(SupportsGet = true)]
    public AgentInstallVariant Variant { get; set; }

    [BindProperty]
    public string Name { get; set; } = "";

    /// <summary>The address for a new enrolment (an edge agent: the server's address it dials); empty keeps the current one.</summary>
    [BindProperty]
    public string? Address { get; set; }

    public DateTimeOffset Now => time.GetUtcNow();

    public string Image => AgentCommands.DefaultImage(options.Value.AgentImage);

    public TcpAddress? AgentAddress => Connection.Address as TcpAddress;

    /// <summary>The install command while the enrolment can still be used.</summary>
    public AgentCommand? InstallCommand => Connection.Enrolment is { } enrolment && enrolment.ExpiresAt > Now
        ? Connection.Edge is { } edge ? AgentCommands.InstallEdge(Variant, enrolment.Token, edge.ServerUrl, Image)
        : AgentAddress is { } address ? AgentCommands.Install(Variant, enrolment.Token, address.Port, Image)
        : null
        : null;

    public AgentCommand? UpdateCommand =>
        Connection.Edge is { } edge ? AgentCommands.UpdateEdge(Variant, edge.ServerUrl, Image)
        : AgentAddress is { } address ? AgentCommands.Update(Variant, address.Port, Image)
        : null;

    /// <summary>The address as typed into the form: host, plus the port when it isn't the default; for an edge agent, the server's address.</summary>
    public string AddressText => Connection.Edge is { } edge ? edge.ServerUrl
        : AgentAddress is { } a
            ? (a.Host.Contains(':', StringComparison.Ordinal) ? $"[{a.Host}]" : a.Host) + (a.Port == Docker.Agent.AgentProtocol.DefaultPort ? "" : $":{a.Port}")
            : "";

    /// <summary>When the hourly check renews the agent's certificate.</summary>
    public DateTimeOffset? RenewsAt => Connection.Agent?.CertificateNotAfter is { } notAfter ? notAfter - AgentRenewals.RenewBefore : null;

    public IActionResult OnGet(int endpointId)
    {
        if (!Load(endpointId))
            return NotFound();
        Name = Connection.Name;
        EndpointSelection.Remember(Response, endpointId);
        return Page();
    }

    /// <summary>The live part: state, install command and agent details, refreshed on state changes.</summary>
    public IActionResult OnGetLive(int endpointId) => Load(endpointId) ? Partial("_EndpointLive", this) : NotFound();

    public async Task<IActionResult> OnPostRenameAsync(int endpointId, CancellationToken cancellationToken)
    {
        if (!Load(endpointId))
            return NotFound();
        var errors = await registry.RenameAsync(endpointId, Name ?? "", cancellationToken);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
                ModelState.AddModelError(error.Field, error.Message);
            return Page();
        }
        Flash.Success(TempData, $"Renamed the endpoint to {Name!.Trim()}.");
        return RedirectToPage(new { endpointId });
    }

    public async Task<IActionResult> OnPostReenrolAsync(int endpointId, CancellationToken cancellationToken)
    {
        if (!Load(endpointId))
            return NotFound();
        if (!Connection.IsAgent)
            return BadRequest();
        var errors = await registry.ReenrolAsync(endpointId, Address, cancellationToken);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
                ModelState.AddModelError(error.Field, error.Message);
            Name = Connection.Name;
            return Page();
        }
        Flash.Success(TempData, $"Created a new install command for {Connection.Name}. The previous certificate no longer works.");
        return RedirectToPage(new { endpointId, variant = Variant });
    }

    public async Task<IActionResult> OnPostRenewAsync(int endpointId, CancellationToken cancellationToken)
    {
        if (!Load(endpointId))
            return NotFound();
        if (!Connection.IsAgent)
            return BadRequest();
        AuditTrail.Action(HttpContext, "Endpoints: renew agent certificate");
        if (await renewals.RenewAsync(endpointId, cancellationToken) is { } error)
            Flash.Error(TempData, error);
        else
            Flash.Success(TempData, Connection.Agent?.CertificateNotAfter is { } notAfter
                ? $"Renewed the certificate of {Connection.Name}'s agent. It's valid until {Format.Timestamp(notAfter)}."
                : $"Renewed the certificate of {Connection.Name}'s agent.");
        return RedirectToPage(new { endpointId, variant = Variant });
    }

    public async Task<IActionResult> OnPostRemoveAsync(int endpointId, CancellationToken cancellationToken)
    {
        if (!Load(endpointId))
            return NotFound();
        if (Connection.IsLocal)
            return BadRequest();
        var name = await registry.RemoveAsync(endpointId, cancellationToken);
        if (name is null)
            return NotFound();
        Flash.Success(TempData, $"Removed {name}. Its agent keeps running on the host until you remove it there.");
        return RedirectToPage("/Endpoints/Index");
    }

    private bool Load(int endpointId)
    {
        if (connections.Get(endpointId) is not { } connection)
            return false;
        Connection = connection;
        return true;
    }
}
