using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Sockless.Port.Docker;

namespace Sockless.Port.Agent;

/// <summary>
/// Passes Engine API requests to the local engine unchanged. Bodies stream both ways, so events,
/// logs, pulls and uploads behave as they do on the socket. Upgrade requests (exec and attach
/// with a TTY) get a raw connection that's spliced to the client after the engine's 101.
/// </summary>
public sealed partial class EngineRelay : IDisposable
{
    private const int MaxResponseHeadBytes = 64 * 1024;

    /// <summary>Connection-level headers that never cross a proxy (RFC 9110 §7.6.1), plus Host.</summary>
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host", "Expect",
    };

    private readonly DockerAddress _engine;
    private readonly HttpClient _http;
    private readonly ILogger<EngineRelay> _logger;

    public EngineRelay(AgentOptions options, ILogger<EngineRelay> logger)
    {
        _engine = options.Engine;
        _logger = logger;
        var (handler, baseAddress) = DockerTransport.CreateHandler(options.Engine);
        _http = new HttpClient(handler, disposeHandler: true) { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan };
    }

    public DockerAddress Engine => _engine;

    public async Task RelayAsync(HttpContext context)
    {
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw && raw[0] == '/'
            ? raw
            : context.Request.Path + context.Request.QueryString;
        try
        {
            if (context.Features.Get<IHttpUpgradeFeature>() is { IsUpgradableRequest: true } upgrade && context.Request.Headers.Upgrade.Count > 0)
                await RelayUpgradeAsync(context, upgrade, target);
            else
                await RelayRequestAsync(context, target);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException && !context.RequestAborted.IsCancellationRequested)
        {
            LogEngineUnreachable(_logger, _engine.ToString(), ex.Message);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(
                    new Docker.Agent.AgentError { Message = $"The agent can't reach the engine at {_engine}: {ex.Message}" },
                    Docker.Agent.AgentJsonContext.Default.AgentError);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The server went away mid-stream (a closed log tail, a cancelled pull).
        }
    }

    // ---------------------------------------------------------------- ordinary requests

    private async Task RelayRequestAsync(HttpContext context, string target)
    {
        var request = context.Request;
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), target);

        var hasBody = request.ContentLength > 0 || (request.ContentLength is null && request.Headers.TransferEncoding.Count > 0);
        if (hasBody)
        {
            message.Content = new StreamContent(request.Body, 64 * 1024);
            if (request.ContentLength is { } length)
                message.Content.Headers.ContentLength = length;
        }

        foreach (var (name, values) in request.Headers)
        {
            if (HopByHop.Contains(name))
                continue;
            if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                if (message.Content is not null && !name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    message.Content.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
                continue;
            }
            message.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
        }

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        CopyHeaders(response.Headers, context.Response.Headers);
        CopyHeaders(response.Content.Headers, context.Response.Headers);

        // Streams (events, logs, stats, pull progress) must reach the server as they're written.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await context.Response.StartAsync(context.RequestAborted);
        await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        await PumpAsync(body, context.Response.Body, context.RequestAborted);
    }

    private static void CopyHeaders(System.Net.Http.Headers.HttpHeaders from, IHeaderDictionary to)
    {
        foreach (var (name, values) in from)
        {
            if (!HopByHop.Contains(name))
                to[name] = new StringValues([.. values]);
        }
    }

    // ---------------------------------------------------------------- upgrades (hijacked connections)

    private async Task RelayUpgradeAsync(HttpContext context, IHttpUpgradeFeature upgrade, string target)
    {
        var request = context.Request;
        var cancellation = context.RequestAborted;

        // On an upgradable request Kestrel hands over the raw connection as the body, so read
        // exactly the declared length (exec start sends a small JSON body) and no further.
        var body = Array.Empty<byte>();
        if (request.ContentLength is > 0 and var length)
        {
            if (length > 1024 * 1024)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            body = new byte[length];
            await request.Body.ReadExactlyAsync(body, cancellation);
        }

        await using var engine = await DockerTransport.ConnectRawAsync(_engine, cancellation);

        var head = new StringBuilder();
        head.Append(CultureInfo.InvariantCulture, $"{request.Method} {target} HTTP/1.1\r\nHost: docker\r\n");
        foreach (var (name, values) in request.Headers)
        {
            if (HopByHop.Contains(name) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var value in values)
                head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }
        if (body.Length > 0)
            head.Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n");
        head.Append(CultureInfo.InvariantCulture, $"Connection: Upgrade\r\nUpgrade: {request.Headers.Upgrade}\r\n\r\n");
        await engine.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellation);
        if (body.Length > 0)
            await engine.WriteAsync(body, cancellation);
        await engine.FlushAsync(cancellation);

        var (status, headers) = await ReadResponseHeadAsync(engine, cancellation);
        if (status != StatusCodes.Status101SwitchingProtocols)
        {
            // An error (unknown exec, container not running) or an engine that streams without upgrading.
            context.Response.StatusCode = status;
            long? contentLength = null;
            foreach (var (name, value) in headers)
            {
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    contentLength = parsed;
                else if (!HopByHop.Contains(name))
                    context.Response.Headers.Append(name, value);
            }
            if (contentLength is { } n)
                context.Response.ContentLength = n;
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await context.Response.StartAsync(cancellation);
            await PumpAsync(contentLength is { } limit ? new LimitedStream(engine, limit) : engine, context.Response.Body, cancellation);
            return;
        }

        foreach (var (name, value) in headers)
        {
            if (!HopByHop.Contains(name))
                context.Response.Headers.Append(name, value);
        }
        context.Response.Headers.Upgrade = request.Headers.Upgrade;
        await using var client = await upgrade.UpgradeAsync();

        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var toEngine = Task.Run(async () =>
        {
            await PumpAsync(client, engine, done.Token);
            // The server closed its input: pass that on, so the process sees end of input.
            if (engine is NetworkStream network)
                network.Socket.Shutdown(SocketShutdown.Send);
        }, CancellationToken.None);
        var toClient = PumpAsync(engine, client, done.Token);

        try
        {
            await toClient;
        }
        finally
        {
            await done.CancelAsync();
            try { await toEngine; } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }
    }

    /// <summary>Reads the status line and headers, byte by byte so nothing after them is consumed.</summary>
    internal static async Task<(int Status, List<(string Name, string Value)> Headers)> ReadResponseHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(512);
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
                throw new IOException("The engine closed the connection before answering.");
            bytes.Add(one[0]);
            if (bytes.Count > MaxResponseHeadBytes)
                throw new IOException("The engine's response headers are too large.");
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
                break;
        }

        var lines = Encoding.ASCII.GetString([.. bytes]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var statusParts = lines[0].Split(' ', 3);
        if (statusParts.Length < 2 || !int.TryParse(statusParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
            throw new IOException($"The engine sent an unreadable status line: {lines[0]}");

        var headers = new List<(string, string)>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
                headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        return (status, headers);
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await from.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await to.FlushAsync(cancellationToken);
        }
    }

    public void Dispose() => _http.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Can't reach the engine at {Engine}: {Reason}")]
    private static partial void LogEngineUnreachable(ILogger logger, string engine, string reason);

    /// <summary>Reads at most a fixed number of bytes from the inner stream.</summary>
    private sealed class LimitedStream(Stream inner, long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
                return 0;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
