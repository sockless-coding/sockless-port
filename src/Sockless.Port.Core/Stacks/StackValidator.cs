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
/// on rootless engines, ports below 1024.
/// </summary>
public sealed partial class StackValidator(ComposeCli compose, IOptions<PortOptions> options, ILogger<StackValidator> logger)
{
    /// <param name="deployDirectory">Where the stack will run from, for the warnings about relative paths.</param>
    /// <param name="rootless">The engine runs rootless, so ports below 1024 can't be published.</param>
    /// <exception cref="ComposeUnavailableException">Compose isn't installed.</exception>
    public async Task<ComposeCheck> CheckAsync(string projectName, StackContent content, string deployDirectory, bool rootless = false, CancellationToken cancellationToken = default)
    {
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
                new ComposeRequest(projectName, ["config", "--format", "json"], ProjectDirectory: project), cancellationToken: cancellationToken);

            var stdout = result.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
            var messages = result.Output
                .Where(l => !stdout.Contains(l) && !string.IsNullOrWhiteSpace(l))
                .Select(l => Clean(l, project))
                .ToList();

            if (!result.Succeeded)
            {
                var errors = (messages.Count > 0 ? messages : result.Output.Select(l => Clean(l, project)).Where(l => l.Length > 0))
                    .Select(m => new ComposeProblem(m, LineOf(m)))
                    .ToList();
                if (errors.Count == 0)
                    errors.Add(new ComposeProblem($"Compose rejected the file (exit code {result.ExitCode}).", null));
                return new ComposeCheck(errors, [], []);
            }

            var warnings = messages.Select(m => WarningPrefix().Replace(m, "")).ToList();
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
            warnings.AddRange(Analyse(services, project, deployDirectory, rootless));
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

    private static int FirstPort(string published) =>
        int.TryParse(published.Split('-')[0], System.Globalization.CultureInfo.InvariantCulture, out var port) ? port : 0;

    private static bool IsUnder(string path, string directory)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
               || string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
    }

    /// <summary>Messages name the temporary file; people know it as compose.yaml.</summary>
    internal static string Clean(string message, string project)
    {
        message = message.Trim();
        foreach (var form in (string[])[Path.Combine(project, StackFiles.ComposeFile), Path.Combine(project, StackFiles.EnvFile), project])
            message = message.Replace(form, form == project ? "." : Path.GetFileName(form), StringComparison.OrdinalIgnoreCase);
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
