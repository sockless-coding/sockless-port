using Sockless.Port.Setup;

namespace Sockless.Port.Web.Infrastructure;

public static class FirstRunSetupMiddleware
{
    public const string SetupPath = "/setup";

    /// <summary>
    /// Until the first administrator exists, sends every page request to <c>/setup</c>.
    /// Static files and the health check keep working so the setup page renders and
    /// orchestrators don't restart the container.
    /// </summary>
    public static IApplicationBuilder UseFirstRunSetup(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var setup = context.RequestServices.GetRequiredService<SetupState>();
            if (setup.IsComplete || IsExempt(context.Request))
            {
                await next(context);
                return;
            }

            context.Response.Redirect(SetupPath);
        });

    private static bool IsExempt(HttpRequest request)
    {
        var path = request.Path;
        if (path.StartsWithSegments(SetupPath, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/error", StringComparison.OrdinalIgnoreCase))
            return true;

        // Pages never have a file extension; static assets (css, fonts, js, svg) always do.
        return (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
               && Path.HasExtension(path.Value);
    }
}
