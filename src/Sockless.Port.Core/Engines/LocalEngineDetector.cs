using Microsoft.Extensions.Logging;
using Sockless.Port.Docker;

namespace Sockless.Port.Engines;

/// <summary>
/// Finds the engine on the host Sockless Port runs on: the configured address, else
/// <c>DOCKER_HOST</c>, else the first well-known Docker or Podman socket that answers.
/// </summary>
public sealed partial class LocalEngineDetector(ILogger<LocalEngineDetector> logger)
{
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Well-known locations, most likely first.</summary>
    public static IReadOnlyList<DockerAddress> WellKnownAddresses()
    {
        List<DockerAddress> candidates = [];
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(new NamedPipeAddress("docker_engine"));
            candidates.Add(new NamedPipeAddress("dockerDesktopLinuxEngine"));
            candidates.Add(new NamedPipeAddress("podman-machine-default"));
            return candidates;
        }

        candidates.Add(new UnixSocketAddress("/var/run/docker.sock"));
        candidates.Add(new UnixSocketAddress("/run/podman/podman.sock"));
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtimeDir)
            candidates.Add(new UnixSocketAddress($"{runtimeDir.TrimEnd('/')}/podman/podman.sock"));
        if (Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home)
            candidates.Add(new UnixSocketAddress($"{home.TrimEnd('/')}/.docker/run/docker.sock")); // Docker Desktop on macOS/Linux
        return candidates;
    }

    /// <param name="configured">The <c>Port:LocalEngine</c> setting, if any. Always wins.</param>
    public async Task<DockerAddress> DetectAsync(string? configured, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var address = DockerAddress.Parse(configured);
            LogUsingConfigured(logger, address.ToString());
            return address;
        }

        List<DockerAddress> candidates = [];
        if (Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 } dockerHost && DockerAddress.TryParse(dockerHost, out var fromEnv))
            candidates.Add(fromEnv);
        candidates.AddRange(WellKnownAddresses());

        DockerAddress? firstPresent = null;
        foreach (var candidate in candidates)
        {
            if (candidate is UnixSocketAddress unix && !File.Exists(unix.Path))
                continue;
            firstPresent ??= candidate;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ProbeTimeout);
                using var client = await DockerClient.ConnectAsync(candidate, new DockerClientOptions { RequestTimeout = ProbeTimeout }, timeout.Token);
                LogDetected(logger, candidate.ToString());
                return candidate;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogProbeFailed(logger, candidate.ToString(), ex.Message);
            }
        }

        // Nothing answered. Keep the most likely address so the endpoint shows why it's offline.
        var fallback = firstPresent ?? candidates[0];
        LogNoEngine(logger, fallback.ToString());
        return fallback;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Using the configured local engine at {Address}")]
    private static partial void LogUsingConfigured(ILogger logger, string address);

    [LoggerMessage(Level = LogLevel.Information, Message = "Found a local engine at {Address}")]
    private static partial void LogDetected(ILogger logger, string address);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No engine answered at {Address}: {Reason}")]
    private static partial void LogProbeFailed(ILogger logger, string address, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No local engine answered. Using {Address}; mount the Docker or Podman socket and restart, or set Port:LocalEngine.")]
    private static partial void LogNoEngine(ILogger logger, string address);
}
