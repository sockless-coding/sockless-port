using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Stacks;

/// <summary>A compose file for a project deployed outside Sockless Port, and where it came from.</summary>
/// <param name="Source">Its compose file, when Sockless Port can read it; null when the file was rebuilt from the containers.</param>
public sealed record AdoptedStack(StackContent Content, string? Source)
{
    /// <summary>Set when the files were read from Portainer's data (<see cref="Source"/> is then a path in its container).</summary>
    public PortainerFiles? Portainer { get; init; }

    /// <summary>Why a file that should have been readable wasn't, when it was rebuilt anyway.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Takes over a Compose project that was deployed some other way. The project's own compose file
/// is used when Sockless Port can read it (the label names its path). Otherwise a file is rebuilt
/// from the containers, leaving out what the image already sets; it's a starting point to check,
/// since containers don't record everything Compose knew (build, depends_on, profiles…).
/// Projects Portainer deployed are read from Portainer's data instead (<see cref="PortainerStacks"/>).
/// </summary>
public static class StackAdopter
{
    private const long MaxFileBytes = 1024 * 1024;

    /// <param name="portainer">
    /// Engines to look for the Portainer that deployed the project on, the project's own first.
    /// Empty to skip that.
    /// </param>
    public static async Task<AdoptedStack> AdoptAsync(DockerClient client, ComposeProject project, IReadOnlyList<PortainerSearch> portainer,
        CancellationToken cancellationToken = default)
    {
        if (TryReadOriginal(project) is { } original)
            return original;

        string? note = null;
        try
        {
            if (await PortainerStacks.FindAsync(project, portainer, cancellationToken) is { } files)
                return new AdoptedStack(files.Content, files.ComposePath) { Portainer = files };
            if (PortainerStacks.LooksDeployedByPortainer(project))
                note = "It looks like Portainer deployed it, but no Portainer container on an endpoint you operate has its files.";
        }
        catch (PortainerAmbiguousException ex)
        {
            note = $"{ex.Message} Copy the right one in by hand.";
        }

        var services = new List<ServiceSource>();
        foreach (var service in project.Services)
        {
            var first = service.Containers[0];
            var (container, json) = await client.InspectContainerAsync(first.Id, cancellationToken);
            ImageConfig? image = null;
            try
            {
                image = (await client.InspectImageAsync(container.Image ?? first.ImageId ?? first.Image ?? "", cancellationToken)).Image.Config;
            }
            catch (DockerApiException ex) when (ex.IsNotFound)
            {
                // The image is gone; keep everything the container has.
            }
            services.Add(new ServiceSource(service.Name, container, JsonNode.Parse(json) as JsonObject, image));
        }
        return new AdoptedStack(new StackContent(Rebuild(project.Name, services), ""), null) { Note = note };
    }

    /// <summary>The project's compose file and its folder's <c>.env</c>, if this server can read them.</summary>
    private static AdoptedStack? TryReadOriginal(ComposeProject project)
    {
        if (project.ConfigFiles is not [var path])
            return null; // several files (overrides) can't be merged into one here
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxFileBytes)
                return null;
            var compose = File.ReadAllText(file.FullName);
            var envPath = project.WorkingDirectory is { } dir ? Path.Combine(dir, StackFiles.EnvFile) : null;
            var env = envPath is not null && File.Exists(envPath) && new FileInfo(envPath).Length <= MaxFileBytes ? File.ReadAllText(envPath) : "";
            return new AdoptedStack(StackContent.Normalize(compose, env), file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal sealed record ServiceSource(string Name, ContainerInspect Container, JsonObject? Raw, ImageConfig? Image);

    /// <summary>The compose YAML for <paramref name="services"/>.</summary>
    internal static string Rebuild(string project, IReadOnlyList<ServiceSource> services)
    {
        var yaml = new StringBuilder();
        yaml.Append("# Rebuilt by Sockless Port from the containers of ").Append(project).Append(".\n");
        yaml.Append("# Check it before deploying: containers don't record build:, depends_on:, profiles: or\n");
        yaml.Append("# healthchecks the way Compose wrote them, and settings the image provides are left out.\n");
        yaml.Append("services:\n");

        var volumes = new SortedDictionary<string, string?>(StringComparer.Ordinal);  // key → external name, or null for the project's own
        var networks = new SortedDictionary<string, string?>(StringComparer.Ordinal);

        foreach (var service in services)
        {
            var c = service.Container;
            var config = c.Config;
            var host = c.HostConfig;
            var raw = service.Raw;
            var image = service.Image;

            yaml.Append("  ").Append(Key(service.Name)).Append(":\n");
            Scalar(yaml, 2, "image", config?.Image ?? c.Image ?? "");

            var name = c.DisplayName;
            if (!IsDefaultName(project, service.Name, name))
                Scalar(yaml, 2, "container_name", name);

            if (config?.Entrypoint is { Count: > 0 } entrypoint && !Same(entrypoint, image?.Entrypoint))
                Flow(yaml, 2, "entrypoint", entrypoint);
            if (config?.Cmd is { Count: > 0 } cmd && !Same(cmd, image?.Cmd))
                Flow(yaml, 2, "command", cmd);

            var imageEnv = image?.Env?.ToHashSet(StringComparer.Ordinal) ?? [];
            var env = (config?.Env ?? []).Where(e => !imageEnv.Contains(e)).ToList();
            if (env.Count > 0)
            {
                yaml.Append("    environment:\n");
                foreach (var entry in env)
                {
                    var eq = entry.IndexOf('=', StringComparison.Ordinal);
                    yaml.Append("      ").Append(Key(eq < 0 ? entry : entry[..eq])).Append(": ").Append(Quote(eq < 0 ? "" : entry[(eq + 1)..])).Append('\n');
                }
            }

            var ports = Ports(host?.PortBindings).ToList();
            if (ports.Count > 0)
                List(yaml, 2, "ports", ports);

            var mounts = new List<string>();
            foreach (var mount in c.Mounts ?? [])
            {
                var ro = mount.ReadWrite ? "" : ":ro";
                switch (mount.Type)
                {
                    case "bind" when mount.Source is { Length: > 0 } source:
                        mounts.Add($"{source}:{mount.Destination}{ro}");
                        break;
                    case "volume" when mount.Name is { } volume && IsAnonymous(volume):
                        // The image's VOLUME gets a fresh anonymous volume anyway.
                        if (image?.Volumes?.ContainsKey(mount.Destination ?? "") != true)
                            mounts.Add(mount.Destination ?? "");
                        break;
                    case "volume" when mount.Name is { } volume:
                        var key = Own(project, volume) ?? volume;
                        volumes[key] = Own(project, volume) is null ? volume : null;
                        mounts.Add($"{key}:{mount.Destination}{ro}");
                        break;
                }
            }
            if (mounts.Count > 0)
                List(yaml, 2, "volumes", mounts);

            var mode = host?.NetworkMode;
            if (mode is "host" or "none" || mode?.StartsWith("container:", StringComparison.Ordinal) == true)
            {
                Scalar(yaml, 2, "network_mode", mode!);
            }
            else
            {
                var joined = (c.NetworkSettings?.Networks?.Keys ?? []).Order(StringComparer.Ordinal).ToList();
                var keys = new List<string>();
                foreach (var network in joined)
                {
                    if (network == $"{project}_default")
                    {
                        keys.Add("default");
                        continue;
                    }
                    var key = Own(project, network) ?? network;
                    networks[key] = Own(project, network) is null ? network : null;
                    keys.Add(key);
                }
                if (keys is not ([] or ["default"]))
                    List(yaml, 2, "networks", keys.OrderBy(k => k != "default").ThenBy(k => k, StringComparer.Ordinal));
            }

            if (host?.RestartPolicy?.Name is { Length: > 0 } policy and not "no")
                Scalar(yaml, 2, "restart", policy == "on-failure" && host.RestartPolicy.MaximumRetryCount > 0
                    ? $"on-failure:{host.RestartPolicy.MaximumRetryCount.ToString(CultureInfo.InvariantCulture)}"
                    : policy);

            if (config?.User is { Length: > 0 } user && user != image?.User)
                Scalar(yaml, 2, "user", user);
            if (config?.WorkingDir is { Length: > 0 } workdir && workdir != image?.WorkingDir)
                Scalar(yaml, 2, "working_dir", workdir);
            if (host?.Privileged == true)
                yaml.Append("    privileged: true\n");
            if (config?.Tty == true)
                yaml.Append("    tty: true\n");
            if (config?.OpenStdin == true)
                yaml.Append("    stdin_open: true\n");
            if (Strings(raw?["HostConfig"]?["CapAdd"]) is { Count: > 0 } capAdd)
                List(yaml, 2, "cap_add", capAdd);
            if (Strings(raw?["HostConfig"]?["CapDrop"]) is { Count: > 0 } capDrop)
                List(yaml, 2, "cap_drop", capDrop);
            if (Strings(raw?["HostConfig"]?["ExtraHosts"]) is { Count: > 0 } hosts)
                List(yaml, 2, "extra_hosts", hosts);
            if (host?.Memory is > 0 and var memory)
                yaml.Append("    mem_limit: ").Append(Quote($"{(memory / 1024 / 1024).ToString(CultureInfo.InvariantCulture)}m")).Append('\n');
            if (host?.NanoCpus is > 0 and var nano)
                yaml.Append("    cpus: ").Append((nano / 1_000_000_000m).ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');

            var imageLabels = image?.Labels ?? new Dictionary<string, string>();
            var labels = (config?.Labels ?? new Dictionary<string, string>())
                .Where(l => !ContainerLabels.IsOrchestratorLabel(l.Key) && !(imageLabels.TryGetValue(l.Key, out var v) && v == l.Value))
                .OrderBy(l => l.Key, StringComparer.Ordinal)
                .ToList();
            if (labels.Count > 0)
            {
                yaml.Append("    labels:\n");
                foreach (var (key, value) in labels)
                    yaml.Append("      ").Append(Key(key)).Append(": ").Append(Quote(value)).Append('\n');
            }
        }

        Section(yaml, "volumes", volumes);
        Section(yaml, "networks", networks);
        return yaml.ToString();
    }

    private static void Section(StringBuilder yaml, string name, SortedDictionary<string, string?> entries)
    {
        if (entries.Count == 0)
            return;
        yaml.Append(name).Append(":\n");
        foreach (var (key, external) in entries)
        {
            if (external is null)
            {
                yaml.Append("  ").Append(Key(key)).Append(": {}\n");
            }
            else
            {
                // Not created by this project: use it, don't create or remove it.
                yaml.Append("  ").Append(Key(key)).Append(":\n");
                yaml.Append("    external: true\n");
                Scalar(yaml, 2, "name", external);
            }
        }
    }

    /// <summary>The short name of a volume or network the project created (<c>{project}_{name}</c>).</summary>
    private static string? Own(string project, string name) =>
        name.StartsWith(project + "_", StringComparison.Ordinal) && name.Length > project.Length + 1 ? name[(project.Length + 1)..] : null;

    /// <summary>Compose v2 names containers <c>{project}-{service}-{n}</c>; v1 used underscores.</summary>
    private static bool IsDefaultName(string project, string service, string name) =>
        name.StartsWith($"{project}-{service}-", StringComparison.Ordinal) || name.StartsWith($"{project}_{service}_", StringComparison.Ordinal);

    private static bool IsAnonymous(string name) => name is { Length: 64 } && name.All(char.IsAsciiHexDigitLower);

    private static bool Same(IReadOnlyList<string> a, IReadOnlyList<string>? b) => b is not null && a.SequenceEqual(b, StringComparer.Ordinal);

    private static IEnumerable<string> Ports(IReadOnlyDictionary<string, IReadOnlyList<PortBinding>?>? bindings)
    {
        foreach (var (key, list) in (bindings ?? new Dictionary<string, IReadOnlyList<PortBinding>?>()).OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            var (port, protocol) = key.Split('/') is [var p, var proto] ? (p, proto) : (key, "tcp");
            var suffix = protocol == "tcp" ? "" : $"/{protocol}";
            foreach (var binding in list ?? [])
            {
                var hostIp = binding.HostIp is { Length: > 0 } ip and not ("0.0.0.0" or "::") ? (ip.Contains(':', StringComparison.Ordinal) ? $"[{ip}]:" : $"{ip}:") : "";
                yield return binding.HostPort is { Length: > 0 } hostPort ? $"{hostIp}{hostPort}:{port}{suffix}" : $"{port}{suffix}";
            }
        }
    }

    private static List<string>? Strings(JsonNode? node) =>
        node is JsonArray array ? [.. array.Select(n => n?.GetValue<string>()).OfType<string>()] : null;

    // ---------------------------------------------------------------- YAML

    private static void Scalar(StringBuilder yaml, int depth, string key, string value) =>
        yaml.Append(' ', depth * 2).Append(key).Append(": ").Append(Quote(value)).Append('\n');

    private static void List(StringBuilder yaml, int depth, string key, IEnumerable<string> values)
    {
        yaml.Append(' ', depth * 2).Append(key).Append(":\n");
        foreach (var value in values)
            yaml.Append(' ', depth * 2 + 2).Append("- ").Append(Quote(value)).Append('\n');
    }

    private static void Flow(StringBuilder yaml, int depth, string key, IEnumerable<string> values) =>
        yaml.Append(' ', depth * 2).Append(key).Append(": [").Append(string.Join(", ", values.Select(Quote))).Append("]\n");

    /// <summary>
    /// A double-quoted YAML scalar. JSON string escapes are valid in YAML's double-quoted style, and
    /// <c>$</c> is doubled so Compose doesn't read the value as interpolation.
    /// </summary>
    internal static string Quote(string value) =>
        $"\"{JsonEncodedText.Encode(value.Replace("$", "$$", StringComparison.Ordinal), JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value}\"";

    /// <summary>A mapping key: plain when it's simple, quoted otherwise.</summary>
    internal static string Key(string key) =>
        key.Length > 0 && key.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/') && !char.IsAsciiDigit(key[0]) ? key : Quote(key);
}
