using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Docker;
using Sockless.Port.Registries;

namespace Sockless.Port.Stacks;

/// <summary>The Docker CLI found on this server.</summary>
public sealed record DockerCliTool(string FileName, string Version);

/// <summary>One Docker CLI command, e.g. <c>stack deploy</c>.</summary>
/// <param name="WorkingDirectory">Where it runs: the stack's folder, so relative paths in the compose file resolve there.</param>
/// <param name="Variables">Extra environment (a stack's <c>.env</c>); reserved names (<see cref="StackEnv.IsReserved"/>) are skipped.</param>
public sealed record DockerCliRequest(
    IReadOnlyList<string> Arguments,
    DockerAddress? Engine = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Variables = null);

/// <summary>
/// Runs the Docker CLI for what only it does: <c>docker stack deploy</c>, <c>rm</c> and
/// <c>config</c> turn a compose file into swarm services, which the Engine API has no call for.
/// Each run gets the same clean environment as Compose (see <see cref="ComposeCli"/>).
/// </summary>
public sealed partial class DockerCli(IOptions<PortOptions> options, RegistryCredentials registries, TimeProvider time, ILogger<DockerCli> logger)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryMissingAfter = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private (DockerCliTool? Tool, string? Problem, DateTimeOffset At)? _probe;

    private PortOptions Port => options.Value;

    /// <exception cref="ComposeUnavailableException">The Docker CLI isn't installed or doesn't run.</exception>
    public async Task<DockerCliTool> FindAsync(CancellationToken cancellationToken = default)
    {
        if (_probe is { Tool: { } cached })
            return cached;
        await _probeGate.WaitAsync(cancellationToken);
        try
        {
            if (_probe is { } probe && (probe.Tool is not null || time.GetUtcNow() - probe.At < RetryMissingAfter))
                return probe.Tool ?? throw new ComposeUnavailableException(probe.Problem!);

            var (tool, problem) = await ProbeAsync(cancellationToken);
            _probe = (tool, problem, time.GetUtcNow());
            return tool ?? throw new ComposeUnavailableException(problem!);
        }
        finally
        {
            _probeGate.Release();
        }
    }

    public async Task<(DockerCliTool? Tool, string? Problem)> StatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await FindAsync(cancellationToken), null);
        }
        catch (ComposeUnavailableException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <exception cref="ComposeUnavailableException">The CLI is missing, or can't reach this kind of engine.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the process was killed.</exception>
    public async Task<ComposeResult> RunAsync(DockerCliRequest request, Action<string>? onLine = null, CancellationToken cancellationToken = default)
    {
        var tool = await FindAsync(cancellationToken);
        var runDirectory = Path.Combine(Port.TempPath, $"docker-{Guid.NewGuid():n}");
        ComposeCli.CreatePrivateDirectory(runDirectory);
        try
        {
            // An edge agent is reached through its tunnel, which only this server can open.
            await using var bridge = EngineBridge.StartIfNeeded(request.Engine, runDirectory);
            var start = ComposeCli.StartInfo(tool.FileName, request.Arguments);
            start.WorkingDirectory = request.WorkingDirectory ?? runDirectory;
            start.Environment.Clear();
            foreach (var (key, value) in await EnvironmentAsync(bridge is null ? request : request with { Engine = bridge.Address }, runDirectory, cancellationToken))
                start.Environment[key] = value;
            return await ComposeCli.ExecuteAsync(start, onLine, cancellationToken);
        }
        finally
        {
            try
            {
                if (Directory.Exists(runDirectory))
                    Directory.Delete(runDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Couldn't delete the Docker CLI run folder {Path}", runDirectory);
            }
        }
    }

    /// <summary>The stack's variables first, then what the run needs, which they can't replace.</summary>
    internal async Task<Dictionary<string, string>> EnvironmentAsync(DockerCliRequest request, string runDirectory, CancellationToken cancellationToken)
    {
        var env = ComposeCli.BaseEnvironment(runDirectory);
        foreach (var (key, value) in request.Variables ?? new Dictionary<string, string>())
        {
            if (!StackEnv.IsReserved(key))
                env[key] = value;
        }
        if (request.Engine is { } engine)
        {
            foreach (var (key, value) in ComposeCli.EngineEnvironment(engine, runDirectory))
                env[key] = value;
        }
        var auths = await registries.AllAsync(cancellationToken);
        if (auths.Count > 0)
            await ComposeCli.WriteDockerConfigAsync(Path.Combine(runDirectory, "config.json"), auths, cancellationToken);
        return env;
    }

    private async Task<(DockerCliTool? Tool, string? Problem)> ProbeAsync(CancellationToken cancellationToken)
    {
        var file = Port.DockerCli is { Length: > 0 } configured ? configured.Trim().Trim('"') : "docker";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            // --version doesn't contact an engine.
            var result = await ComposeCli.ExecuteAsync(ComposeCli.StartInfo(file, ["--version"]), null, timeout.Token);
            if (result.Succeeded && VersionPattern().Match(result.Stdout) is { Success: true } match)
            {
                logger.LogInformation("Using the Docker CLI {Version} ({Command})", match.Groups[1].Value, file);
                return (new DockerCliTool(file, match.Groups[1].Value), null);
            }
            return Missing($"{file} didn't report a version: {string.Join(' ', result.Output.TakeLast(3))}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Missing(Port.DockerCli is { Length: > 0 }
                ? $"The Docker CLI isn't at '{file}'. Check the Port:DockerCli setting."
                : "The Docker CLI isn't installed on the Sockless Port server, and swarm stacks are deployed with docker stack deploy. The image includes it; when running from source, install the docker CLI.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Missing($"{file} didn't answer within {ProbeTimeout.TotalSeconds:0} seconds.");
        }

        (DockerCliTool?, string?) Missing(string problem)
        {
            logger.LogWarning("The Docker CLI is unavailable: {Problem}", problem);
            return (null, problem);
        }
    }

    [GeneratedRegex(@"Docker version v?(\d+[^\s,]*)")]
    private static partial Regex VersionPattern();
}
