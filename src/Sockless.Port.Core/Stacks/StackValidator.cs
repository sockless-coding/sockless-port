using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sockless.Port.Stacks;

/// <summary>A problem Compose reported, with the line of <c>compose.yaml</c> it points at when it says.</summary>
public sealed record ComposeProblem(string Message, int? Line);

/// <summary>The outcome of <c>docker compose config</c> on a stack's files.</summary>
/// <param name="Errors">Why the files are invalid; empty when they're valid.</param>
/// <param name="Warnings">Compose's own warnings (e.g. unset variables), and Sockless Port's (see <see cref="StackValidator"/>).</param>
/// <param name="Services">Service names, when valid.</param>
public sealed record ComposeCheck(IReadOnlyList<ComposeProblem> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<string> Services)
{
    public bool Valid => Errors.Count == 0;
}

/// <summary>
/// Validates a stack's files with <c>docker compose config</c> before they're saved or deployed,
/// and warns about what will go wrong on the engine although the file is valid: builds (there's no
/// build context), relative bind mounts (they resolve inside Sockless Port's own data folder) and,
/// on rootless engines, ports below 1024. Swarm stacks are also checked with <c>docker stack
/// config</c>, which knows what swarm accepts, and warned about settings swarm ignores.
/// </summary>
public sealed partial class StackValidator(ComposeCli compose, DockerCli docker, IOptions<PortOptions> options, ILogger<StackValidator> logger)
{
    /// <summary>Service settings <c>docker stack deploy</c> ignores, and what to do instead.</summary>
    internal static readonly IReadOnlyDictionary<string, string> IgnoredBySwarm = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["build"] = "swarm only runs images: push one to a registry and use image:",
        ["container_name"] = "swarm names tasks itself (stack_service.slot)",
        ["depends_on"] = "swarm starts services in any order; make them retry until what they need is up",
        ["restart"] = "use deploy.restart_policy instead",
        ["network_mode"] = "attach networks instead; host networking needs the predefined host network",
        ["links"] = "services on the same network reach each other by name",
        ["external_links"] = "services on the same network reach each other by name",
        ["devices"] = "swarm can't pass devices to tasks",
        ["security_opt"] = "swarm doesn't pass security options",
        ["userns_mode"] = "swarm doesn't pass a user namespace mode",
        ["cgroup_parent"] = "swarm doesn't pass a cgroup parent",
    };

    /// <param name="deployDirectory">Where the stack will run from, for the warnings about relative paths.</param>
    /// <param name="kind">Swarm stacks get swarm's own check and warnings.</param>
    /// <param name="rootless">The engine runs rootless, so ports below 1024 can't be published.</param>
    /// <exception cref="ComposeUnavailableException">Compose isn't installed.</exception>
    public async Task<ComposeCheck> CheckAsync(string projectName, StackContent content, string deployDirectory, StackKind kind = StackKind.Compose,
        bool rootless = false, CancellationToken cancellationToken = default, StackSource? source = null)
    {
        var swarm = kind == StackKind.Swarm;
        if (string.IsNullOrWhiteSpace(content.Compose))
            return new ComposeCheck([new ComposeProblem("The compose file is empty. Add at least one service.", null)], [], []);

        // Full path: Compose prints it normalised, and messages are cleaned by matching it.
        var directory = Path.GetFullPath(Path.Combine(options.Value.TempPath, $"validate-{Guid.NewGuid():n}"));
        Directory.CreateDirectory(directory);
        try
        {
            await new StackFiles(directory).WriteAsync(0, "check", content, cancellationToken);
            var project = new StackFiles(directory).Directory(0, "check");
            var result = await compose.RunAsync(
                new ComposeRequest(projectName, ["config", "--format", "json"], ProjectDirectory: project, Source: source), cancellationToken: cancellationToken);

            var stdout = result.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
            var messages = result.Output
                .Where(l => !stdout.Contains(l) && !string.IsNullOrWhiteSpace(l))
                .Select(l => Clean(l, project, source))
                .ToList();

            if (!result.Succeeded)
            {
                var errors = (messages.Count > 0 ? messages : result.Output.Select(l => Clean(l, project, source)).Where(l => l.Length > 0))
                    .Select(m => new ComposeProblem(m, LineOf(m)))
                    .ToList();
                if (errors.Count == 0)
                    errors.Add(new ComposeProblem($"Compose rejected the file (exit code {result.ExitCode}).", null));
                return new ComposeCheck(errors, [], []);
            }

            var warnings = messages.Select(m => WarningPrefix().Replace(m, ""))
                // Compose warns that deploy settings only apply to swarm, which is where this goes.
                .Where(m => !swarm || !m.Contains("Swarm", StringComparison.Ordinal))
                .ToList();
            JsonObject? model;
            try
            {
                model = JsonNode.Parse(result.Stdout) as JsonObject;
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Compose config output wasn't JSON");
                return new ComposeCheck([], warnings, []);
            }

            var services = model?["services"] as JsonObject;
            warnings.AddRange(Analyse(services, source?.Directory ?? project, source?.Directory ?? deployDirectory, rootless && !swarm));
            if (swarm)
            {
                warnings.AddRange(AnalyseSwarm(services, content));
                if (await CheckSwarmAsync(project, content, source, cancellationToken) is { } swarmCheck)
                {
                    if (swarmCheck.Errors.Count > 0)
                        return new ComposeCheck(swarmCheck.Errors, warnings, []);
                    warnings.AddRange(swarmCheck.Warnings);
                }
            }
            return new ComposeCheck([], warnings, [.. (services?.Select(s => s.Key) ?? []).Order(StringComparer.Ordinal)]);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Couldn't delete {Path}", directory);
            }
        }
    }

    /// <summary>Warnings about a valid model. <paramref name="project"/> is where Compose resolved relative paths.</summary>
    internal static IEnumerable<string> Analyse(JsonObject? services, string project, string deployDirectory, bool rootless)
    {
        if (services is null)
            yield break;

        foreach (var (name, node) in services)
        {
            if (node is not JsonObject service)
                continue;

            if (service["build"] is JsonObject build && build["context"]?.GetValue<string>() is { } context
                && !context.Contains("://", StringComparison.Ordinal) && !context.StartsWith("git@", StringComparison.Ordinal))
            {
                yield return service["image"] is null
                    ? $"{name} is built from a local folder (build:). Sockless Port keeps only compose.yaml and .env, so there's nothing to build from: use image: instead."
                    : $"{name} has both image: and a local build:. Compose pulls the image and only builds if that fails, which it can't here: there's no build context.";
            }

            foreach (var volume in service["volumes"] as JsonArray ?? [])
            {
                if (volume?["type"]?.GetValue<string>() == "bind" && volume["source"]?.GetValue<string>() is { } source && IsUnder(source, project))
                {
                    var relative = "./" + Path.GetRelativePath(project, source).Replace('\\', '/');
                    yield return $"{name} mounts {relative}, which resolves to {Path.Combine(deployDirectory, Path.GetRelativePath(project, source))} inside Sockless Port's data folder. The engine looks for that path on its own host, where it usually doesn't exist: use an absolute host path or a named volume.";
                }
            }

            if (rootless)
            {
                foreach (var port in service["ports"] as JsonArray ?? [])
                {
                    if (port?["published"]?.ToString() is { } published && FirstPort(published) is > 0 and < 1024 and var low)
                        yield return $"{name} publishes port {low}. This engine runs rootless, which can't bind ports below 1024 unless the host allows it (net.ipv4.ip_unprivileged_port_start).";
                }
            }
        }
    }

    /// <summary>Settings swarm ignores, and <c>.env</c> variables that can't reach <c>docker stack deploy</c>.</summary>
    internal static IEnumerable<string> AnalyseSwarm(JsonObject? services, StackContent content)
    {
        foreach (var (name, node) in services ?? [])
        {
            if (node is not JsonObject service)
                continue;
            foreach (var (key, instead) in IgnoredBySwarm.Where(i => service.ContainsKey(i.Key)))
                yield return $"{name} sets {key}:, which swarm ignores: {instead}.";
        }
        foreach (var reserved in StackEnv.Parse(content.Env).Keys.Where(StackEnv.IsReserved))
            yield return $".env sets {reserved}, which Sockless Port sets itself for the deploy, so ${{{reserved}}} won't be what .env says.";
    }

    /// <summary><c>docker stack config</c>: null when it ran and found nothing wrong.</summary>
    private async Task<ComposeCheck?> CheckSwarmAsync(string project, StackContent content, StackSource? source, CancellationToken cancellationToken)
    {
        var (tool, problem) = await docker.StatusAsync(cancellationToken);
        if (tool is null)
            return new ComposeCheck([], [$"Swarm's own checks were skipped: {problem}"], []);

        // docker stack resolves relative paths against the compose file's folder: a git stack's checkout.
        var file = source?.ComposeFile ?? Path.Combine(project, StackFiles.ComposeFile);
        var result = await docker.RunAsync(new DockerCliRequest(
            ["stack", "config", "--compose-file", file], WorkingDirectory: source?.Directory ?? project, Variables: StackEnv.Parse(content.Env)),
            cancellationToken: cancellationToken);
        if (result.Succeeded)
            return null;
        var stdout = result.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
        var errors = result.Output.Where(l => !string.IsNullOrWhiteSpace(l) && !stdout.Contains(l))
            .Select(l => Clean(l, project, source))
            .Select(m => new ComposeProblem($"Swarm: {m}", LineOf(m)))
            .ToList();
        if (errors.Count == 0)
            errors.Add(new ComposeProblem($"docker stack config rejected the file (exit code {result.ExitCode}).", null));
        return new ComposeCheck(errors, [], []);
    }

    private static int FirstPort(string published) =>
        int.TryParse(published.Split('-')[0], System.Globalization.CultureInfo.InvariantCulture, out var port) ? port : 0;

    private static bool IsUnder(string path, string directory)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
               || string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
    }

    /// <summary>Messages name the temporary file (or a git stack's checkout); people know it as compose.yaml.</summary>
    internal static string Clean(string message, string project, StackSource? source = null)
    {
        message = message.Trim();
        List<(string Path, string Name)> forms =
        [
            (Path.Combine(project, StackFiles.ComposeFile), StackFiles.ComposeFile),
            (Path.Combine(project, StackFiles.EnvFile), StackFiles.EnvFile),
            (project, "."),
        ];
        if (source is not null)
            forms.InsertRange(0, [(source.ComposeFile, Path.GetFileName(source.ComposeFile)), (source.Directory, ".")]);
        foreach (var (form, name) in forms)
            message = message.Replace(form, name, StringComparison.OrdinalIgnoreCase);
        return message;
    }

    /// <summary>The line an error points at: go-yaml's <c>L4.C3</c> (the last one given) or <c>line 4:</c>.</summary>
    internal static int? LineOf(string message)
    {
        var matches = PositionPattern().Matches(message);
        if (matches.Count > 0 && int.TryParse(matches[^1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var line))
            return line;
        return LinePattern().Match(message) is { Success: true } m && int.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : null;
    }

    [GeneratedRegex(@"\bL(\d+)\.C\d+")]
    private static partial Regex PositionPattern();

    [GeneratedRegex(@"\bline (\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LinePattern();

    /// <summary><c>WARN[0000] </c> or <c>time="…" level=warning msg=</c> before Compose's warnings.</summary>
    [GeneratedRegex(@"^(WARN\[\d+\]\s*|time=""[^""]*""\s+level=warning\s+msg=)")]
    private static partial Regex WarningPrefix();
}
