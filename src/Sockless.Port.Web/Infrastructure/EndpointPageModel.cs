using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Docker;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Base for pages under <c>/endpoints/{endpointId}</c>: resolves the endpoint (404 if unknown)
/// and remembers it as the user's current endpoint.
/// </summary>
public abstract class EndpointPageModel(EngineConnections connections) : PageModel
{
    public EngineConnection Connection { get; private set; } = null!;

    /// <summary>The client, or null while the endpoint isn't online.</summary>
    public DockerClient? Client => Connection.Client;

    public int EndpointId => Connection.Id;

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (!int.TryParse(RouteData.Values[EndpointSelection.RouteKey]?.ToString(), out var id) || connections.Get(id) is not { } connection)
        {
            context.Result = NotFound();
            return;
        }

        Connection = connection;
        EndpointSelection.Remember(Response, id);

        if (await LoadAsync(HttpContext.RequestAborted) is { } shortCircuit)
        {
            context.Result = shortCircuit;
            return;
        }
        await next();
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
