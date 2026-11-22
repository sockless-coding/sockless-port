using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sockless.Port.Containers;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Swarm;

/// <summary>
/// What the service form collects, in the text formats of <c>docker service create</c>'s flags,
/// one entry per line: ports <c>8080:80</c>, mounts <c>data:/data:ro</c>, constraints
/// <c>node.role==manager</c>, sizes <c>512M</c>, durations <c>10s</c>.
/// </summary>
public sealed class ServiceForm
{
    /// <summary>Only when creating: a service's name can't change.</summary>
    public string? Name { get; set; }

    public string Image { get; set; } = "";

    /// <summary><c>replicated</c> or <c>global</c>. Fixed once the service exists.</summary>
    public string Mode { get; set; } = "replicated";

    public int? Replicas { get; set; } = 1;

    /// <summary>The image's command replaced, shell-style quoting (the spec's <c>Args</c>).</summary>
    public string? Command { get; set; }

    public string? Environment { get; set; }
    public string? Labels { get; set; }

    /// <summary><c>8080:80</c>, <c>8080:80/udp</c>, <c>80</c> (the engine picks the published port); add <c>host</c> after a space to publish only where a task runs.</summary>
    public string? Ports { get; set; }

    /// <summary><c>name:/data</c> (volume), <c>/srv/www:/www</c> (bind), <c>/cache</c> (anonymous volume), each optionally <c>:ro</c>.</summary>
    public string? Mounts { get; set; }

    /// <summary>Network names.</summary>
    public List<string> Networks { get; set; } = [];

    /// <summary>Secret names; each becomes <c>/run/secrets/{name}</c>.</summary>
    public List<string> Secrets { get; set; } = [];

    /// <summary>Config names; each becomes <c>/{name}</c>.</summary>
    public List<string> Configs { get; set; } = [];

    /// <summary>One per line, e.g. <c>node.role==manager</c>, <c>node.labels.zone!=a</c>.</summary>
    public string? Constraints { get; set; }

    /// <summary>One per line: <c>spread=node.labels.zone</c> (or just the label).</summary>
    public string? Preferences { get; set; }

    public string? CpuLimit { get; set; }
    public string? MemoryLimit { get; set; }
    public string? CpuReservation { get; set; }
    public string? MemoryReservation { get; set; }

    public int? UpdateParallelism { get; set; } = 1;
    public string? UpdateDelay { get; set; } = "0s";
    public string UpdateFailureAction { get; set; } = "pause";
    public string UpdateOrder { get; set; } = "stop-first";

    public int? RollbackParallelism { get; set; } = 1;
    public string? RollbackDelay { get; set; } = "0s";
    public string RollbackFailureAction { get; set; } = "pause";
    public string RollbackOrder { get; set; } = "stop-first";
}

/// <summary>What a service can refer to on this swarm, by name → ID.</summary>
/// <param name="Networks">Networks services can attach to: swarm-scoped ones.</param>
public sealed record ServiceReferences(
    IReadOnlyDictionary<string, string> Networks,
    IReadOnlyDictionary<string, string> Secrets,
    IReadOnlyDictionary<string, string> Configs)
{
    public static ServiceReferences Empty { get; } = new(new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>());

    public static ServiceReferences From(IEnumerable<NetworkSummary> networks, IEnumerable<SwarmSecret> secrets, IEnumerable<SwarmConfig> configs) => new(
        Index(networks.Where(n => n.Scope == "swarm" && !n.Ingress).Select(n => (n.Name, n.Id))),
        Index(secrets.Select(s => (s.Name, s.Id))),
        Index(configs.Select(c => (c.Name, c.Id))));

    private static Dictionary<string, string> Index(IEnumerable<(string Name, string Id)> items)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, id) in items)
            index.TryAdd(name, id);
        return index;
    }
}

public static partial class ServiceSpecBuilder
{
    public static readonly IReadOnlyList<string> Modes = ["replicated", "global"];
    public static readonly IReadOnlyList<string> UpdateFailureActions = ["pause", "continue", "rollback"];
    public static readonly IReadOnlyList<string> RollbackFailureActions = ["pause", "continue"];
    public static readonly IReadOnlyList<string> Orders = ["stop-first", "start-first"];

    /// <summary>File permissions the Docker CLI gives secrets and configs: 0444.</summary>
    private const int ReadOnlyFileMode = 292;

    /// <summary>What the Docker CLI watches a new task for, after it starts, before counting the update a success.</summary>
    private const long DefaultMonitor = 5_000_000_000;

    /// <summary>
    /// The spec for a new service, or (with <paramref name="existing"/>, a service's <c>Spec</c>
    /// JSON) that spec with the form applied. What the form doesn't show is kept as it was.
    /// </summary>
    public static (JsonObject? Spec, IReadOnlyList<SpecError> Errors) Build(ServiceForm form, ServiceReferences references, JsonObject? existing = null)
    {
        var errors = new List<SpecError>();
        var spec = existing?.DeepClone().AsObject() ?? new JsonObject();
        var task = Child(spec, "TaskTemplate");
        var container = Child(task, "ContainerSpec");

        // ---- name, image, mode
        if (existing is null)
        {
            var name = form.Name?.Trim() ?? "";
            if (name.Length == 0)
                errors.Add(new(nameof(form.Name), "Enter a name, e.g. web."));
            else if (!ServiceName().IsMatch(name))
                errors.Add(new(nameof(form.Name), "Use letters, digits, '_', '.' or '-' (at most 63), starting with a letter or digit."));
            spec["Name"] = name;
        }

        var image = form.Image?.Trim() ?? "";
        if (image.Length == 0)
            errors.Add(new(nameof(form.Image), "Enter an image, e.g. nginx:1.27."));
        else if (image.Any(char.IsWhiteSpace))
            errors.Add(new(nameof(form.Image), "An image reference has no spaces."));
        else if (container["Image"]?.GetValue<string>() is not { } current || ServiceSpec.ImageWithoutDigest(current) != image)
            container["Image"] = image; // Unchanged, it keeps the digest it's pinned to.

        var mode = form.Mode?.Trim().ToLowerInvariant() ?? "";
        var existingMode = existing is null ? null : ModeOf(existing);
        if (existingMode is "job")
            errors.Add(new(nameof(form.Mode), "Jobs can't be edited here. Use docker service update."));
        else if (!Modes.Contains(mode))
            errors.Add(new(nameof(form.Mode), "Choose replicated or global."));
        else if (existingMode is not null && existingMode != mode)
            errors.Add(new(nameof(form.Mode), $"A service's mode can't change. It stays {existingMode}; create a new service to run it {mode}."));
        else if (mode == "replicated")
        {
            if (form.Replicas is not { } replicas || replicas < 0 || replicas > 10_000)
                errors.Add(new(nameof(form.Replicas), "Enter 0 to 10,000 replicas."));
            else
                Child(Child(spec, "Mode"), "Replicated")["Replicas"] = replicas;
        }
        else if (existing is null)
            spec["Mode"] = new JsonObject { ["Global"] = new JsonObject() };

        // ---- container
        SetList(container, "Args", Collect(errors, nameof(form.Command), () => ContainerSpecBuilder.ShellWords(form.Command)));
        SetList(container, "Env", Collect(errors, nameof(form.Environment), () => ContainerSpecBuilder.ParseEnvironment(form.Environment)));
        if (Collect(errors, nameof(form.Labels), () => ContainerSpecBuilder.ParseLabels(form.Labels)) is { } labels)
            spec["Labels"] = Object(labels);

        if (Collect(errors, nameof(form.Mounts), () => ParseMounts(form.Mounts)) is { } mounts)
            container["Mounts"] = MergeMounts(container["Mounts"] as JsonArray, mounts);
        if (container["Mounts"] is JsonArray { Count: 0 })
            container.Remove("Mounts");

        // ---- endpoint
        if (Collect(errors, nameof(form.Ports), () => ParsePorts(form.Ports)) is { } ports)
        {
            var endpoint = Child(spec, "EndpointSpec");
            endpoint["Ports"] = MergePorts(endpoint["Ports"] as JsonArray, ports);
            if (endpoint["Ports"] is JsonArray { Count: 0 })
                endpoint.Remove("Ports");
            if (endpoint.Count == 0)
                spec.Remove("EndpointSpec");
        }

        // ---- references
        task["Networks"] = Attach(errors, nameof(form.Networks), form.Networks, references.Networks, task["Networks"] as JsonArray, "network",
            (name, id) => new JsonObject { ["Target"] = id },
            entry => entry["Target"]?.GetValue<string>(), useIdForMatch: true);
        container["Secrets"] = Attach(errors, nameof(form.Secrets), form.Secrets, references.Secrets, container["Secrets"] as JsonArray, "secret",
            (name, id) => new JsonObject { ["File"] = File(name), ["SecretID"] = id, ["SecretName"] = name },
            entry => entry["SecretName"]?.GetValue<string>());
        container["Configs"] = Attach(errors, nameof(form.Configs), form.Configs, references.Configs, container["Configs"] as JsonArray, "config",
            (name, id) => new JsonObject { ["File"] = File("/" + name), ["ConfigID"] = id, ["ConfigName"] = name },
            entry => entry["ConfigName"]?.GetValue<string>());
        foreach (var (parent, key) in (ReadOnlySpan<(JsonObject, string)>)[(task, "Networks"), (container, "Secrets"), (container, "Configs")])
        {
            if (parent[key] is null or JsonArray { Count: 0 })
                parent.Remove(key);
        }

        // ---- placement
        var placement = Child(task, "Placement");
        SetList(placement, "Constraints", Collect(errors, nameof(form.Constraints), () => ParseConstraints(form.Constraints)));
        if (Collect(errors, nameof(form.Preferences), () => ParsePreferences(form.Preferences)) is { } preferences)
        {
            placement["Preferences"] = new JsonArray([.. preferences.Select(p => (JsonNode)new JsonObject { ["Spread"] = new JsonObject { ["SpreadDescriptor"] = p } })]);
            if (preferences.Count == 0)
                placement.Remove("Preferences");
        }
        if (placement.Count == 0)
            task.Remove("Placement");

        // ---- resources
        var resources = Child(task, "Resources");
        SetResources(errors, Child(resources, "Limits"), form.CpuLimit, nameof(form.CpuLimit), form.MemoryLimit, nameof(form.MemoryLimit), minimumMemory: 6 * 1024 * 1024);
        SetResources(errors, Child(resources, "Reservations"), form.CpuReservation, nameof(form.CpuReservation), form.MemoryReservation, nameof(form.MemoryReservation), minimumMemory: 0);
        foreach (var key in (string[])["Limits", "Reservations"])
        {
            if (resources[key] is JsonObject { Count: 0 })
                resources.Remove(key);
        }
        if (resources.Count == 0)
            task.Remove("Resources");

        // ---- update and rollback
        SetUpdateConfig(errors, spec, "UpdateConfig", editing: existing is not null, form.UpdateParallelism, form.UpdateDelay, form.UpdateFailureAction, form.UpdateOrder,
            UpdateFailureActions, nameof(form.UpdateParallelism), nameof(form.UpdateDelay), nameof(form.UpdateFailureAction), nameof(form.UpdateOrder));
        SetUpdateConfig(errors, spec, "RollbackConfig", editing: existing is not null, form.RollbackParallelism, form.RollbackDelay, form.RollbackFailureAction, form.RollbackOrder,
            RollbackFailureActions, nameof(form.RollbackParallelism), nameof(form.RollbackDelay), nameof(form.RollbackFailureAction), nameof(form.RollbackOrder));

        return errors.Count > 0 ? (null, errors) : (spec, errors);
    }

    /// <summary>The form for a service's spec, as <see cref="Build"/> reads it back.</summary>
    public static ServiceForm FromSpec(ServiceSpec spec, ServiceReferences references)
    {
        var task = spec.TaskTemplate;
        var container = task?.ContainerSpec;
        var networkNames = references.Networks.ToDictionary(n => n.Value, n => n.Key, StringComparer.Ordinal);
        var update = spec.UpdateConfig;
        var rollback = spec.RollbackConfig;

        return new ServiceForm
        {
            Name = spec.Name,
            Image = ServiceSpec.ImageWithoutDigest(container?.Image) ?? "",
            Mode = spec.Mode?.Global is not null ? "global" : "replicated",
            Replicas = spec.Mode?.Replicated is { } replicated ? (int)Math.Min(replicated.Replicas, int.MaxValue) : null,
            Command = container?.Args is { Count: > 0 } args ? ContainerSpecBuilder.JoinShellWords(args) : null,
            Environment = Lines(container?.Env),
            Labels = Lines(spec.Labels?.Select(l => l.Value.Length == 0 ? l.Key : $"{l.Key}={l.Value}")),
            Ports = Lines(spec.EndpointSpec?.Ports?.Select(FormatPort)),
            Mounts = Lines(container?.Mounts?.Select(FormatMount).OfType<string>()),
            Networks = [.. (task?.Networks ?? []).Select(n => n.Target is { } target ? networkNames.GetValueOrDefault(target, target) : null).OfType<string>()],
            Secrets = [.. (container?.Secrets ?? []).Select(s => s.SecretName).OfType<string>()],
            Configs = [.. (container?.Configs ?? []).Select(c => c.ConfigName).OfType<string>()],
            Constraints = Lines(task?.Placement?.Constraints),
            Preferences = Lines(task?.Placement?.Preferences?.Select(p => p.Spread?.SpreadDescriptor).OfType<string>().Select(d => $"spread={d}")),
            CpuLimit = Cpus(task?.Resources?.Limits?.NanoCpus),
            MemoryLimit = Bytes(task?.Resources?.Limits?.MemoryBytes),
            CpuReservation = Cpus(task?.Resources?.Reservations?.NanoCpus),
            MemoryReservation = Bytes(task?.Resources?.Reservations?.MemoryBytes),
            UpdateParallelism = update is null ? 1 : (int)Math.Min(update.Parallelism, int.MaxValue),
            UpdateDelay = SwarmUnits.FormatDuration(update?.Delay ?? 0),
            UpdateFailureAction = update?.FailureAction is { Length: > 0 } ufa ? ufa : "pause",
            UpdateOrder = update?.Order is { Length: > 0 } uo ? uo : "stop-first",
            RollbackParallelism = rollback is null ? 1 : (int)Math.Min(rollback.Parallelism, int.MaxValue),
            RollbackDelay = SwarmUnits.FormatDuration(rollback?.Delay ?? 0),
            RollbackFailureAction = rollback?.FailureAction is { Length: > 0 } rfa ? rfa : "pause",
            RollbackOrder = rollback?.Order is { Length: > 0 } ro ? ro : "stop-first",
        };

        static string? Cpus(long? nano) => nano is > 0 ? SwarmUnits.FormatCpus(nano.Value) : null;
        static string? Bytes(long? bytes) => bytes is > 0 ? SwarmUnits.FormatBytes(bytes.Value) : null;
    }

    /// <summary><c>replicated</c>, <c>global</c> or <c>job</c> for a spec's JSON.</summary>
    public static string ModeOf(JsonObject spec) =>
        spec["Mode"] is JsonObject mode
            ? mode.ContainsKey("Global") ? "global" : mode.ContainsKey("ReplicatedJob") || mode.ContainsKey("GlobalJob") ? "job" : "replicated"
            : "replicated";

    /// <summary>Scales a replicated service's spec JSON.</summary>
    public static JsonObject Scale(JsonObject spec, ulong replicas)
    {
        var scaled = spec.DeepClone().AsObject();
        Child(Child(scaled, "Mode"), "Replicated")["Replicas"] = replicas;
        return scaled;
    }

    /// <summary>The spec with <c>ForceUpdate</c> bumped: every task is replaced, nothing else changes.</summary>
    public static JsonObject ForceUpdate(JsonObject spec)
    {
        var forced = spec.DeepClone().AsObject();
        var task = Child(forced, "TaskTemplate");
        var current = task["ForceUpdate"] is JsonValue value && value.TryGetValue<ulong>(out var n) ? n : 0;
        task["ForceUpdate"] = current + 1;
        return forced;
    }

    // ---------------------------------------------------------------- parsers

    public sealed record MountLine(string Type, string? Source, string Target, bool ReadOnly);

    public static List<MountLine> ParseMounts(string? text)
    {
        List<MountLine> mounts = [];
        foreach (var (number, line) in Lines(text))
        {
            var parts = line.Split(':');
            var readOnly = false;
            if (parts.Length >= 2 && parts[^1] is "ro" or "rw")
            {
                readOnly = parts[^1] == "ro";
                parts = parts[..^1];
            }
            var (source, target) = parts.Length switch
            {
                1 => ((string?)null, parts[0]),
                2 => (parts[0], parts[1]),
                _ => throw new ContainerSpecBuilder.SpecFormatException($"Line {number}: use source:/target or source:/target:ro."),
            };
            if (!target.StartsWith('/'))
                throw new ContainerSpecBuilder.SpecFormatException($"Line {number}: the path inside the container must be absolute, e.g. /data.");
            if (source is { Length: 0 })
                throw new ContainerSpecBuilder.SpecFormatException($"Line {number}: the source is a volume name or an absolute host path.");
            if (source is not null && !source.StartsWith('/') && !VolumeName().IsMatch(source))
                throw new ContainerSpecBuilder.SpecFormatException($"Line {number}: '{source}' is neither an absolute host path nor a valid volume name.");
            if (mounts.Any(m => m.Target == target))
                throw new ContainerSpecBuilder.SpecFormatException($"Line {number}: {target} is mounted twice.");
            mounts.Add(new MountLine(source?.StartsWith('/') == true ? "bind" : "volume", source, target, readOnly));
        }
        return mounts;
    }

    public sealed record PortLine(int Published, int Target, string Protocol, string Mode);

    public static List<PortLine> ParsePorts(string? text)
    {
        List<PortLine> ports = [];
        foreach (var (number, line) in Lines(text))
        {
            var words = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var mode = "ingress";
            if (words.Length == 2 && words[1].ToLowerInvariant() is "host" or "ingress" or "mode=host" or "mode=ingress")
                mode = words[1].ToLowerInvariant().Replace("mode=", "", StringComparison.Ordinal);
            else if (words.Length != 1)
                throw new ContainerSpecBuilder.SpecFormatException($"Line {number}: use published:target, e.g. 8080:80, optionally followed by host.");

            var spec = words[0];
            var (numbers, protocol) = spec.LastIndexOf('/') is var slash and > 0 ? (spec[..slash], spec[(slash + 1)..].ToLowerInvariant()) : (spec, "tcp");
            if (protocol is not ("tcp" or "udp" or "sctp"))
                throw new ContainerSpecBuilder.SpecFormatException($"Line {number}: the protocol is tcp, udp or sctp.");
            var parts = numbers.Split(':');
            var (published, target) = parts.Length switch
            {
                1 => ("", parts[0]),
                2 => (parts[0], parts[1]),
                _ => throw new ContainerSpecBuilder.SpecFormatException($"Line {number}: use published:target. Swarm publishes on every address; there's no host IP."),
            };
            if (!TryPort(target, out var targetPort))
                throw new ContainerSpecBuilder.SpecFormatException($"Line {number}: '{target}' is not a port (1–65535).");
            var publishedPort = 0;
            if (published.Length > 0 && !TryPort(published, out publishedPort))
                throw new ContainerSpecBuilder.SpecFormatException($"Line {number}: '{published}' is not a port (1–65535).");
            if (publishedPort > 0 && ports.Any(p => p.Published == publishedPort && p.Protocol == protocol))
                throw new ContainerSpecBuilder.SpecFormatException($"Line {number}: port {publishedPort}/{protocol} is published twice.");
            ports.Add(new PortLine(publishedPort, targetPort, protocol, mode));
        }
        return ports;
    }

    public static List<string> ParseConstraints(string? text)
    {
        List<string> constraints = [];
        foreach (var (number, line) in Lines(text))
        {
            var compact = line.Replace(" ", "", StringComparison.Ordinal);
            if (!Constraint().IsMatch(compact))
                throw new ContainerSpecBuilder.SpecFormatException($"Line {number}: use attribute==value or attribute!=value, e.g. node.role==manager or node.labels.zone!=a.");
            constraints.Add(compact);
        }
        return constraints;
    }

    public static List<string> ParsePreferences(string? text)
    {
        List<string> preferences = [];
        foreach (var (number, line) in Lines(text))
        {
            var descriptor = line.StartsWith("spread=", StringComparison.Ordinal) ? line["spread=".Length..].Trim() : line;
            if (!Descriptor().IsMatch(descriptor))
                throw new ContainerSpecBuilder.SpecFormatException($"Line {number}: use spread=node.labels.name, e.g. spread=node.labels.zone.");
            preferences.Add(descriptor);
        }
        return preferences;
    }

    // ---------------------------------------------------------------- formatting

    public static string FormatPort(ServicePort port)
    {
        var text = port.PublishedPort > 0
            ? $"{port.PublishedPort.ToString(CultureInfo.InvariantCulture)}:{port.TargetPort.ToString(CultureInfo.InvariantCulture)}"
            : port.TargetPort.ToString(CultureInfo.InvariantCulture);
        if (port.Protocol is { Length: > 0 } protocol && protocol != "tcp")
            text += "/" + protocol;
        return port.PublishMode == "host" ? text + " host" : text;
    }

    /// <summary>A mount as a form line; null for kinds the form doesn't edit (they're kept as they are).</summary>
    public static string? FormatMount(ServiceMount mount)
    {
        if (mount.Target is null || mount.Type is not ("volume" or "bind"))
            return null;
        var text = mount.Source is { Length: > 0 } source ? $"{source}:{mount.Target}" : mount.Target;
        return mount.ReadOnly ? text + ":ro" : text;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The mounts in their old order: kinds the form doesn't show (tmpfs, npipe, cluster) as they
    /// were, the form's ones updated in place (keeping their options) or dropped, then new ones.
    /// </summary>
    private static JsonArray MergeMounts(JsonArray? existing, List<MountLine> mounts)
    {
        var result = new JsonArray();
        var placed = new HashSet<MountLine>();
        foreach (var old in (existing ?? []).OfType<JsonObject>())
        {
            var type = old["Type"]?.GetValue<string>();
            if (type is not ("volume" or "bind"))
            {
                result.Add(old.DeepClone());
                continue;
            }
            var target = old["Target"]?.GetValue<string>();
            if (mounts.FirstOrDefault(m => m.Target == target && m.Type == type) is { } kept)
            {
                result.Add(Apply(old.DeepClone().AsObject(), kept));
                placed.Add(kept);
            }
        }
        foreach (var mount in mounts.Where(m => !placed.Contains(m)))
            result.Add(Apply(new JsonObject(), mount));
        return result;

        static JsonObject Apply(JsonObject entry, MountLine mount)
        {
            entry["Type"] = mount.Type;
            if (mount.Source is null)
                entry.Remove("Source");
            else
                entry["Source"] = mount.Source;
            entry["Target"] = mount.Target;
            if (mount.ReadOnly)
                entry["ReadOnly"] = true;
            else
                entry.Remove("ReadOnly");
            return entry;
        }
    }

    private static JsonArray MergePorts(JsonArray? existing, List<PortLine> ports)
    {
        var previous = (existing ?? []).OfType<JsonObject>().ToList();
        var result = new JsonArray();
        foreach (var port in ports)
        {
            var entry = previous.FirstOrDefault(p => p["TargetPort"]?.GetValue<int>() == port.Target
                                                     && (p["Protocol"]?.GetValue<string>() ?? "tcp") == port.Protocol
                                                     && (p["PublishedPort"]?.GetValue<int>() ?? 0) == port.Published)?.DeepClone().AsObject()
                        ?? new JsonObject();
            entry["Protocol"] = port.Protocol;
            entry["TargetPort"] = port.Target;
            if (port.Published > 0)
                entry["PublishedPort"] = port.Published;
            else
                entry.Remove("PublishedPort");
            entry["PublishMode"] = port.Mode;
            result.Add(entry);
        }
        return result;
    }

    /// <summary>
    /// The entries for the chosen names: existing ones kept as they were (aliases, custom file
    /// targets), new ones from <paramref name="create"/>.
    /// </summary>
    private static JsonArray Attach(List<SpecError> errors, string field, IEnumerable<string>? chosen, IReadOnlyDictionary<string, string> known,
        JsonArray? existing, string noun, Func<string, string, JsonObject> create, Func<JsonObject, string?> key, bool useIdForMatch = false)
    {
        var result = new JsonArray();
        var previous = (existing ?? []).OfType<JsonObject>().ToList();
        foreach (var name in (chosen ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal))
        {
            // An entry the service already has may name something that's gone from the list (or a network by name).
            var match = previous.FirstOrDefault(p => key(p) == name || (useIdForMatch && known.TryGetValue(name, out var id) && key(p) == id));
            if (match is not null)
            {
                result.Add(match.DeepClone());
                continue;
            }
            if (!known.TryGetValue(name, out var newId))
            {
                errors.Add(new(field, $"There's no {noun} named {name} on this swarm."));
                continue;
            }
            result.Add(create(name, newId));
        }
        return result;
    }

    private static JsonObject File(string name) => new() { ["Name"] = name, ["UID"] = "0", ["GID"] = "0", ["Mode"] = ReadOnlyFileMode };

    private static void SetResources(List<SpecError> errors, JsonObject target, string? cpus, string cpuField, string? memory, string memoryField, long minimumMemory)
    {
        if (string.IsNullOrWhiteSpace(cpus))
            target.Remove("NanoCPUs");
        else if (SwarmUnits.TryParseCpus(cpus, out var nano))
            target["NanoCPUs"] = nano;
        else
            errors.Add(new(cpuField, "Enter a number of CPUs above 0, e.g. 0.5."));

        if (string.IsNullOrWhiteSpace(memory))
            target.Remove("MemoryBytes");
        else if (SwarmUnits.TryParseBytes(memory, out var bytes) && bytes > 0)
        {
            if (bytes < minimumMemory)
                errors.Add(new(memoryField, "The engine needs at least 6M."));
            else
                target["MemoryBytes"] = bytes;
        }
        else
            errors.Add(new(memoryField, "Enter a size such as 512M or 1.5G."));
    }

    /// <param name="editing">
    /// Changing an existing service: one without this config keeps having none while the form
    /// shows swarm's own defaults (1 at a time, no delay, pause, stop-first).
    /// </param>
    private static void SetUpdateConfig(List<SpecError> errors, JsonObject spec, string key, bool editing, int? parallelism, string? delay, string? failureAction, string? order,
        IReadOnlyList<string> failureActions, string parallelismField, string delayField, string failureField, string orderField)
    {
        var count = errors.Count;
        if (parallelism is not { } p || p < 0)
            errors.Add(new(parallelismField, "Enter 0 (all at once) or more."));

        long nanos = 0;
        if (!string.IsNullOrWhiteSpace(delay) && !SwarmUnits.TryParseDuration(delay, out nanos))
            errors.Add(new(delayField, "Enter a duration such as 10s, 1m30s or 500ms."));

        if (failureAction is null || !failureActions.Contains(failureAction))
            errors.Add(new(failureField, $"Choose {string.Join(", ", failureActions)}."));

        if (order is null || !Orders.Contains(order))
            errors.Add(new(orderField, "Choose stop-first or start-first."));

        if (errors.Count > count)
            return;
        var fresh = spec[key] is not JsonObject;
        if (fresh && editing && parallelism == 1 && nanos == 0 && failureAction == "pause" && order == "stop-first")
            return;

        var config = Child(spec, key);
        config["Parallelism"] = parallelism;
        config["Delay"] = nanos;
        config["FailureAction"] = failureAction;
        config["Order"] = order;
        if (fresh)
        {
            config["Monitor"] = DefaultMonitor;
            config["MaxFailureRatio"] = 0;
        }
    }

    private static JsonObject Child(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject child)
            return child;
        child = [];
        parent[key] = child;
        return child;
    }

    private static void SetList(JsonObject parent, string key, IReadOnlyCollection<string>? values)
    {
        if (values is null)
            return;
        if (values.Count == 0)
            parent.Remove(key);
        else
            parent[key] = new JsonArray([.. values.Select(v => (JsonNode)v)]);
    }

    private static JsonObject Object(Dictionary<string, string> values)
    {
        var json = new JsonObject();
        foreach (var (key, value) in values)
            json[key] = value;
        return json;
    }

    private static T? Collect<T>(List<SpecError> errors, string field, Func<T> parse) where T : class
    {
        try
        {
            return parse();
        }
        catch (ContainerSpecBuilder.SpecFormatException ex)
        {
            errors.Add(new(field, ex.Message));
            return null;
        }
    }

    private static IEnumerable<(int Number, string Text)> Lines(string? text) =>
        (text ?? "").Split('\n')
            .Select((line, index) => (Number: index + 1, Text: line.Trim()))
            .Where(l => l.Text.Length > 0 && !l.Text.StartsWith('#'));

    private static string? Lines(IEnumerable<string>? values) =>
        values?.ToList() is { Count: > 0 } list ? string.Join('\n', list) : null;

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,62}$")]
    private static partial Regex ServiceName();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]+$")]
    private static partial Regex VolumeName();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_.\-]*(==|!=)[^=!\s]+$")]
    private static partial Regex Constraint();

    [GeneratedRegex(@"^(node|engine)\.labels\.[a-zA-Z0-9_.\-]+$")]
    private static partial Regex Descriptor();
}
