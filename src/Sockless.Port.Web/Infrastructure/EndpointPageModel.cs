using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Access;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Images;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Base for pages under <c>/endpoints/{endpointId}</c>: resolves the endpoint (404 if unknown, or
/// if the user may not see it), checks the user's role on it, and remembers it as the user's
/// current endpoint. Reading needs <see cref="ReadPermission"/>; any other method than GET or
/// HEAD needs <see cref="WritePermission"/>, checked after <see cref="LoadAsync"/> so a page can
/// decide from what it loaded (a swarm stack needs more than a Compose one).
/// </summary>
public abstract class EndpointPageModel(EngineConnections connections) : PageModel
{
    public EngineConnection Connection { get; private set; } = null!;

    /// <summary>The client, or null while the endpoint isn't online.</summary>
    public DockerClient? Client => Connection.Client;

    public int EndpointId => Connection.Id;

    /// <summary>Every endpoint's connection; check <see cref="Access"/> before using another endpoint.</summary>
    protected EngineConnections Connections => connections;

    public UserAccess Access { get; private set; } = null!;

    /// <summary>What opening the page needs. Forms that only make sense for changing things (create, console) need more.</summary>
    protected virtual EndpointPermission ReadPermission => EndpointPermission.View;

    /// <summary>What the page's posts need.</summary>
    protected virtual EndpointPermission WritePermission => EndpointPermission.Operate;

    /// <summary>May the user use this page's actions? Views hide them otherwise (<c>sp-can="write"</c>).</summary>
    public bool CanWrite => Access.Can(EndpointId, WritePermission);

    public bool CanOperate => Access.Can(EndpointId, EndpointPermission.Operate);

    public bool CanManageSwarm => Access.Can(EndpointId, EndpointPermission.ManageSwarm);

    /// <summary>Online, and the engine can ask registries for digests (not Podman before 6.2). Hides the update-check actions.</summary>
    public bool CanCheckUpdates => Client is not null && ImageUpdates.CanCheck(Connection);

    /// <summary>What the audit log names as the target of this page's posts; null for the route's values.</summary>
    public virtual string? AuditTarget => null;

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (!int.TryParse(RouteData.Values[EndpointSelection.RouteKey]?.ToString(), out var id) || connections.Get(id) is not { } connection)
        {
            context.Result = NotFound();
            return;
        }

        // Endpoints the user may not see don't exist for them.
        if (HttpContext.Access() is not { } access || !access.CanView(id))
        {
            context.Result = NotFound();
            return;
        }

        Connection = connection;
        Access = access;
        if (!access.Can(id, ReadPermission))
        {
            context.Result = Denied(ReadPermission);
            return;
        }
        EndpointSelection.Remember(Response, id);

        if (await LoadAsync(HttpContext.RequestAborted) is { } shortCircuit)
        {
            context.Result = shortCircuit;
            return;
        }
        if (!HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method) && !CanWrite)
        {
            context.Result = Denied(WritePermission);
            return;
        }
        PageAccess.SetWrite(HttpContext, CanWrite);
        await next();
    }

    private StatusCodeResult Denied(EndpointPermission needed)
    {
        AuditTrail.Deny(HttpContext, needed switch
        {
            EndpointPermission.ManageSwarm => $"Needs the swarm operator role on {Connection.Name}.",
            EndpointPermission.Operate => $"Needs the operator role on {Connection.Name}.",
            _ => $"No access to {Connection.Name}.",
        });
        return StatusCode(StatusCodes.Status403Forbidden);
    }

    /// <summary>Loads what every handler of the page needs. Return a result to stop (e.g. 404).</summary>
    protected virtual Task<IActionResult?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<IActionResult?>(null);

    /// <summary>Runs an engine call for a form post, turning failures into a flash message.</summary>
    protected async Task<bool> TryEngineAsync(Func<DockerClient, Task> action, string? success = null)
    {
        if (Client is not { } client)
        {
            Flash.Error(TempData, "The engine is offline, so nothing was changed.");
            return false;
        }
        try
        {
            await action(client);
            if (success is not null)
                Flash.Success(TempData, success);
            return true;
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, HttpContext.RequestAborted))
        {
            Flash.Error(TempData, EngineFailures.Describe(ex, Connection));
            return false;
        }
    }
}
