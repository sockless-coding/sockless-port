using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sockless.Port.Stacks;

namespace Sockless.Port.Templates;

/// <summary>What the user chose for a template: variable values, and which ports and volumes to use.</summary>
/// <param name="Variables">By variable name. Preset variables keep the template's value whatever this says.</param>
/// <param name="Ports">One per <see cref="AppTemplate.Ports"/>, in order.</param>
/// <param name="Volumes">One per <see cref="AppTemplate.Volumes"/>, in order.</param>
public sealed record TemplateChoices(IReadOnlyDictionary<string, string> Variables, IReadOnlyList<PortChoice> Ports, IReadOnlyList<VolumeChoice> Volumes)
{
    /// <summary>What the form starts with.</summary>
    public static TemplateChoices Defaults(AppTemplate template) => new(
        template.Variables.ToDictionary(v => v.Name, v => v.Optional ? "" : v.Default, StringComparer.Ordinal),
        // A host address the template names shows in the field, so it can be changed too.
        template.Ports.Select(p => new PortChoice(!p.Optional, p.HostIp is { } ip && p.Host is { } host ? $"{ip}:{host}" : p.Host)).ToList(),
        TemplateCompose.DefaultSources(template).Select((source, i) => new VolumeChoice(!template.Volumes[i].Optional, source)).ToList());
}

/// <param name="Host">The host port (optionally <c>address:port</c>); empty lets the engine pick one.</param>
public sealed record PortChoice(bool Publish, string? Host);

/// <param name="Source">A volume name, or a host path (starting with <c>/</c>); empty for an anonymous volume.</param>
public sealed record VolumeChoice(bool Include, string? Source);

/// <summary>A problem with what the user chose, for the form field it belongs to.</summary>
/// <param name="Field">The field's name in <see cref="TemplateChoices"/>: <c>Variables[NAME]</c>, <c>Ports[0]</c>, <c>Volumes[1]</c>.</param>
public sealed record TemplateChoiceError(string Field, string Message);

/// <summary>
/// Writes a stack's files from a template. A container template becomes a one-service compose
/// file; a stack template's file is used as fetched. Either way the variables go into <c>.env</c>
/// (single-quoted, so Compose takes them literally) and every literal value in the compose file
/// has its <c>$</c> doubled, so nothing from the feed is interpolated by accident.
/// </summary>
public static partial class TemplateCompose
{
    /// <summary>Where each volume starts: the template's host path when it names a real one, else a volume named after the container path.</summary>
    public static IReadOnlyList<string> DefaultSources(AppTemplate template)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        List<string> sources = [];
        foreach (var volume in template.Volumes)
        {
            if (volume.Bind is { } bind && IsRealHostPath(bind))
            {
                sources.Add(bind);
                continue;
            }
            var name = VolumeName(volume.Container);
            for (var n = 2; !used.Add(name); n++)
                name = $"{VolumeName(volume.Container)}-{n}";
            sources.Add(name);
        }
        return sources;
    }

    /// <summary>Checks the choices against the template; nothing is written when there are errors.</summary>
    public static IReadOnlyList<TemplateChoiceError> Problems(AppTemplate template, TemplateChoices choices)
    {
        List<TemplateChoiceError> errors = [];
        foreach (var variable in template.Variables.Where(v => !v.Preset))
        {
            var value = Value(variable, choices);
            if (value.Contains('\'') && value.Contains('$'))
                errors.Add(new($"Variables[{variable.Name}]", "Use either ' or $ in this value, not both: the .env file can't hold that exactly."));
            else if (value.Contains('\n') || value.Contains('\r'))
                errors.Add(new($"Variables[{variable.Name}]", "Use one line."));
            else if (variable.Choices.Count > 0 && value.Length > 0 && !variable.Choices.Any(c => c.Value == value))
                errors.Add(new($"Variables[{variable.Name}]", "Choose one of the listed values."));
        }
        if (template.Kind != TemplateKind.Container)
            return errors;

        for (var i = 0; i < template.Ports.Count; i++)
        {
            var choice = i < choices.Ports.Count ? choices.Ports[i] : new PortChoice(false, null);
            if (choice.Publish && choice.Host is { Length: > 0 } host && HostPort(host) is null)
                errors.Add(new($"Ports[{i}]", "Enter a port (8080), a range (8000-8010), or an address and port (127.0.0.1:8080)."));
        }
        for (var i = 0; i < template.Volumes.Count; i++)
        {
            var choice = i < choices.Volumes.Count ? choices.Volumes[i] : new VolumeChoice(false, null);
            var source = choice.Source?.Trim() ?? "";
            if (!choice.Include || source.Length == 0 || IsPath(source))
                continue;
            if (!VolumeNamePattern().IsMatch(source))
                errors.Add(new($"Volumes[{i}]", "Use a volume name (letters, digits, _ . -) or a host path starting with /."));
        }
        return errors;
    }

    /// <summary>The files for a container template. Call <see cref="Problems"/> first.</summary>
    public static StackContent ForContainer(AppTemplate template, TemplateChoices choices, string sourceName)
    {
        var service = template.ServiceName ?? template.Key;
        var yaml = new StringBuilder();
        yaml.Append("# From the template \"").Append(Comment(template.Title)).Append("\" (").Append(Comment(sourceName)).Append(").\n");
        yaml.Append("services:\n");
        yaml.Append("  ").Append(service).Append(":\n");
        yaml.Append("    image: ").Append(Scalar(Literal(template.Image ?? ""))).Append('\n');
        if (template.Command is { } command)
            yaml.Append("    command: ").Append(Scalar(Literal(command))).Append('\n');
        yaml.Append("    restart: ").Append(template.RestartPolicy ?? "unless-stopped").Append('\n');
        if (template.Hostname is { } hostname)
            yaml.Append("    hostname: ").Append(Scalar(Literal(hostname))).Append('\n');
        if (template.Privileged)
            yaml.Append("    privileged: true\n");
        if (template.Interactive)
            yaml.Append("    stdin_open: true\n    tty: true\n");

        var hostNetwork = string.Equals(template.Network, "host", StringComparison.OrdinalIgnoreCase);
        if (template.Network is { } network && !string.Equals(network, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            // Other networks would have to exist already; host and none are modes.
            if (network.ToLowerInvariant() is "host" or "none")
                yaml.Append("    network_mode: ").Append(network.ToLowerInvariant()).Append('\n');
        }

        var ports = template.Ports
            .Select((port, i) => (port, choice: i < choices.Ports.Count ? choices.Ports[i] : new PortChoice(false, null)))
            .Where(p => p.choice.Publish)
            .Select(p => p.choice.Host is { Length: > 0 } host ? $"{HostPort(host)}:{p.port.ContainerSpec}"
                : p.port.HostIp is { } ip ? $"{ip}::{p.port.ContainerSpec}" : p.port.ContainerSpec)
            .ToList();
        // With the host's network, the container's ports are the host's already.
        if (ports.Count > 0 && !hostNetwork)
            List(yaml, "ports", ports.Select(p => Quoted(p)));

        List<string> namedVolumes = [];
        List<string> mounts = [];
        for (var i = 0; i < template.Volumes.Count; i++)
        {
            var volume = template.Volumes[i];
            var choice = i < choices.Volumes.Count ? choices.Volumes[i] : new VolumeChoice(false, null);
            if (!choice.Include)
                continue;
            var source = choice.Source?.Trim() ?? "";
            var target = Literal(volume.Container) + (volume.ReadOnly ? ":ro" : "");
            if (source.Length == 0)
                mounts.Add(Quoted(target));
            else
            {
                mounts.Add(Quoted($"{Literal(source)}:{target}"));
                if (!IsPath(source) && !namedVolumes.Contains(source))
                    namedVolumes.Add(source);
            }
        }
        if (mounts.Count > 0)
            List(yaml, "volumes", mounts);
        if (template.Devices.Count > 0)
            List(yaml, "devices", template.Devices.Select(d => Quoted(Literal(d))));
        if (template.CapAdd.Count > 0)
            List(yaml, "cap_add", template.CapAdd.Select(c => Scalar(Literal(c))));
        if (template.SecurityOpt.Count > 0)
            List(yaml, "security_opt", template.SecurityOpt.Select(s => Quoted(Literal(s))));
        foreach (var setting in template.Settings)
        {
            if (setting.IsList)
                List(yaml, setting.Key, setting.Values.Select(v => Quoted(Literal(v))));
            else
                yaml.Append("    ").Append(setting.Key).Append(": ").Append(Scalar(Literal(setting.Values.FirstOrDefault() ?? ""))).Append('\n');
        }

        var env = new StringBuilder();
        var environment = new List<string>();
        foreach (var variable in template.Variables)
        {
            var value = variable.Preset ? variable.Default : Value(variable, choices);
            // An empty value would set the variable to "" in the container; leave it unset instead.
            if (value.Length == 0)
                continue;
            if (!EnvName().IsMatch(variable.Name) || StackEnv.IsReserved(variable.Name))
            {
                environment.Add($"      {Key(variable.Name)}: {Quoted(Literal(value))}");
                continue;
            }
            environment.Add($"      {variable.Name}: ${{{variable.Name}}}");
            AppendEnv(env, variable, value);
        }
        if (environment.Count > 0)
        {
            yaml.Append("    environment:\n");
            foreach (var line in environment)
                yaml.Append(line).Append('\n');
        }

        if (template.Labels.Count > 0)
        {
            yaml.Append("    labels:\n");
            foreach (var (name, value) in template.Labels)
                yaml.Append("      ").Append(Key(name)).Append(": ").Append(Quoted(Literal(value))).Append('\n');
        }

        if (namedVolumes.Count > 0)
        {
            yaml.Append("volumes:\n");
            foreach (var name in namedVolumes)
                yaml.Append("  ").Append(Key(name)).Append(":\n");
        }
        return StackContent.Normalize(yaml.ToString(), env.ToString());
    }

    /// <summary>The files for a stack template: its compose file as fetched, and the variables as <c>.env</c>.</summary>
    /// <param name="compose">The fetched file.</param>
    public static StackContent ForStack(AppTemplate template, TemplateChoices choices, string compose)
    {
        // Portainer passes the variables in a file named stack.env; here they're in .env.
        var (rewritten, _) = PortainerStacks.RewriteEnvFile(compose);
        var env = new StringBuilder();
        foreach (var variable in template.Variables)
        {
            var value = variable.Preset ? variable.Default : Value(variable, choices);
            if (!EnvName().IsMatch(variable.Name) || (variable.Optional && value.Length == 0))
                continue;
            // Written even when empty, so Compose doesn't warn that it isn't set.
            AppendEnv(env, variable, value);
        }
        return StackContent.Normalize(rewritten, env.ToString());
    }

    /// <summary>A host path, rather than a volume name: what Compose treats as a bind mount.</summary>
    public static bool IsPath(string source) => source.StartsWith('/') || source.StartsWith('.') || source.StartsWith('~');

    private static string Value(TemplateVariable variable, TemplateChoices choices) =>
        (choices.Variables.TryGetValue(variable.Name, out var value) ? value : variable.Optional ? "" : variable.Default)?.Trim() ?? "";

    private static void AppendEnv(StringBuilder env, TemplateVariable variable, string value)
    {
        if (variable.Label != variable.Name || variable.Description is not null)
            env.Append("# ").Append(Comment(variable.Label)).Append('\n');
        env.Append(variable.Name).Append('=');
        // Single quotes are literal; a value with one in it (and no $, see Problems) goes in double quotes.
        if (!value.Contains('\''))
            env.Append('\'').Append(value).Append('\'');
        else
            env.Append('"').Append(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
        env.Append('\n');
    }

    /// <summary>A real host path, not linuxserver.io's <c>/path/to/…</c> placeholders.</summary>
    private static bool IsRealHostPath(string bind) =>
        bind.StartsWith('/') && !bind.StartsWith("/path/to", StringComparison.OrdinalIgnoreCase) && !bind.Contains('<') && !bind.Contains(' ');

    /// <summary><c>config</c> for <c>/config</c>, <c>registry</c> for <c>/var/lib/registry</c>.</summary>
    private static string VolumeName(string containerPath)
    {
        var last = containerPath.TrimEnd('/').Split('/').LastOrDefault(s => s.Length > 0) ?? "data";
        var name = TemplateFeed.Slug(last);
        return VolumeNamePattern().IsMatch(name) ? name : "data";
    }

    /// <summary>A host port, a range, or an address and port; null if it's neither.</summary>
    private static string? HostPort(string host)
    {
        host = host.Trim();
        var colon = host.LastIndexOf(':');
        if (colon < 0)
            return TemplateFeed.PortRange().IsMatch(host) && PortsInRange(host) ? host : null;
        var address = host[..colon].Trim('[', ']');
        var port = host[(colon + 1)..];
        if (!System.Net.IPAddress.TryParse(address, out var ip) || !TemplateFeed.PortRange().IsMatch(port) || !PortsInRange(port))
            return null;
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
    }

    private static bool PortsInRange(string range) =>
        range.Split('-').All(p => int.TryParse(p, out var n) && n is >= 1 and <= 65535);

    private static void List(StringBuilder yaml, string key, IEnumerable<string> items)
    {
        yaml.Append("    ").Append(key).Append(":\n");
        foreach (var item in items)
            yaml.Append("      - ").Append(item).Append('\n');
    }

    /// <summary>Doubles <c>$</c>, so Compose doesn't interpolate a value from the feed.</summary>
    private static string Literal(string value) => value.Replace("$", "$$", StringComparison.Ordinal);

    /// <summary>A map key: plain when it's a simple name, quoted otherwise.</summary>
    private static string Key(string key) => PlainKey().IsMatch(key) ? key : Quoted(key);

    /// <summary>Plain when YAML would read it back as the same string, quoted otherwise.</summary>
    private static string Scalar(string value) =>
        PlainScalar().IsMatch(value) && !Ambiguous().IsMatch(value) ? value : Quoted(value);

    /// <summary>A YAML double-quoted string; JSON's escapes are valid in it.</summary>
    private static string Quoted(string value) => JsonSerializer.Serialize(value, QuoteOptions);

    private static readonly JsonSerializerOptions QuoteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Comment(string text) => text.Replace('\n', ' ').Replace('\r', ' ');

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvName();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex VolumeNamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]*$")]
    private static partial Regex PlainKey();

    [GeneratedRegex("^[A-Za-z0-9/_][A-Za-z0-9_./@:+-]*$")]
    private static partial Regex PlainScalar();

    /// <summary>Plain scalars YAML reads as something else: booleans, null, numbers, or ending in a colon.</summary>
    [GeneratedRegex("^(?:true|false|yes|no|on|off|y|n|null|[-+0-9.:eE_x]+|.*:)$", RegexOptions.IgnoreCase)]
    private static partial Regex Ambiguous();
}
