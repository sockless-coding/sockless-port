using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sockless.Port.Docker;

namespace Sockless.Port.Agent.Tests;

/// <summary>
/// Just enough of an engine, over plain TCP, for the agent to relay: version and info, an event
/// stream that stays open, an empty container list, and an exec start that upgrades and echoes
/// what it gets in upper case.
/// </summary>
internal sealed partial class FakeEngine : IAsyncDisposable
{
    private const string HeadEnd = "\r\n\r\n";
    private const string Upgraded = "HTTP/1.1 101 UPGRADED\r\nContent-Type: application/vnd.docker.raw-stream\r\nConnection: Upgrade\r\nUpgrade: tcp\r\n\r\n";

    private readonly WebApplication _app;

    private FakeEngine(WebApplication app) => _app = app;

    public TcpAddress Address { get; private set; } = null!;

    public List<string> Requests { get; } = [];

    public static async Task<FakeEngine> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // Kestrel won't upgrade a request with a body, and exec start has one: answer it below HTTP, as the engine does.
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen => listen.Use(next => connection => ExecAsync(connection, next))));
        var app = builder.Build();
        var engine = new FakeEngine(app);

        app.Use(async (context, next) =>
        {
            lock (engine.Requests)
                engine.Requests.Add($"{context.Request.Method} {context.Request.Path}");
            await next(context);
        });
        app.MapGet("/version", () => Results.Text("""{"Version":"29.0.0","ApiVersion":"1.51","Os":"linux","Arch":"amd64"}""", "application/json"));
        app.MapGet("/v1.51/info", () => Results.Text("""{"Name":"fake","ServerVersion":"29.0.0","OsType":"linux","Architecture":"x86_64","SecurityOptions":["name=seccomp,profile=builtin"]}""", "application/json"));
        app.MapGet("/v1.51/containers/json", () => Results.Text("[]", "application/json"));
        app.MapGet("/v1.51/events", async (HttpContext context) =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.StartAsync();
            await context.Response.WriteAsync("""{"Type":"container","Action":"start","Actor":{"ID":"abc"}}""" + "\n");
            await context.Response.Body.FlushAsync();
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
        });

        await app.StartAsync(TestContext.Current.CancellationToken);
        var url = new Uri(app.Urls.First());
        engine.Address = new TcpAddress("127.0.0.1", url.Port);
        return engine;
    }

    /// <summary>A connection whose first request starts an exec: 101, then echo in upper case.</summary>
    private static async Task ExecAsync(ConnectionContext connection, ConnectionDelegate next)
    {
        var input = connection.Transport.Input;
        while (true)
        {
            var result = await input.ReadAsync();
            if (result.Buffer.Length > 0 && result.Buffer.FirstSpan[0] == 0x16)
            {
                // A TLS handshake: let Kestrel answer it as the plain HTTP server it is.
                input.AdvanceTo(result.Buffer.Start);
                await next(connection);
                return;
            }
            var text = Encoding.ASCII.GetString(result.Buffer);
            var end = text.IndexOf(HeadEnd, StringComparison.Ordinal);
            if (end < 0 && !result.IsCompleted)
            {
                input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                continue;
            }
            if (end < 0 || !text.StartsWith("POST /v1.51/exec/", StringComparison.Ordinal))
            {
                input.AdvanceTo(result.Buffer.Start);
                await next(connection);
                return;
            }
            var length = ContentLength().Match(text[..end]) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            var headAndBody = end + HeadEnd.Length + length;
            if (result.Buffer.Length < headAndBody)
            {
                input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                continue;
            }
            input.AdvanceTo(result.Buffer.GetPosition(headAndBody));
            break;
        }

        var output = connection.Transport.Output;
        await output.WriteAsync(Encoding.ASCII.GetBytes(Upgraded));
        while (true)
        {
            var result = await input.ReadAsync();
            foreach (var segment in result.Buffer)
            {
                var upper = segment.ToArray();
                for (var i = 0; i < upper.Length; i++)
                    upper[i] = (byte)char.ToUpperInvariant((char)upper[i]);
                await output.WriteAsync(upper);
            }
            input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
                break;
        }
        await output.CompleteAsync();
    }

    [GeneratedRegex(@"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContentLength();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
