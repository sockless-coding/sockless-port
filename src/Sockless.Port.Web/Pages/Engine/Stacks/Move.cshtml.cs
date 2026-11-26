using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Access;
using Sockless.Port.Engines;
using Sockless.Port.Moves;
using Sockless.Port.Stacks;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Stacks;

/// <summary>
/// Moves a stack Sockless Port manages to another endpoint: pick the target, read what the checks
/// found, choose what happens to the volumes and the source, then start the move (a job of the
/// target's stack). Needs the stack's write role here and on the target.
/// </summary>
public sealed class MoveModel(EngineConnections connections, StackStore store, StackJobs jobs, StackMoves moves) : StackPageModel(connections, store, jobs)
{
    /// <summary>The target endpoint's ID.</summary>
    [BindProperty(SupportsGet = true)]
    public int? To { get; set; }

    /// <summary>Endpoints the stack can go to: other ones the user may deploy this kind of stack on.</summary>
    public IReadOnlyList<EngineConnection> Targets { get; private set; } = [];

    public EngineConnection? Target { get; private set; }

    /// <summary>What the checks found, once a target is chosen.</summary>
    public MovePlan? Plan { get; private set; }

    /// <summary>The form needs the write role: it only makes sense for moving.</summary>
    protected override EndpointPermission ReadPermission => IsSwarm ? EndpointPermission.ManageSwarm : EndpointPermission.Operate;

    private EndpointPermission TargetPermission => IsSwarm ? EndpointPermission.ManageSwarm : EndpointPermission.Operate;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        LoadTargets();
        if (Target is not null && IsManaged)
            Plan = await CheckAsync(Target, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(bool copyVolumes, MoveSourceAfter after, CancellationToken cancellationToken)
    {
        LoadTargets();
        if (Target is not { } target)
        {
            Flash.Error(TempData, "Choose an endpoint you may deploy this stack on.");
            return RedirectToPage(new { endpointId = EndpointId, stackName = StackName });
        }
        AuditTrail.Target(HttpContext, $"{StackName} to {target.Name}");
        try
        {
            await moves.StartAsync(Connection, target, StackName, new MoveOptions(copyVolumes, after), cancellationToken);
        }
        catch (StackJobRejectedException ex)
        {
            Flash.Error(TempData, ex.Message);
            return RedirectToPage(new { endpointId = EndpointId, stackName = StackName, to = target.Id });
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Flash.Error(TempData, EngineFailures.Describe(ex, target));
            return RedirectToPage(new { endpointId = EndpointId, stackName = StackName, to = target.Id });
        }
        await AuditTrail.WriteAsync(HttpContext, "Stacks: move here", $"{StackName} from {Connection.Name}", target);
        Flash.Success(TempData, $"Moving {StackName} from {Connection.Name} to {target.Name}. Follow it here.");
        return RedirectToPage("/Engine/Stacks/Detail", new { endpointId = target.Id, stackName = StackName });
    }

    private void LoadTargets()
    {
        var access = HttpContext.Access()!;
        Targets = [.. HttpContext.Visible(Connections).Where(c => c.Id != EndpointId && access.Can(c.Id, TargetPermission))];
        Target = To is { } to ? Targets.FirstOrDefault(c => c.Id == to) : null;
    }

    private async Task<MovePlan?> CheckAsync(EngineConnection target, CancellationToken cancellationToken)
    {
        try
        {
            return await moves.CheckAsync(Connection, target, StackName, cancellationToken);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            return new MovePlan(StackName, Kind, false, [new MoveFinding(MoveSeverity.Blocker, $"The checks couldn't finish: {EngineFailures.Describe(ex, target)}")],
                [], [], [MoveSourceAfter.Remove]);
        }
    }
}
