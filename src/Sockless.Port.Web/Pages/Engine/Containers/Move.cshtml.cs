using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Access;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Moves;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Containers;

/// <summary>
/// Moves a container to another endpoint: pick the target, read what the checks found, choose
/// what happens to the volumes and the original, then start the move in the background (the
/// list of background work follows it). Needs the operator role here and on the target.
/// </summary>
public sealed class MoveModel(EngineConnections connections, ContainerMoves moves) : ContainerPageModel(connections)
{
    /// <summary>The target endpoint's ID.</summary>
    [BindProperty(SupportsGet = true)]
    public int? To { get; set; }

    /// <summary>Other endpoints the user may run containers on.</summary>
    public IReadOnlyList<EngineConnection> Targets { get; private set; } = [];

    public EngineConnection? Target { get; private set; }

    public ContainerMovePlan? Plan { get; private set; }

    /// <summary>The form only makes sense for moving.</summary>
    protected override EndpointPermission ReadPermission => EndpointPermission.Operate;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        LoadTargets();
        if (Target is { } target && Container is not null)
        {
            try
            {
                Plan = await moves.CheckAsync(Connection, target, ContainerId, cancellationToken);
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                Plan = new ContainerMovePlan(DisplayName, IsRunning,
                    [new MoveFinding(MoveSeverity.Blocker, $"The checks couldn't finish: {EngineFailures.Describe(ex, target)}")], [], [], []);
            }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(bool copyVolumes, MoveSourceAfter after, CancellationToken cancellationToken)
    {
        LoadTargets();
        if (Target is not { } target)
        {
            Flash.Error(TempData, "Choose an endpoint you may run containers on.");
            return RedirectToPage(new { endpointId = EndpointId, containerId = ContainerId });
        }
        AuditTrail.Target(HttpContext, $"{DisplayName} to {target.Name}");
        try
        {
            await moves.StartAsync(Connection, target, ContainerId, new MoveOptions(copyVolumes, after), Access.UserName, cancellationToken);
        }
        catch (ContainerMoveRejectedException ex)
        {
            Flash.Error(TempData, ex.Message);
            return RedirectToPage(new { endpointId = EndpointId, containerId = ContainerId, to = target.Id });
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Flash.Error(TempData, EngineFailures.Describe(ex, target));
            return RedirectToPage(new { endpointId = EndpointId, containerId = ContainerId, to = target.Id });
        }
        await AuditTrail.WriteAsync(HttpContext, "Containers: move here", $"{DisplayName} from {Connection.Name}", target);
        Flash.Success(TempData, $"Moving {DisplayName} from {Connection.Name} to {target.Name}. Background tasks follow it.");
        return RedirectToPage("/Tasks/Index");
    }

    private void LoadTargets()
    {
        Targets = [.. HttpContext.Visible(Connections).Where(c => c.Id != EndpointId && Access.Can(c.Id, EndpointPermission.Operate))];
        Target = To is { } to ? Targets.FirstOrDefault(c => c.Id == to) : null;
    }
}
