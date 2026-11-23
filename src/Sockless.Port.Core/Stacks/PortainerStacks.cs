using System.Formats.Tar;
using System.Text;
using System.Text.RegularExpressions;
using Sockless.Port.Containers;
using Sockless.Port.Docker;

namespace Sockless.Port.Stacks;

/// <summary>An engine to look for Portainer on, by the name of its endpoint.</summary>
public sealed record PortainerSearch(string Endpoint, DockerClient Client);

/// <summary>A Compose project's files, found in Portainer's data.</summary>
/// <param name="Endpoint">The endpoint whose engine runs that Portainer.</param>
/// <param name="Container">The Portainer container the files were read from.</param>
/// <param name="ComposePath">The compose file, as a path inside that container.</param>
/// <param name="RewroteEnvFile">References to <c>stack.env</c> were changed to <c>.env</c>.</param>
public sealed record PortainerFiles(StackContent Content, string Endpoint, string Container, string ComposePath, bool RewroteEnvFile);

/// <summary>
/// Reads the files of a Compose project that Portainer deployed. Portainer keeps each stack in
/// <c>{data}/compose/{id}/</c> inside its own container, writes the stack's variables to
/// <c>stack.env</c> there, and passes that file to Compose. The project's labels name those paths,
/// so the files are read out of the container that mounts <c>{data}</c>, through the engine's
/// archive endpoint (what <c>docker cp</c> uses): a read, with no helper container and no exec.
/// Portainer often deploys through its agent, so it may run on another endpoint than the project.
/// </summary>
public static partial class PortainerStacks
{
    /// <summary>The file Portainer writes a stack's variables to.</summary>
    public const string EnvFile = "stack.env";

    internal const string EnvironmentFileLabel = "com.docker.compose.project.environment_file";

    private const long MaxFileBytes = 1024 * 1024;

    /// <summary>The project's one compose file lies in a numbered folder under <c>compose/</c>, as Portainer lays them out.</summary>
    public static bool LooksDeployedByPortainer(ComposeProject project) =>
        project.ConfigFiles is [var path] && StackFolderPattern().IsMatch(path);

    /// <summary>
    /// The project's files from the first Portainer in <paramref name="engines"/> (in order) that has them,
    /// or null when none does or it isn't a Portainer project.
    /// </summary>
    /// <exception cref="PortainerAmbiguousException">Several Portainers have a stack at that path, and none on the first engine.</exception>
    public static async Task<PortainerFiles?> FindAsync(ComposeProject project, IReadOnlyList<PortainerSearch> engines, CancellationToken cancellationToken = default)
    {
        if (!LooksDeployedByPortainer(project))
            return null;

        var found = new List<PortainerFiles>();
        for (var i = 0; i < engines.Count; i++)
        {
            try
            {
                if (await FindOnAsync(project, engines[i], cancellationToken) is { } files)
                {
                    if (i == 0)
                        return files; // the project's own engine wins
                    found.Add(files);
                }
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or InvalidDataException or TimeoutException)
            {
                // That engine can't be read now; look on the others.
            }
        }
        return found switch
        {
            [] => null,
            [var only] => only,
            _ => throw new PortainerAmbiguousException(project.ConfigFiles[0], [.. found.Select(f => $"{f.Container} on {f.Endpoint}")]),
        };
    }

    private static async Task<PortainerFiles?> FindOnAsync(ComposeProject project, PortainerSearch engine, CancellationToken cancellationToken)
    {
        var composePath = project.ConfigFiles[0];
        var ownIds = project.Containers.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var container in await engine.Client.ListContainersAsync(all: true, cancellationToken))
        {
            if (ownIds.Contains(container.Id) || ContainerLabels.IsHelper(container.Labels))
                continue;
            if ((container.Mounts ?? []).Select(m => m.Destination).OfType<string>().FirstOrDefault(d => StackFolder(composePath, d) is not null) is not { } dataRoot)
                continue;

            var compose = await ReadFileAsync(engine.Client, container.Id, composePath, cancellationToken);
            if (compose is null)
                continue; // another Portainer, or not one at all

            var folder = StackFolder(composePath, dataRoot)!;
            var env = "";
            foreach (var candidate in EnvCandidates(project, folder).Distinct(StringComparer.Ordinal))
            {
                if (await ReadFileAsync(engine.Client, container.Id, candidate, cancellationToken) is { } text)
                {
                    env = text;
                    break;
                }
            }

            var content = StackContent.Normalize(compose, env);
            var (rewritten, changed) = RewriteEnvFile(content.Compose);
            return new PortainerFiles(content with { Compose = rewritten }, engine.Endpoint, container.DisplayName, composePath, changed);
        }
        return null;
    }

    /// <summary>
    /// Where the project's variables may be, in order: the env files Compose recorded (only inside the
    /// stack's folder), Portainer's <c>stack.env</c>, and the <c>.env</c> Compose reads by default.
    /// </summary>
    internal static IEnumerable<string> EnvCandidates(ComposeProject project, string folder)
    {
        var recorded = project.Containers.Select(c => c.Labels?.GetValueOrDefault(EnvironmentFileLabel)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        foreach (var path in recorded?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
        {
            if (IsInside(path, folder))
                yield return path;
        }
        yield return $"{folder}/{EnvFile}";
        // Git stacks may keep a .env next to the compose file, in a folder of the clone.
        var workingDir = project.WorkingDirectory?.TrimEnd('/');
        var dotEnvFolder = workingDir is not null && (workingDir == folder || IsInside(workingDir, folder)) ? workingDir : folder;
        yield return $"{dotEnvFolder}/{StackFiles.EnvFile}";
    }

    /// <summary>The stack's folder (<c>{dataRoot}/compose/{id}</c>) when <paramref name="path"/> lies in one; otherwise null.</summary>
    internal static string? StackFolder(string path, string dataRoot)
    {
        var root = dataRoot.TrimEnd('/');
        var prefix = root + "/compose/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var rest = path[prefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || !rest[..slash].All(char.IsAsciiDigit) || rest[(slash + 1)..].Length == 0)
            return null;
        return prefix + rest[..slash];
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder + "/", StringComparison.Ordinal) && !path.Split('/').Contains("..");

    /// <summary>
    /// Points <c>env_file:</c> entries naming Portainer's <c>stack.env</c> at <c>.env</c>, which holds the
    /// same variables in a stack here. Only whole values that are exactly that file are changed.
    /// </summary>
    internal static (string Compose, bool Changed) RewriteEnvFile(string compose)
    {
        var changed = false;
        var result = EnvFileReference().Replace(compose, m =>
        {
            changed = true;
            return $"{m.Groups["lead"].Value}{m.Groups["q"].Value}{StackFiles.EnvFile}{m.Groups["q"].Value}{m.Groups["tail"].Value}";
        });
        return (result, changed);
    }

    /// <summary>A regular file's text, or null when it's missing, not a regular file or too large.</summary>
    private static async Task<string?> ReadFileAsync(DockerClient client, string containerId, string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var archive = await client.GetArchiveAsync(containerId, path, cancellationToken);
            await using var tar = new TarReader(archive);
            var entry = await tar.GetNextEntryAsync(copyData: false, cancellationToken);
            if (entry is not { EntryType: TarEntryType.RegularFile or TarEntryType.V7RegularFile, DataStream: { } data } || entry.Length > MaxFileBytes)
                return null;
            using var reader = new StreamReader(data, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (DockerApiException ex) when (ex.IsNotFound)
        {
            return null;
        }
    }

    [GeneratedRegex(@"/compose/\d+/[^/]")]
    private static partial Regex StackFolderPattern();

    // "env_file: stack.env", "- stack.env", "- path: ./stack.env", optionally quoted, optionally with a comment.
    [GeneratedRegex(@"^(?<lead>[ \t]*(?:(?:-[ \t]+)?(?:env_file|path):|-)[ \t]+)(?<q>[""']?)(?:\./)?stack\.env\k<q>(?<tail>[ \t]*(?:#.*)?)$", RegexOptions.Multiline)]
    private static partial Regex EnvFileReference();
}

/// <summary>Several Portainers have a stack at the project's path, so which one deployed it isn't known.</summary>
public sealed class PortainerAmbiguousException(string path, IReadOnlyList<string> candidates)
    : Exception($"Several Portainers have a stack at {path}: {string.Join(", ", candidates)}.")
{
    public IReadOnlyList<string> Candidates { get; } = candidates;
}
