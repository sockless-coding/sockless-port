using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Sockless.Port.Docker.Models;
using Sockless.Port.Docker.Streams;

namespace Sockless.Port.Docker;

public sealed class LogOptions
{
    /// <summary>Keep the stream open and send new output as it is written.</summary>
    public bool Follow { get; init; }

    /// <summary>Number of lines from the end; null for all.</summary>
    public int? Tail { get; init; } = 500;

    public bool Timestamps { get; init; }

    /// <summary>Whether the container has a TTY (then output is raw, not multiplexed). From inspect.</summary>
    public bool Tty { get; init; }
}

public sealed partial class DockerClient
{
    // ---------------------------------------------------------------- inspect

    /// <summary>Inspects a container. Returns the typed view and the engine's JSON, verbatim.</summary>
    public async Task<(ContainerInspect Container, string Json)> InspectContainerAsync(string id, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);
        using var response = await SendCoreAsync(HttpMethod.Get, $"/containers/{Escape(id)}/json", content: null, cts.Token);
        var json = await response.Content.ReadAsStringAsync(cts.Token);
        var container = System.Text.Json.JsonSerializer.Deserialize(json, Json.ContainerInspect)
                        ?? throw new InvalidDataException($"The engine returned an empty inspect for {id}.");
        return (container, json);
    }

    // ---------------------------------------------------------------- lifecycle

    public Task StartContainerAsync(string id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/containers/{Escape(id)}/start", content: null, cancellationToken, allowNotModified: true);

    /// <param name="timeout">Seconds to wait before killing; null for the container's own default (usually 10).</param>
    public Task StopContainerAsync(string id, int? timeout = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Path($"/containers/{Escape(id)}/stop", Query(("t", Number(timeout)))), content: null, cancellationToken,
            timeout: StopWait(timeout), allowNotModified: true);

    public Task RestartContainerAsync(string id, int? timeout = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Path($"/containers/{Escape(id)}/restart", Query(("t", Number(timeout)))), content: null, cancellationToken,
            timeout: StopWait(timeout));

    public Task KillContainerAsync(string id, string? signal = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Path($"/containers/{Escape(id)}/kill", Query(("signal", signal))), content: null, cancellationToken);

    public Task PauseContainerAsync(string id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/containers/{Escape(id)}/pause", content: null, cancellationToken);

    public Task UnpauseContainerAsync(string id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/containers/{Escape(id)}/unpause", content: null, cancellationToken);

    public Task RenameContainerAsync(string id, string newName, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Path($"/containers/{Escape(id)}/rename", Query(("name", newName))), content: null, cancellationToken);

    /// <param name="force">Kill a running container first.</param>
    /// <param name="removeVolumes">Also remove anonymous volumes (never named ones).</param>
    public Task RemoveContainerAsync(string id, bool force = false, bool removeVolumes = false, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, Path($"/containers/{Escape(id)}", Query(("force", force ? "true" : null), ("v", removeVolumes ? "true" : null))),
            content: null, cancellationToken, timeout: StopWait(null));

    /// <summary>Creates a container from an engine create body (<c>Config</c> fields plus <c>HostConfig</c> and <c>NetworkingConfig</c>).</summary>
    public Task<ContainerCreateResponse> CreateContainerAsync(string? name, JsonObject body, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Path("/containers/create", Query(("name", string.IsNullOrWhiteSpace(name) ? null : name))),
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), Json.ContainerCreateResponse, cancellationToken);

    // ---------------------------------------------------------------- logs and stats

    public async IAsyncEnumerable<LogLine> StreamLogsAsync(string id, LogOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = Query(
            ("stdout", "true"),
            ("stderr", "true"),
            ("follow", options.Follow ? "true" : null),
            ("timestamps", options.Timestamps ? "true" : null),
            ("tail", options.Tail is { } tail ? tail.ToString(CultureInfo.InvariantCulture) : "all"));

        using var response = await SendCoreAsync(HttpMethod.Get, Path($"/containers/{Escape(id)}/logs", query), content: null,
            cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var line in LogReader.ReadLinesAsync(stream, options.Tty, options.Timestamps, cancellationToken))
            yield return line;
    }

    /// <summary>Streams a stats sample about once a second until cancelled or the container stops.</summary>
    public async IAsyncEnumerable<StatsSample> StreamStatsAsync(string id, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await SendCoreAsync(HttpMethod.Get, Path($"/containers/{Escape(id)}/stats", Query(("stream", "true"))), content: null,
            cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var stats in JsonLines.ReadAsync(stream, Json.ContainerStats, cancellationToken))
            yield return StatsSample.From(stats);
    }

    // ---------------------------------------------------------------- exec

    public async Task<string> CreateExecAsync(string containerId, ExecCreateRequest request, CancellationToken cancellationToken = default) =>
        (await SendAsync(HttpMethod.Post, $"/containers/{Escape(containerId)}/exec",
            JsonContent.Create(request, Json.ExecCreateRequest), Json.IdResponse, cancellationToken)).Id;

    /// <summary>
    /// Starts an exec with a TTY and hijacks the connection (HTTP <c>101 Upgrade: tcp</c>), giving a
    /// stream that carries terminal input and output.
    /// </summary>
    public async Task<ExecSession> StartExecAsync(string execId, CancellationToken cancellationToken = default)
    {
        // A fixed-length body, not JsonContent: that one is sent chunked, and the engine hijacks
        // the connection after the headers, so the chunk framing never completes and start hangs.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_prefix}/exec/{Escape(execId)}/start")
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new ExecStartRequest { Detach = false, Tty = true }, Json.ExecStartRequest),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Connection.Add("Upgrade");
        request.Headers.Upgrade.Add(new ProductHeaderValue("tcp"));

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            // 101 isn't a 2xx, so check it before error handling: reading an "error body" from a
            // hijacked connection would block until the session ends.
            if (response.StatusCode != HttpStatusCode.SwitchingProtocols)
            {
                await EnsureSuccessAsync(response, $"/exec/{execId}/start", cancellationToken);
                throw new NotSupportedException("The engine did not upgrade the exec connection, so input can't be sent.");
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            if (!stream.CanWrite)
                throw new NotSupportedException("The engine's exec connection is read-only, so input can't be sent.");
            return new ExecSession(execId, response, stream);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs a command in a running container without a TTY and collects its output (a non-hijacked
    /// exec: the engine streams multiplexed frames in the response and closes it when the command ends).
    /// </summary>
    /// <param name="maxOutputBytes">Stop reading once stdout and stderr together reach this; the result is then <see cref="ExecResult.Truncated"/>.</param>
    public async Task<ExecResult> RunExecAsync(string containerId, IReadOnlyList<string> command, int maxOutputBytes = 4 * 1024 * 1024,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? _requestTimeout);
        var execId = await CreateExecAsync(containerId, new ExecCreateRequest { Cmd = command, AttachStdin = false, Tty = false }, cts.Token);

        // Fixed-length body: see StartExecAsync.
        var start = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new ExecStartRequest { Detach = false, Tty = false }, Json.ExecStartRequest),
            Encoding.UTF8, "application/json");
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var truncated = false;
        using (var response = await SendCoreAsync(HttpMethod.Post, $"/exec/{Escape(execId)}/start", start, cts.Token, HttpCompletionOption.ResponseHeadersRead))
        await using (var stream = await response.Content.ReadAsStreamAsync(cts.Token))
        {
            await foreach (var frame in MultiplexedStream.ReadFramesAsync(stream, cts.Token))
            {
                var target = frame.Kind == StreamKind.Stderr ? stderr : stdout;
                var room = maxOutputBytes - (int)(stdout.Length + stderr.Length);
                target.Write(frame.Payload.Span[..Math.Min(room, frame.Payload.Length)]);
                if (frame.Payload.Length > room)
                {
                    truncated = true;
                    break;
                }
            }
        }

        // The stream ends when the process does, but the engine records the exit code a moment later.
        int? exitCode = null;
        for (var attempt = 0; attempt < 20 && !truncated; attempt++)
        {
            var inspect = await InspectExecAsync(execId, cts.Token);
            if (!inspect.Running && inspect.ExitCode is { } code)
            {
                exitCode = code;
                break;
            }
            await Task.Delay(25, cts.Token);
        }
        return new ExecResult(Encoding.UTF8.GetString(stdout.GetBuffer(), 0, (int)stdout.Length),
            Encoding.UTF8.GetString(stderr.GetBuffer(), 0, (int)stderr.Length), exitCode, truncated);
    }

    public Task ResizeExecAsync(string execId, int rows, int columns, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Path($"/exec/{Escape(execId)}/resize",
            Query(("h", rows.ToString(CultureInfo.InvariantCulture)), ("w", columns.ToString(CultureInfo.InvariantCulture)))), content: null, cancellationToken);

    public Task<ExecInspect> InspectExecAsync(string execId, CancellationToken cancellationToken = default) =>
        GetAsync($"/exec/{Escape(execId)}/json", Json.ExecInspect, cancellationToken);

    // ---------------------------------------------------------------- files

    /// <summary>
    /// A file or directory from a container's filesystem (running or not) as an uncompressed tar
    /// stream. Dispose it to release the connection. For a symbolic link Docker sends the link,
    /// Podman the file it points to.
    /// </summary>
    public async Task<Stream> GetArchiveAsync(string containerId, string path, CancellationToken cancellationToken = default)
    {
        var response = await SendCoreAsync(HttpMethod.Get, Path($"/containers/{Escape(containerId)}/archive", Query(("path", path))), content: null,
            cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        try
        {
            return new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string Escape(string id) => Uri.EscapeDataString(id);

    /// <summary>Image names keep their slashes in the path (<c>/images/library/nginx/json</c>).</summary>
    private static string EscapeImage(string reference) => string.Join('/', reference.Split('/').Select(Uri.EscapeDataString));

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>Stopping waits for the container's grace period, so allow that on top of the normal timeout.</summary>
    private TimeSpan StopWait(int? seconds) => _requestTimeout + TimeSpan.FromSeconds(seconds ?? 30);
}
