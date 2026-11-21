using System.Text;
using System.Text.Json.Nodes;
using Sockless.Port.Docker.Streams;

namespace Sockless.Port.Docker.Tests;

/// <summary>Container lifecycle, logs, stats and exec against a real engine (see <see cref="EngineIntegrationTests"/>).</summary>
[Trait("Category", "Integration")]
public sealed class ContainerIntegrationTests : IAsyncLifetime
{
    private const string Image = "busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");

    private DockerClient _client = null!;
    private readonly List<string> _containers = [];

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run engine integration tests.");
        _client = await DockerClient.ConnectAsync(DockerAddress.Parse(Address!), cancellationToken: TestContext.Current.CancellationToken);
        if (!await _client.ImageExistsAsync(Image, TestContext.Current.CancellationToken))
        {
            await foreach (var _ in _client.PullImageAsync(Image, cancellationToken: TestContext.Current.CancellationToken)) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        foreach (var id in _containers)
        {
            try { await _client.RemoveContainerAsync(id, force: true); } catch (DockerApiException) { }
        }
        _client.Dispose();
    }

    private async Task<string> RunAsync(string script, bool tty = false)
    {
        var name = $"sockless-port-test-{Guid.NewGuid():n}"[..32];
        var body = new JsonObject
        {
            ["Image"] = Image,
            ["Cmd"] = new JsonArray("sh", "-c", script),
            ["Tty"] = tty,
            ["Labels"] = new JsonObject { ["test"] = "sockless-port" },
        };
        var created = await _client.CreateContainerAsync(name, body, TestContext.Current.CancellationToken);
        _containers.Add(created.Id);
        await _client.StartContainerAsync(created.Id, TestContext.Current.CancellationToken);
        return created.Id;
    }

    [Fact]
    public async Task Lifecycle_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await RunAsync("sleep 300");

        var (running, json) = await _client.InspectContainerAsync(id, ct);
        Assert.True(running.State!.Running);
        Assert.Contains("\"Id\"", json);

        await _client.StartContainerAsync(id, ct); // already running: 304 is fine
        try
        {
            await _client.PauseContainerAsync(id, ct);
            Assert.True((await _client.InspectContainerAsync(id, ct)).Container.State!.Paused);
            await _client.UnpauseContainerAsync(id, ct);
        }
        catch (DockerApiException ex) when (ex.Message.Contains("cgroup", StringComparison.OrdinalIgnoreCase))
        {
            // Pausing needs the cgroup freezer, which Podman without cgroups (nested) lacks.
        }

        var newName = $"renamed-{Guid.NewGuid():n}"[..20];
        await _client.RenameContainerAsync(id, newName, ct);
        Assert.Equal(newName, (await _client.InspectContainerAsync(id, ct)).Container.DisplayName);

        await _client.StopContainerAsync(id, timeout: 1, ct);
        await _client.StopContainerAsync(id, timeout: 1, ct); // already stopped: 304 is fine
        Assert.False((await _client.InspectContainerAsync(id, ct)).Container.State!.Running);

        await _client.RemoveContainerAsync(id, cancellationToken: ct);
        var missing = await Assert.ThrowsAsync<DockerApiException>(() => _client.InspectContainerAsync(id, ct));
        Assert.True(missing.IsNotFound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logs_are_split_into_lines_per_stream(bool tty)
    {
        var ct = TestContext.Current.CancellationToken;
        // Two lines in one write, so they share a frame. (A trailing partial line isn't logged by
        // the engine until the container exits; flushing it is covered by the unit tests.)
        var id = await RunAsync("echo one; echo two >&2; printf 'three\\nfour\\n'; sleep 300", tty);
        await Task.Delay(1500, ct);

        var lines = new List<LogLine>();
        await foreach (var line in _client.StreamLogsAsync(id, new LogOptions { Tty = tty, Timestamps = true, Tail = null }, ct))
            lines.Add(line);

        Assert.Equal(["four", "one", "three", "two"], lines.Select(l => l.Text).Order());
        Assert.All(lines, l => Assert.NotNull(l.Timestamp));
        // With a TTY there's only one stream; without, stderr stays separate.
        Assert.Equal(tty ? StreamKind.Stdout : StreamKind.Stderr, lines.Single(l => l.Text == "two").Stream);
    }

    [Fact]
    public async Task Follow_streams_new_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await RunAsync("i=0; while true; do i=$((i+1)); echo line-$i; sleep 0.2; done");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        var seen = new List<string>();
        await foreach (var line in _client.StreamLogsAsync(id, new LogOptions { Follow = true, Tail = 0 }, cts.Token))
        {
            seen.Add(line.Text);
            if (seen.Count == 3)
                break;
        }

        Assert.Equal(3, seen.Count);
        Assert.All(seen, s => Assert.StartsWith("line-", s));
    }

    [Fact]
    public async Task Stats_stream_reports_memory()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await RunAsync("sleep 300");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var samples = new List<StatsSample>();
            await foreach (var sample in _client.StreamStatsAsync(id, cts.Token))
            {
                samples.Add(sample);
                if (samples.Count == 2)
                    break;
            }
            Assert.True(samples[^1].MemoryUsage > 0);
        }
        catch (DockerApiException ex) when (ex.Message.Contains("cgroup", StringComparison.OrdinalIgnoreCase))
        {
            // Podman without cgroup access (e.g. nested in a container) can't report stats; the UI says so.
            Assert.Skip($"Engine can't report stats here: {ex.Message}");
        }
    }

    [Fact]
    public async Task Exec_session_carries_input_and_output()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await RunAsync("sleep 300");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        var execId = await _client.CreateExecAsync(id, new Models.ExecCreateRequest { Cmd = ["sh"], Env = ["TERM=xterm-256color"] }, cts.Token);
        await using var session = await _client.StartExecAsync(execId, cts.Token);
        await _client.ResizeExecAsync(execId, rows: 30, columns: 100, cts.Token);

        await session.Stream.WriteAsync("echo marker-$((40+2))\nexit 7\n"u8.ToArray(), cts.Token);
        await session.Stream.FlushAsync(cts.Token);

        var output = new StringBuilder();
        var buffer = new byte[4096];
        int read;
        while ((read = await session.Stream.ReadAsync(buffer, cts.Token)) > 0)
            output.Append(Encoding.UTF8.GetString(buffer, 0, read));

        Assert.Contains("marker-42", output.ToString());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        Models.ExecInspect inspect;
        do
        {
            inspect = await _client.InspectExecAsync(execId, cts.Token);
        } while (inspect.Running && DateTime.UtcNow < deadline && await Task.Delay(100, cts.Token).ContinueWith(_ => true, TaskScheduler.Default));
        Assert.Equal(7, inspect.ExitCode);
    }

    [Fact]
    public async Task Pull_errors_are_thrown()
    {
        await Assert.ThrowsAsync<DockerApiException>(async () =>
        {
            await foreach (var _ in _client.PullImageAsync("sockless-port/does-not-exist:never", cancellationToken: TestContext.Current.CancellationToken)) { }
        });
    }
}
