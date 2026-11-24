using Microsoft.AspNetCore.Http.Features;
using Sockless.Port.Audit;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// The webhook git hosts call after a push: <c>POST /hooks/stacks/{id}</c>, one address per stack.
/// No session and no antiforgery token (a host can't have either): the call proves itself with
/// the stack's secret (<see cref="GitWebhooks.Verify"/>). A valid push to the stack's branch queues
/// a sync (<see cref="GitStackTriggers"/>), answered 202 at once; the sync itself is audited when
/// it ends. Calls with a wrong secret are audited as denied. Rate-limited per client address.
/// </summary>
public static class GitHooks
{
    public const string RateLimitPolicy = "git-hooks";

    /// <summary>Calls per client address per minute.</summary>
    public const int PerMinute = 30;

    public static string Path(string id) => $"/hooks/stacks/{id}";

    public static void MapGitHooks(this IEndpointRouteBuilder app) =>
        app.MapPost("/hooks/stacks/{id}", HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimitPolicy);

    internal static async Task<IResult> HandleAsync(string id, HttpContext context, StackStore store, GitStackTriggers triggers, EngineConnections connections,
        AuditLog audit)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower))
            return Results.NotFound();
        if (context.Request.ContentLength > GitWebhooks.MaxBody)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (await ReadBodyAsync(context) is not { } body)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        if (await store.FindWebhookAsync(id, context.RequestAborted) is not { } found)
            return Results.NotFound();
        var (stack, repository) = found;
        var endpoint = connections.Get(stack.EndpointId);
        string? Header(string name) => context.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;

        string? secret;
        try
        {
            secret = store.WebhookSecret(repository);
        }
        catch (GitException)
        {
            secret = null;
        }
        var host = secret is null ? null : GitWebhooks.Verify(Header, body, secret);
        if (host is null)
        {
            await audit.WriteAsync(new AuditRecord(null, "webhook", "Stacks: webhook", stack.Name, stack.EndpointId, endpoint?.Name, AuditOutcome.Denied,
                "The call didn't carry the stack's webhook secret.", context.Connection.RemoteIpAddress?.ToString()));
            return Results.Text("The signature or token doesn't match this webhook's secret.\n", statusCode: StatusCodes.Status401Unauthorized);
        }

        return GitWebhooks.Read(Header, body, repository.Branch) switch
        {
            WebhookPing => Results.Text("Sockless Port is listening.\n"),
            WebhookIgnored ignored => Results.Text($"Ignored: {ignored.Reason}\n", statusCode: StatusCodes.Status202Accepted),
            WebhookPush push => Queue(push.Commit),
        };

        IResult Queue(string? commit)
        {
            triggers.Request(stack.Id, $"webhook ({host})");
            return Results.Text(commit is not null
                ? $"Queued a fetch and redeploy of {stack.Name} for {GitSource.Short(commit)}.\n"
                : $"Queued a fetch and redeploy of {stack.Name}.\n", statusCode: StatusCodes.Status202Accepted);
        }
    }

    /// <summary>The body, at most <see cref="GitWebhooks.MaxBody"/> bytes; null when it's larger.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = GitWebhooks.MaxBody + 1;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > GitWebhooks.MaxBody)
                    return null;
            }
        }
        catch (BadHttpRequestException)
        {
            return null;
        }
        return buffer.ToArray();
    }
}
