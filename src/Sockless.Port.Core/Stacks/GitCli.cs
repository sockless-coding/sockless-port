using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sockless.Port.Stacks;

/// <summary>The git client found on this server.</summary>
public sealed record GitTool(string FileName, string Version);

/// <summary>A repository and how to sign in to it, for one git run. <paramref name="Secret"/> is in clear: keep it out of logs.</summary>
/// <param name="KnownHosts">The SSH server's host keys learnt before; null to learn them now (trust on first use).</param>
public sealed record GitRemote(string Url, GitAuth Auth = GitAuth.None, string? Username = null, string? Secret = null, string? KnownHosts = null)
{
    /// <summary>The user name sent with a token when none is given. GitHub, GitLab and Gitea accept any name with a token.</summary>
    public const string DefaultTokenUser = "x-access-token";

    public override string ToString() => Url;
}

/// <summary>A git command failed. The message is for people and holds no secrets.</summary>
public sealed class GitException(string message) : Exception(message);

/// <summary>
/// Runs the git client as a child process, like <see cref="ComposeCli"/>: an argument list, never a
/// shell, and a clean environment for each session. Git reads no system or user configuration,
/// never prompts, and may only use https, http and ssh (so a repository can't name <c>ext::</c> or
/// <c>file://</c> transports). A token reaches git through environment configuration (an
/// <c>Authorization</c> header scoped to the repository's host), never the command line; an
/// SSH key and the known host keys are files in the session's private folder, deleted after.
/// </summary>
public sealed partial class GitCli(IOptions<PortOptions> options, TimeProvider time, ILogger<GitCli> logger)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryMissingAfter = TimeSpan.FromMinutes(1);

    /// <summary>Passed through so fetches work behind a proxy and with a custom CA bundle.</summary>
    private static readonly string[] PassedThrough = ["HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy", "SSL_CERT_FILE", "SSL_CERT_DIR", "GIT_SSL_CAINFO"];

    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private (GitTool? Tool, string? Problem, DateTimeOffset At)? _probe;

    /// <summary>The transports git may use (<c>GIT_ALLOW_PROTOCOL</c>). Tests add <c>file</c> for local repositories.</summary>
    internal string AllowedProtocols { get; set; } = "https:http:ssh";

    private PortOptions Port => options.Value;

    /// <exception cref="ComposeUnavailableException">Git isn't installed or doesn't run.</exception>
    public async Task<GitTool> FindAsync(CancellationToken cancellationToken = default)
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

    public async Task<(GitTool? Tool, string? Problem)> StatusAsync(CancellationToken cancellationToken = default)
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

    /// <summary>
    /// A session for several git commands against one repository (fetch, then checkout, which may
    /// fetch missing files). Without <paramref name="remote"/>, only local commands work.
    /// </summary>
    /// <exception cref="ComposeUnavailableException">Git isn't installed.</exception>
    public async Task<GitSession> OpenAsync(GitRemote? remote, CancellationToken cancellationToken = default)
    {
        var tool = await FindAsync(cancellationToken);
        var runDirectory = Path.Combine(Port.TempPath, $"git-{Guid.NewGuid():n}");
        ComposeCli.CreatePrivateDirectory(runDirectory);
        try
        {
            var environment = Environment(remote, runDirectory, AllowedProtocols);
            return new GitSession(tool, runDirectory, environment, remote?.Auth == GitAuth.SshKey, logger);
        }
        catch
        {
            GitSession.TryDelete(runDirectory, logger);
            throw;
        }
    }

    /// <summary>
    /// The variables git runs with. Files the session needs (an empty global config, the SSH key,
    /// its config and the known hosts) are written to <paramref name="runDirectory"/>.
    /// </summary>
    internal static Dictionary<string, string> Environment(GitRemote? remote, string runDirectory, string allowedProtocols)
    {
        var env = ComposeCli.BaseEnvironment(runDirectory);
        env.Remove("DOCKER_CONFIG");
        foreach (var key in PassedThrough)
        {
            if (System.Environment.GetEnvironmentVariable(key) is { Length: > 0 } value)
                env[key] = value;
        }

        var globalConfig = Path.Combine(runDirectory, "gitconfig");
        File.WriteAllText(globalConfig, "");
        env["GIT_CONFIG_NOSYSTEM"] = "1";
        env["GIT_CONFIG_GLOBAL"] = globalConfig;
        env["GIT_TERMINAL_PROMPT"] = "0";
        env["GIT_ALLOW_PROTOCOL"] = allowedProtocols;
        // A stalled transfer fails instead of hanging until the job's time limit.
        env["GIT_HTTP_LOW_SPEED_LIMIT"] = "1000";
        env["GIT_HTTP_LOW_SPEED_TIME"] = "60";
        // Configuration through the environment (git 2.31+), so nothing secret is on the command line.
        List<(string Key, string Value)> config = [("protocol.file.allow", allowedProtocols.Contains("file", StringComparison.Ordinal) ? "always" : "never")];

        switch (remote)
        {
            case { Auth: GitAuth.Token, Secret: { Length: > 0 } token }:
                var user = string.IsNullOrWhiteSpace(remote.Username) ? GitRemote.DefaultTokenUser : remote.Username.Trim();
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
                // Scoped to the repository's host: git sends it nowhere else, redirects included.
                if (GitSource.HttpOrigin(remote.Url) is not { } origin)
                    throw new GitException("A token can only be sent over http(s). Use an https:// address, or an SSH key.");
                config.Add(($"http.{origin}.extraHeader", $"Authorization: Basic {basic}"));
                break;

            case { Auth: GitAuth.SshKey, Secret: { Length: > 0 } key }:
                env["GIT_SSH_COMMAND"] = SshCommand(runDirectory, key, remote.KnownHosts);
                break;
        }

        if (remote is not null && GitSource.IsSsh(remote.Url) && !env.ContainsKey("GIT_SSH_COMMAND"))
            env["GIT_SSH_COMMAND"] = SshCommand(runDirectory, null, remote.KnownHosts);

        env["GIT_CONFIG_COUNT"] = config.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < config.Count; i++)
        {
            env[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
            env[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
        }
        return env;
    }

    /// <summary>The file holding the host keys an SSH session learnt or used.</summary>
    internal static string KnownHostsPath(string runDirectory) => Path.Combine(runDirectory, "known_hosts");

    /// <summary>
    /// <c>GIT_SSH_COMMAND</c>, which git runs through a shell: every path is single-quoted, and
    /// the run folder (ours) can't hold a quote. Host keys must match what's stored; with none
    /// stored, the server's keys are accepted and kept (trust on first use).
    /// </summary>
    private static string SshCommand(string runDirectory, string? key, string? knownHosts)
    {
        if (runDirectory.Contains('\'', StringComparison.Ordinal))
            throw new GitException("The server's temporary folder has a quote in its path, which SSH can't be given safely.");

        var hosts = KnownHostsPath(runDirectory);
        File.WriteAllText(hosts, knownHosts ?? "");
        var config = Path.Combine(runDirectory, "ssh_config");
        File.WriteAllText(config, "");
        List<string> command =
        [
            "ssh", "-F", Quote(config),
            "-o", "BatchMode=yes",
            "-o", "IdentitiesOnly=yes",
            "-o", $"UserKnownHostsFile={Quote(hosts)}",
            "-o", $"GlobalKnownHostsFile={Quote(config)}",
            "-o", $"StrictHostKeyChecking={(string.IsNullOrWhiteSpace(knownHosts) ? "accept-new" : "yes")}",
            "-o", "HashKnownHosts=no",
            "-o", "UpdateHostKeys=no",
            "-o", "ConnectTimeout=30",
        ];
        if (key is not null)
        {
            var keyFile = Path.Combine(runDirectory, "id_key");
            WritePrivate(keyFile, SshKeys.Normalize(key));
            command.AddRange(["-i", Quote(keyFile)]);
        }
        return string.Join(' ', command);
    }

    private static string Quote(string path) => $"'{path.Replace('\\', '/')}'";

    private static void WritePrivate(string path, string text)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, text);
            return;
        }
        using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        file.Write(Encoding.ASCII.GetBytes(text));
    }

    private async Task<(GitTool? Tool, string? Problem)> ProbeAsync(CancellationToken cancellationToken)
    {
        var file = Port.Git is { Length: > 0 } configured ? configured.Trim().Trim('"') : "git";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            var result = await ComposeCli.ExecuteAsync(ComposeCli.StartInfo(file, ["--version"]), null, timeout.Token);
            if (result.Succeeded && VersionPattern().Match(result.Stdout) is { Success: true } match)
            {
                logger.LogInformation("Using git {Version} ({Command})", match.Groups[1].Value, file);
                return (new GitTool(file, match.Groups[1].Value), null);
            }
            return Missing($"{file} didn't report a version: {string.Join(' ', result.Output.TakeLast(3))}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Missing(Port.Git is { Length: > 0 }
                ? $"Git isn't at '{file}'. Check the Port:Git setting."
                : "Git isn't installed on the Sockless Port server, and stacks from a repository need it. The image includes it; when running from source, install git.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Missing($"{file} didn't answer within {ProbeTimeout.TotalSeconds:0} seconds.");
        }

        (GitTool?, string?) Missing(string problem)
        {
            logger.LogWarning("Git is unavailable: {Problem}", problem);
            return (null, problem);
        }
    }

    [GeneratedRegex(@"git version (\d+\.\d+[^\s]*)")]
    private static partial Regex VersionPattern();
}

/// <summary>Git commands sharing one private folder and environment. Dispose to delete the folder (and the key in it).</summary>
public sealed class GitSession : IAsyncDisposable
{
    private readonly GitTool _tool;
    private readonly string _runDirectory;
    private readonly Dictionary<string, string> _environment;
    private readonly bool _ssh;
    private readonly ILogger _logger;

    internal GitSession(GitTool tool, string runDirectory, Dictionary<string, string> environment, bool ssh, ILogger logger)
    {
        _tool = tool;
        _runDirectory = runDirectory;
        _environment = environment;
        _ssh = ssh;
        _logger = logger;
    }

    public GitTool Tool => _tool;

    /// <summary>Runs git with <paramref name="arguments"/>. Its own folder is the working directory unless one is given.</summary>
    public async Task<ComposeResult> RunAsync(IReadOnlyList<string> arguments, Action<string>? onLine = null, CancellationToken cancellationToken = default)
    {
        var start = ComposeCli.StartInfo(_tool.FileName, arguments);
        start.WorkingDirectory = _runDirectory;
        start.Environment.Clear();
        foreach (var (key, value) in _environment)
            start.Environment[key] = value;
        return await ComposeCli.ExecuteAsync(start, onLine, cancellationToken);
    }

    /// <summary>The host keys in the session's <c>known_hosts</c>: those it was given, plus any learnt on the first connection.</summary>
    public string? KnownHosts()
    {
        var path = GitCli.KnownHostsPath(_runDirectory);
        if (!File.Exists(path))
            return null;
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines) + "\n";
    }

    /// <summary>Whether the session signs in with an SSH key (for hints about deploy keys).</summary>
    public bool UsesSshKey => _ssh;

    public ValueTask DisposeAsync()
    {
        TryDelete(_runDirectory, _logger);
        return ValueTask.CompletedTask;
    }

    internal static void TryDelete(string directory, ILogger logger)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't delete the git run folder {Path}", directory);
        }
    }
}
