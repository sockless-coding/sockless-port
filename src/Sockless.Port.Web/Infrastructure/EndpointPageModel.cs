using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Base for pages under <c>/endpoints/{endpointId}</c>: resolves the endpoint (404 if unknown)
/// and remembers it as the user's current endpoint.
/// </summary>
public abstract class EndpointPageModel(EngineConnections connections) : PageModel
{
    public EngineConnection Connection { get; private set; } = null!;

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (!int.TryParse(RouteData.Values[EndpointSelection.RouteKey]?.ToString(), out var id) || connections.Get(id) is not { } connection)
        {
            context.Result = NotFound();
            return;
        }

        Connection = connection;
        EndpointSelection.Remember(Response, id);
        await next();
    }
}
