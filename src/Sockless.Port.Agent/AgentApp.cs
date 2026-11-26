using System.Net;
using System.Reflection;
using System.Security.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sockless.Port.Docker.Agent;

namespace Sockless.Port.Agent;

/// <summary>Builds the agent's web app: Kestrel with TLS, the enrolment and info endpoints, and the relay.</summary>
public static partial class AgentApp
{
    /// <summary>The agent's version, e.g. <c>0.2.0</c> (build metadata removed).</summary>
    public static string Version { get; } =
        (typeof(AgentApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static WebApplication Create(AgentOptions options, string[]? args = null, IPAddress? listen = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        var identity = new AgentIdentity(options.DataPath);
        var startedAt = DateTimeOffset.UtcNow;

        // Every relayed request and health check would otherwise log twice at Information.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton<EngineRelay>();
        if (options.Server is not null)
        {
            builder.Services.AddSingleton<TunnelClient>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<TunnelClient>());
        }

        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // Image builds and archive uploads can be large; the server already limits them.
            kestrel.Limits.MaxRequestBodySize = null;
            void Configure(ListenOptions endpoint)
            {
                endpoint.Protocols = HttpProtocols.Http1;
                endpoint.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificateSelector = (_, _) => identity.ServerCertificate,
                    ClientCertificateMode = ClientCertificateMode.AllowCertificate,
                    // Checked per request against the CA from enrolment (see the gate below).
                    ClientCertificateValidation = (_, _, _) => true,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                });
                var hijack = new HijackMiddleware(identity, options,
                    endpoint.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger<HijackMiddleware>());
                endpoint.Use(next => connection => hijack.InvokeAsync(connection, next));
            }

            // In edge mode nothing connects from outside: the tunnel's streams arrive on loopback.
            listen ??= options.Server is null ? null : IPAddress.Loopback;
            if (listen is null)
                kestrel.ListenAnyIP(options.Port, Configure);
            else
                kestrel.Listen(listen, options.Port, Configure);
        });

        var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Sockless.Port.Agent");

        // Only the server's certificate gets past here, apart from enrolment and the health check.
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path == AgentProtocol.HealthPath || path.StartsWithSegments("/agent/enrol"))
            {
                await next(context);
                return;
            }
            if (!identity.IsEnrolled)
            {
                await ErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "This agent isn't enrolled yet. Enrol it from Sockless Port.");
                return;
            }
            var certificate = context.Connection.ClientCertificate ?? await context.Connection.GetClientCertificateAsync(context.RequestAborted);
            if (!identity.IsTrustedServer(certificate))
            {
                await ErrorAsync(context, StatusCodes.Status403Forbidden, "Only the Sockless Port server this agent is enrolled with can use it.");
                return;
            }
            await next(context);
        });

        app.MapGet(AgentProtocol.HealthPath, () => Results.Text("ok"));

        app.MapPost(AgentProtocol.CsrPath, async (HttpContext context) =>
        {
            if (await ReadAsync(context, AgentJsonContext.Default.AgentCsrRequest) is not { } request)
                return;
            if (EnrolmentProblem(identity, options, request.TokenId) is { } problem)
            {
                await ErrorAsync(context, problem.Status, problem.Message);
                return;
            }
            var csr = identity.CreateSigningRequest();
            var response = new AgentCsrResponse
            {
                CsrPem = csr,
                Proof = AgentProtocol.CsrProof(options.Token!.Secret, request.Nonce, csr),
                AgentVersion = Version,
            };
            await context.Response.WriteAsJsonAsync(response, AgentJsonContext.Default.AgentCsrResponse);
        });

        app.MapPost(AgentProtocol.CompletePath, async (HttpContext context) =>
        {
            if (await ReadAsync(context, AgentJsonContext.Default.AgentCompleteRequest) is not { } request)
                return;
            if (EnrolmentProblem(identity, options, request.TokenId) is { } problem)
            {
                await ErrorAsync(context, problem.Status, problem.Message);
                return;
            }
            var expected = AgentProtocol.CompleteProof(options.Token!.Secret, request.CertificatePem, request.CaCertificatePem);
            if (!AgentProtocol.ProofMatches(expected, request.Proof))
            {
                LogBadProof(logger, context.Connection.RemoteIpAddress?.ToString());
                await ErrorAsync(context, StatusCodes.Status403Forbidden, "The enrolment proof doesn't match this agent's token.");
                return;
            }
            if (identity.Complete(request.CertificatePem, request.CaCertificatePem) is { } error)
            {
                await ErrorAsync(context, StatusCodes.Status400BadRequest, error);
                return;
            }
            LogEnrolled(logger, options.Engine.ToString());
            await context.Response.WriteAsJsonAsync(Info(identity, options, startedAt), AgentJsonContext.Default.AgentInfo);
        });

        app.MapGet(AgentProtocol.InfoPath, () => Results.Json(Info(identity, options, startedAt), AgentJsonContext.Default.AgentInfo));

        // Renewal: only the enrolled server gets here (the gate above), so no token proofs are needed.
        app.MapPost(AgentProtocol.RenewCsrPath, () =>
            Results.Json(new AgentRenewCsrResponse { CsrPem = identity.CreateSigningRequest() }, AgentJsonContext.Default.AgentRenewCsrResponse));

        app.MapPost(AgentProtocol.RenewCompletePath, async (HttpContext context) =>
        {
            if (await ReadAsync(context, AgentJsonContext.Default.AgentRenewRequest) is not { } request)
                return;
            if (identity.Renew(request.CertificatePem, request.CaCertificatePem) is { } error)
            {
                await ErrorAsync(context, StatusCodes.Status400BadRequest, error);
                return;
            }
            LogRenewed(logger, identity.CertificateNotAfter);
            await context.Response.WriteAsJsonAsync(Info(identity, options, startedAt), AgentJsonContext.Default.AgentInfo);
        });

        // Everything else is the Engine API.
        var relay = app.Services.GetRequiredService<EngineRelay>();
        app.MapFallback(relay.RelayAsync);

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (options.Server is { } server)
            {
                if (identity.IsEnrolled)
                    LogEdgeServing(logger, server.GetLeftPart(UriPartial.Path), options.Engine.ToString());
                else if (options.Token is { } edgeToken)
                    LogEdgeWaiting(logger, server.GetLeftPart(UriPartial.Path), edgeToken.Id);
            }
            else if (identity.IsEnrolled)
                LogServing(logger, options.Port, options.Engine.ToString());
            else if (options.Token is { } token)
                LogWaiting(logger, options.Port, token.Id);
            else
                LogNoToken(logger, options.Port);
        });
        return app;
    }

    private static AgentInfo Info(AgentIdentity identity, AgentOptions options, DateTimeOffset startedAt) => new()
    {
        Version = Version,
        Engine = options.Engine.ToString(),
        Os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux",
        Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        StartedAt = startedAt,
        EnrolledAt = identity.EnrolledAt,
        CertificateNotAfter = identity.CertificateNotAfter,
        CertificateSha256 = identity.CertificateThumbprint,
        Mode = options.Server is null ? "direct" : "edge",
        ContainerIds = Docker.OwnContainer.Ids,
    };

    private static (int Status, string Message)? EnrolmentProblem(AgentIdentity identity, AgentOptions options, string tokenId)
    {
        if (identity.IsEnrolled)
            return (StatusCodes.Status409Conflict, "This agent is already enrolled. To enrol it again, remove its data volume and run the new command.");
        if (options.Token is not { } token)
            return (StatusCodes.Status409Conflict, "This agent was started without an enrolment token. Run the command from the endpoint's page.");
        if (!string.Equals(token.Id, tokenId, StringComparison.Ordinal))
            return (StatusCodes.Status403Forbidden, "This agent was started with a different enrolment command. Run the current one from the endpoint's page.");
        return null;
    }

    private static async Task<T?> ReadAsync<T>(HttpContext context, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        try
        {
            if (await context.Request.ReadFromJsonAsync(type, context.RequestAborted) is { } value)
                return value;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
        }
        await ErrorAsync(context, StatusCodes.Status400BadRequest, "The request isn't valid enrolment JSON.");
        return null;
    }

    private static Task ErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new AgentError { Message = message }, AgentJsonContext.Default.AgentError);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting on port {Port} for Sockless Port to enrol this agent (enrolment {TokenId})")]
    private static partial void LogWaiting(ILogger logger, int port, string tokenId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not enrolled and started without AGENT_TOKEN, so nothing can enrol this agent (port {Port}). Run the command from the endpoint's page.")]
    private static partial void LogNoToken(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolled. Relaying {Engine}")]
    private static partial void LogEnrolled(ILogger logger, string engine);

    [LoggerMessage(Level = LogLevel.Information, Message = "Serving on port {Port}, relaying {Engine}")]
    private static partial void LogServing(ILogger logger, int port, string engine);

    [LoggerMessage(Level = LogLevel.Information, Message = "Edge mode: dialling {Server} for Sockless Port to enrol this agent (enrolment {TokenId})")]
    private static partial void LogEdgeWaiting(ILogger logger, string server, string tokenId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Edge mode: dialling {Server}, relaying {Engine}")]
    private static partial void LogEdgeServing(ILogger logger, string server, string engine);

    [LoggerMessage(Level = LogLevel.Information, Message = "Renewed the agent's certificate, valid until {NotAfter}")]
    private static partial void LogRenewed(ILogger logger, DateTimeOffset? notAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused an enrolment with a wrong proof from {Remote}")]
    private static partial void LogBadProof(ILogger logger, string? remote);
}
