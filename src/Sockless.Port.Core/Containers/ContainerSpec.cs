using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sockless.Port.Containers;

/// <summary>
/// What the create form collects, in the same text formats as the <c>docker run</c> flags
/// (<c>-p 8080:80</c>, <c>-v data:/data:ro</c>, <c>-e KEY=value</c>), one entry per line.
/// </summary>
public sealed class ContainerSpec
{
    public string? Name { get; set; }
    public string Image { get; set; } = "";

    /// <summary>Overrides the image's command; shell-style quoting.</summary>
    public string? Command { get; set; }

    public string? Environment { get; set; }
    public string? Ports { get; set; }
    public string? Volumes { get; set; }
    public string? Labels { get; set; }
    public string? Network { get; set; }

    /// <summary><c>no</c>, <c>always</c>, <c>unless-stopped</c> or <c>on-failure</c>.</summary>
    public string RestartPolicy { get; set; } = "no";

    public int? MaxRetries { get; set; }

    /// <summary>Megabytes.</summary>
    public long? MemoryLimit { get; set; }

    /// <summary>CPUs, e.g. 0.5.</summary>
    public decimal? CpuLimit { get; set; }

    public string? User { get; set; }
    public string? WorkingDir { get; set; }
    public bool Tty { get; set; }
    public bool Interactive { get; set; }
}

/// <summary>A problem with one field of a <see cref="ContainerSpec"/>, keyed by the property name.</summary>
public sealed record SpecError(string Field, string Message);

public static partial class ContainerSpecBuilder
{
    public static readonly IReadOnlyList<string> RestartPolicies = ["no", "always", "unless-stopped", "on-failure"];

    /// <summary>Builds the engine's create body, or the errors that prevent it.</summary>
    public static (JsonObject? Body, IReadOnlyList<SpecError> Errors) Build(ContainerSpec spec)
    {
        var errors = new List<SpecError>();

        if (string.IsNullOrWhiteSpace(spec.Image))
            errors.Add(new(nameof(spec.Image), "Enter an image, e.g. nginx:1.27."));
        else if (spec.Image.Any(char.IsWhiteSpace))
            errors.Add(new(nameof(spec.Image), "An image reference has no spaces."));

        if (!string.IsNullOrWhiteSpace(spec.Name) && !ContainerName().IsMatch(spec.Name.Trim()))
            errors.Add(new(nameof(spec.Name), "Use letters, digits and . _ - only, starting with a letter or digit."));

        var command = Collect(errors, nameof(spec.Command), () => ShellWords(spec.Command));
        var env = Collect(errors, nameof(spec.Environment), () => ParseEnvironment(spec.Environment));
        var labels = Collect(errors, nameof(spec.Labels), () => ParseLabels(spec.Labels));
        var ports = Collect(errors, nameof(spec.Ports), () => ParsePorts(spec.Ports));
        var volumes = Collect(errors, nameof(spec.Volumes), () => ParseVolumes(spec.Volumes));

        if (!RestartPolicies.Contains(spec.RestartPolicy))
            errors.Add(new(nameof(spec.RestartPolicy), "Choose a restart policy."));
        if (spec.MaxRetries is < 0)
            errors.Add(new(nameof(spec.MaxRetries), "Use 0 or more."));
        if (spec.MemoryLimit is < 6)
            errors.Add(new(nameof(spec.MemoryLimit), "The engine needs at least 6 MB."));
        if (spec.CpuLimit is <= 0)
            errors.Add(new(nameof(spec.CpuLimit), "Use a number above 0, e.g. 0.5."));

        if (errors.Count > 0)
            return (null, errors);

        var host = new JsonObject
        {
            ["RestartPolicy"] = new JsonObject
            {
                ["Name"] = spec.RestartPolicy,
                ["MaximumRetryCount"] = spec.RestartPolicy == "on-failure" ? spec.MaxRetries ?? 0 : 0,
            },
        };
        if (ports!.Bindings.Count > 0)
            host["PortBindings"] = ports.Bindings;
        if (volumes!.Binds.Count > 0)
            host["Binds"] = new JsonArray([.. volumes.Binds.Select(b => (JsonNode)b)]);
        if (!string.IsNullOrWhiteSpace(spec.Network))
            host["NetworkMode"] = spec.Network.Trim();
        if (spec.MemoryLimit is { } mb)
            host["Memory"] = mb * 1024 * 1024;
        if (spec.CpuLimit is { } cpus)
            host["NanoCpus"] = (long)(cpus * 1_000_000_000m);

        var body = new JsonObject
        {
            ["Image"] = spec.Image.Trim(),
            ["Env"] = new JsonArray([.. env!.Select(e => (JsonNode)e)]),
            ["Labels"] = Object(labels!),
            ["Tty"] = spec.Tty,
            ["OpenStdin"] = spec.Interactive,
            ["HostConfig"] = host,
        };
        if (command is { Count: > 0 })
            body["Cmd"] = new JsonArray([.. command.Select(c => (JsonNode)c)]);
        if (ports.Exposed.Count > 0)
            body["ExposedPorts"] = ports.Exposed;
        if (volumes.Anonymous.Count > 0)
            body["Volumes"] = Object(volumes.Anonymous.ToDictionary(v => v, _ => (JsonNode?)new JsonObject()));
        if (!string.IsNullOrWhiteSpace(spec.User))
            body["User"] = spec.User.Trim();
        if (!string.IsNullOrWhiteSpace(spec.WorkingDir))
            body["WorkingDir"] = spec.WorkingDir.Trim();

        return (body, errors);
    }

    // ---------------------------------------------------------------- parsers

    /// <summary>Thrown by a parser; becomes a <see cref="SpecError"/> on that field.</summary>
    public sealed class SpecFormatException(string message) : FormatException(message);

    private static T? Collect<T>(List<SpecError> errors, string field, Func<T> parse) where T : class
    {
        try
        {
            return parse();
        }
        catch (SpecFormatException ex)
        {
            errors.Add(new(field, ex.Message));
            return null;
        }
    }

    private static IEnumerable<(int Number, string Text)> Lines(string? text) =>
        (text ?? "").Split('\n')
            .Select((line, index) => (Number: index + 1, Text: line.Trim()))
            .Where(l => l.Text.Length > 0 && !l.Text.StartsWith('#'));

    public static List<string> ParseEnvironment(string? text)
    {
        List<string> env = [];
        foreach (var (number, line) in Lines(text))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
                throw new SpecFormatException($"Line {number}: use NAME=value.");
            if (line[..equals].Any(char.IsWhiteSpace))
                throw new SpecFormatException($"Line {number}: a variable name has no spaces.");
            env.Add(line);
        }
        return env;
    }

    public static Dictionary<string, string> ParseLabels(string? text)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (number, line) in Lines(text))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            var key = equals < 0 ? line : line[..equals];
            if (key.Length == 0 || key.Any(char.IsWhiteSpace))
                throw new SpecFormatException($"Line {number}: use key=value; a key has no spaces.");
            labels[key] = equals < 0 ? "" : line[(equals + 1)..];
        }
        return labels;
    }

    public sealed record PortSpec(JsonObject Exposed, JsonObject Bindings);

    /// <summary>
    /// <c>80</c> (random host port), <c>8080:80</c>, <c>8080:80/udp</c>, <c>127.0.0.1:8080:80</c>, <c>[::1]:8080:80</c>.
    /// </summary>
    public static PortSpec ParsePorts(string? text)
    {
        var exposed = new JsonObject();
        var bindings = new JsonObject();
        foreach (var (number, line) in Lines(text))
        {
            var (spec, protocol) = line.LastIndexOf('/') is var slash and > 0 ? (line[..slash], line[(slash + 1)..].ToLowerInvariant()) : (line, "tcp");
            if (protocol is not ("tcp" or "udp" or "sctp"))
                throw new SpecFormatException($"Line {number}: the protocol is tcp, udp or sctp.");

            string? hostIp = null;
            if (spec.StartsWith('['))
            {
                var close = spec.IndexOf(']', StringComparison.Ordinal);
                if (close < 0 || close + 1 >= spec.Length || spec[close + 1] != ':')
                    throw new SpecFormatException($"Line {number}: write an IPv6 address as [::1]:8080:80.");
                hostIp = spec[1..close];
                spec = spec[(close + 2)..];
            }

            var parts = spec.Split(':');
            string hostPort, containerPort;
            switch (parts.Length)
            {
                case 1 when hostIp is null:
                    (hostPort, containerPort) = ("", parts[0]);
                    break;
                case 2:
                    (hostPort, containerPort) = (parts[0], parts[1]);
                    break;
                case 3 when hostIp is null:
                    (hostIp, hostPort, containerPort) = (parts[0], parts[1], parts[2]);
                    break;
                default:
                    throw new SpecFormatException($"Line {number}: use container port, host:container or ip:host:container.");
            }

            if (!IsPort(containerPort))
                throw new SpecFormatException($"Line {number}: '{containerPort}' is not a port (1–65535). Port ranges aren't supported yet.");
            if (hostPort.Length > 0 && !IsPort(hostPort))
                throw new SpecFormatException($"Line {number}: '{hostPort}' is not a port (1–65535).");
            if (hostIp is { Length: > 0 } && !System.Net.IPAddress.TryParse(hostIp, out _))
                throw new SpecFormatException($"Line {number}: '{hostIp}' is not an IP address.");

            var key = $"{containerPort}/{protocol}";
            exposed[key] = new JsonObject();
            if (bindings[key] is not JsonArray list)
                bindings[key] = list = [];
            list.Add(new JsonObject { ["HostIp"] = hostIp ?? "", ["HostPort"] = hostPort });
        }
        return new PortSpec(exposed, bindings);
    }

    public sealed record VolumeSpec(IReadOnlyList<string> Binds, IReadOnlyList<string> Anonymous);

    /// <summary>
    /// <c>/data</c> (anonymous volume), <c>name:/data</c> (named volume), <c>/host/path:/data</c>
    /// (bind mount), each optionally followed by <c>:ro</c>, <c>:rw</c>, <c>:z</c> or <c>:Z</c>.
    /// </summary>
    public static VolumeSpec ParseVolumes(string? text)
    {
        List<string> binds = [];
        List<string> anonymous = [];
        foreach (var (number, line) in Lines(text))
        {
            var parts = line.Split(':');
            if (parts.Length == 1)
            {
                RequireAbsolute(parts[0], number);
                anonymous.Add(parts[0]);
                continue;
            }
            if (parts.Length > 3)
                throw new SpecFormatException($"Line {number}: use source:/target or source:/target:ro.");

            var (source, target) = (parts[0], parts[1]);
            if (source.Length == 0)
                throw new SpecFormatException($"Line {number}: the source is a volume name or an absolute host path.");
            if (!source.StartsWith('/') && !VolumeName().IsMatch(source))
                throw new SpecFormatException($"Line {number}: '{source}' is neither an absolute host path nor a valid volume name.");
            RequireAbsolute(target, number);
            if (parts.Length == 3 && parts[2].Split(',').Any(o => o is not ("ro" or "rw" or "z" or "Z")))
                throw new SpecFormatException($"Line {number}: options are ro, rw, z or Z (comma-separated).");
            binds.Add(line);
        }
        return new VolumeSpec(binds, anonymous);

        static void RequireAbsolute(string path, int number)
        {
            if (!path.StartsWith('/'))
                throw new SpecFormatException($"Line {number}: the path inside the container must be absolute, e.g. /data.");
        }
    }

    /// <summary>Splits like a POSIX shell: spaces separate words; '…' is literal; "…" and \ escape.</summary>
    public static List<string> ShellWords(string? text)
    {
        List<string> words = [];
        if (string.IsNullOrWhiteSpace(text))
            return words;

        var word = new StringBuilder();
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\'':
                    var end = text.IndexOf('\'', i + 1);
                    if (end < 0)
                        throw new SpecFormatException("A single quote isn't closed.");
                    word.Append(text, i + 1, end - i - 1);
                    i = end;
                    inWord = true;
                    break;
                case '"':
                    i++;
                    for (; i < text.Length && text[i] != '"'; i++)
                    {
                        if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\' or '$' or '`')
                            i++;
                        word.Append(text[i]);
                    }
                    if (i >= text.Length)
                        throw new SpecFormatException("A double quote isn't closed.");
                    inWord = true;
                    break;
                case '\\' when i + 1 < text.Length:
                    word.Append(text[++i]);
                    inWord = true;
                    break;
                case var _ when char.IsWhiteSpace(c):
                    if (inWord)
                    {
                        words.Add(word.ToString());
                        word.Clear();
                        inWord = false;
                    }
                    break;
                default:
                    word.Append(c);
                    inWord = true;
                    break;
            }
        }
        if (inWord)
            words.Add(word.ToString());
        return words;
    }

    /// <summary>The inverse of <see cref="ShellWords"/>: quotes words only when needed.</summary>
    public static string JoinShellWords(IEnumerable<string> words) =>
        string.Join(' ', words.Select(w =>
            w.Length > 0 && w.All(c => char.IsAsciiLetterOrDigit(c) || "-_./=:,@%+".Contains(c, StringComparison.Ordinal))
                ? w
                : $"'{w.Replace("'", "'\\''", StringComparison.Ordinal)}'"));

    private static bool IsPort(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535;

    private static JsonObject Object<T>(IEnumerable<KeyValuePair<string, T>> items)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in items)
            obj[key] = value is JsonNode node ? node : JsonValue.Create(value);
        return obj;
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex ContainerName();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex VolumeName();
}
