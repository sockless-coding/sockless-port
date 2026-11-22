using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Registries;

namespace Sockless.Port.Stacks;

/// <summary>The Compose binary found on this server.</summary>
/// <param name="Arguments">Arguments before Compose's own, e.g. <c>compose</c> for <c>docker compose</c>.</param>
public sealed record ComposeTool(string FileName, IReadOnlyList<string> Arguments, string Version)
{
    public string DisplayName => Arguments.Count == 0 ? FileName : $"{FileName} {string.Join(' ', Arguments)}";
}

/// <summary>Compose isn't installed or can't run here. The message says what to do.</summary>
public sealed class ComposeUnavailableException(string message) : Exception(message);

/// <summary>One Compose command against a project.</summary>
/// <param name="Engine">The engine to talk to; null for commands that don't need one (<c>config</c>).</param>
/// <param name="ProjectDirectory">The folder holding <c>compose.yaml</c> and <c>.env</c>; null to act on the project by name only.</param>
public sealed record ComposeRequest(string ProjectName, IReadOnlyList<string> Arguments, DockerAddress? Engine = null, string? ProjectDirectory = null);

/// <param name="Output">Everything Compose printed (stdout and stderr, as it came), one entry per line.</param>
/// <param name="Stdout">Standard output alone, for commands whose output is data (<c>config --format json</c>).</param>
public sealed record ComposeResult(int ExitCode, IReadOnlyList<string> Output, string Stdout)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs the Compose CLI as a child process. Each run gets a clean environment: the engine address,
/// a private config folder holding the stored registry credentials, and what the OS needs to start
/// a process. Sockless Port's own variables would otherwise leak into <c>${…}</c> interpolation.
/// </summary>
public sealed class ComposeCli(IOptions<PortOptions> options, RegistryCredentials registries, TimeProvider time, ILogger<ComposeCli> logger)
{
    /// <summary>Lines kept from one run. Compose's plain progress is short; this is only a guard.</summary>
    public const int MaxOutputLines = 5000;

    private const int MaxStdoutChars = 8 * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryMissingAfter = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private (ComposeTool? Tool, string? Problem, DateTimeOffset At)? _probe;

    private PortOptions Port => options.Value;

    /// <summary>The Compose binary, found once (and looked for again a minute after it wasn't there).</summary>
    /// <exception cref="ComposeUnavailableException">It isn't installed or doesn't run.</exception>
    public async Task<ComposeTool> FindAsync(CancellationToken cancellationToken = default)
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

    /// <summary>The Compose binary, or why there's none, without throwing.</summary>
    public async Task<(ComposeTool? Tool, string? Problem)> StatusAsync(CancellationToken cancellationToken = default)
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

    /// <summary>Runs Compose. <paramref name="onLine"/> sees each line as it's printed.</summary>
    /// <exception cref="ComposeUnavailableException">Compose isn't installed, or can't reach this kind of engine.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the process and its children were killed.</exception>
    public async Task<ComposeResult> RunAsync(ComposeRequest request, Action<string>? onLine = null, CancellationToken cancellationToken = default)
    {
        var tool = await FindAsync(cancellationToken);
        var runDirectory = Path.Combine(Port.TempPath, $"compose-{Guid.NewGuid():n}");
        CreatePrivateDirectory(runDirectory);
        try
        {
            var start = StartInfo(tool.FileName, Arguments(tool, request));
            // Without a project folder, Compose must not pick up a compose.yaml from wherever it runs.
            start.WorkingDirectory = request.ProjectDirectory ?? runDirectory;
            start.Environment.Clear();
            foreach (var (key, value) in await EnvironmentAsync(request.Engine, runDirectory, cancellationToken))
                start.Environment[key] = value;
            return await ExecuteAsync(start, onLine, cancellationToken);
        }
        finally
        {
            TryDelete(runDirectory);
        }
    }

    // ---------------------------------------------------------------- arguments and environment

    internal static IReadOnlyList<string> Arguments(ComposeTool tool, ComposeRequest request)
    {
        List<string> arguments = [.. tool.Arguments, "--ansi", "never", "--progress", "plain", "--project-name", request.ProjectName];
        if (request.ProjectDirectory is { } directory)
        {
            arguments.AddRange(["--project-directory", directory, "--file", Path.Combine(directory, StackFiles.ComposeFile)]);
            var env = Path.Combine(directory, StackFiles.EnvFile);
            if (File.Exists(env))
                arguments.AddRange(["--env-file", env]);
        }
        arguments.AddRange(request.Arguments);
        return arguments;
    }

    /// <summary>
    /// The variables Compose runs with. <c>DOCKER_CONFIG</c> is <paramref name="runDirectory"/>, which
    /// gets a <c>config.json</c> with the stored registry credentials (deleted with the folder after the run).
    /// </summary>
    internal async Task<Dictionary<string, string>> EnvironmentAsync(DockerAddress? engine, string runDirectory, CancellationToken cancellationToken)
    {
        var env = BaseEnvironment(runDirectory);
        if (engine is not null)
        {
            foreach (var (key, value) in EngineEnvironment(engine, runDirectory))
                env[key] = value;
        }

        var auths = await registries.AllAsync(cancellationToken);
        if (auths.Count > 0)
            await WriteDockerConfigAsync(Path.Combine(runDirectory, "config.json"), auths, cancellationToken);
        return env;
    }

    /// <summary>What every run gets: the search path, a private home and config folder, and on Windows what a process needs to start.</summary>
    internal static Dictionary<string, string> BaseEnvironment(string runDirectory)
    {
        var env = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin",
            ["HOME"] = runDirectory,
            ["DOCKER_CONFIG"] = runDirectory,
            ["TMPDIR"] = runDirectory,
        };
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in (string[])["SystemRoot", "windir", "PATHEXT", "ComSpec"])
            {
                if (Environment.GetEnvironmentVariable(key) is { } value)
                    env[key] = value;
            }
            foreach (var key in (string[])["USERPROFILE", "TEMP", "TMP", "APPDATA", "LOCALAPPDATA"])
                env[key] = runDirectory;
        }
        return env;
    }

    /// <summary>
    /// <c>DOCKER_HOST</c> for the engine, as the Docker CLI spells it. With TLS, the CA and client
    /// certificate go into <paramref name="runDirectory"/> for <c>DOCKER_CERT_PATH</c>.
    /// </summary>
    internal static Dictionary<string, string> EngineEnvironment(DockerAddress engine, string runDirectory)
    {
        switch (engine)
        {
            case UnixSocketAddress or NamedPipeAddress:
                return new() { ["DOCKER_HOST"] = engine.ToString() };

            case TcpAddress { Tls: null } tcp:
                return new() { ["DOCKER_HOST"] = TcpHost(tcp) };

            case TcpAddress { Tls: { } tls } tcp:
                if (tls.CaCertificatePem is null)
                    throw new ComposeUnavailableException("Compose needs the engine's CA certificate to check its TLS certificate, and this endpoint has none.");
                var certs = Path.Combine(runDirectory, "certs");
                CreatePrivateDirectory(certs);
                File.WriteAllText(Path.Combine(certs, "ca.pem"), tls.CaCertificatePem);
                if (tls.ClientCertificatePem is { } cert && tls.ClientKeyPem is { } key)
                {
                    File.WriteAllText(Path.Combine(certs, "cert.pem"), cert);
                    File.WriteAllText(Path.Combine(certs, "key.pem"), key);
                }
                return new() { ["DOCKER_HOST"] = TcpHost(tcp), ["DOCKER_TLS_VERIFY"] = "1", ["DOCKER_CERT_PATH"] = certs };

            default:
                throw new ComposeUnavailableException($"Compose can't reach an engine at {engine}.");
        }
    }

    private static string TcpHost(TcpAddress tcp) =>
        $"tcp://{(tcp.Host.Contains(':', StringComparison.Ordinal) ? $"[{tcp.Host}]" : tcp.Host)}:{tcp.Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A Docker CLI <c>config.json</c> with <c>auths</c>, so Compose pulls with the stored credentials.</summary>
    internal static async Task WriteDockerConfigAsync(string path, IReadOnlyCollection<AuthConfig> auths, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await using var json = new Utf8JsonWriter(file);
        json.WriteStartObject();
        json.WriteStartObject("auths");
        foreach (var auth in auths)
        {
            if (auth.ServerAddress is null)
                continue;
            json.WriteStartObject(auth.ServerAddress);
            json.WriteString("auth", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}")));
            json.WriteEndObject();
        }
        json.WriteEndObject();
        json.WriteEndObject();
        await json.FlushAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- processes

    private async Task<(ComposeTool? Tool, string? Problem)> ProbeAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(string File, string[] Arguments)> candidates = Port.Compose is { Length: > 0 } configured
            ? [Split(configured)]
            : [("docker-compose", []), ("docker", ["compose"])];

        string? lastProblem = null;
        foreach (var (file, arguments) in candidates)
        {
            var command = string.Join(' ', [file, .. arguments]);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ProbeTimeout);
                var result = await ExecuteAsync(StartInfo(file, [.. arguments, "version", "--short"]), null, timeout.Token);
                var version = result.Stdout.Trim().TrimStart('v');
                if (result.Succeeded && version.Length > 0 && char.IsAsciiDigit(version[0]))
                {
                    logger.LogInformation("Using Compose {Version} ({Command})", version, command);
                    return (new ComposeTool(file, arguments, version), null);
                }
                lastProblem = $"{command} didn't report a version: {string.Join(' ', result.Output.TakeLast(3))}";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed: try the next one.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastProblem = $"{command} didn't answer within {ProbeTimeout.TotalSeconds:0} seconds.";
            }
        }

        var problem = lastProblem ?? (Port.Compose is { Length: > 0 } c
            ? $"Compose isn't at '{c}'. Check the Port:Compose setting."
            : "Compose isn't installed on the Sockless Port server. The image includes it; when running from source, install docker-compose or the docker compose plugin.");
        logger.LogWarning("Compose is unavailable: {Problem}", problem);
        return (null, problem);
    }

    /// <summary><c>docker compose</c> → <c>docker</c> + <c>[compose]</c>. Quote a path with spaces: <c>"C:\Program Files\x.exe" compose</c>.</summary>
    internal static (string File, string[] Arguments) Split(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"') && command.IndexOf('"', 1) is > 0 and var end)
            return (command[1..end], command[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (File.Exists(command))
            return (command, []);
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts[1..]);
    }

    private static ProcessStartInfo StartInfo(string file, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task<ComposeResult> ExecuteAsync(ProcessStartInfo start, Action<string>? onLine, CancellationToken cancellationToken)
    {
        var output = new List<string>();
        var stdout = new StringBuilder();
        var gate = new Lock();

        void Add(string? line, bool isStdout)
        {
            if (line is null)
                return;
            lock (gate)
            {
                if (isStdout && stdout.Length < MaxStdoutChars)
                    stdout.Append(line).Append('\n');
                if (output.Count < MaxOutputLines)
                    output.Add(line);
                else if (output.Count == MaxOutputLines)
                    output.Add("… (more output was cut)");
            }
            onLine?.Invoke(line);
        }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Add(e.Data, isStdout: true);
        process.ErrorDataReceived += (_, e) => Add(e.Data, isStdout: false);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        // WaitForExitAsync returns after the redirected output has been read to the end.
        lock (gate)
            return new ComposeResult(process.ExitCode, [.. output], stdout.ToString());
    }

    private static void CreatePrivateDirectory(string path)
    {
        // The folder briefly holds registry credentials and TLS keys: owner only.
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't delete the Compose run folder {Path}", directory);
        }
    }
}
