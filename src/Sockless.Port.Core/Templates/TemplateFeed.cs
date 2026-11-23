using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sockless.Port.Templates;

/// <summary>Thrown when a feed can't be read; the message is for people.</summary>
public sealed class TemplateFeedException(string message) : Exception(message);

/// <summary>The formats a feed can be in, told apart by their content.</summary>
public enum TemplateFeedFormat
{
    /// <summary>Portainer's app templates, version 2 or 3: <c>{"version": "2", "templates": [...]}</c>.</summary>
    Portainer,

    /// <summary>The linuxserver.io image API (<c>api.linuxserver.io/api/v1/images?include_config=true</c>).</summary>
    LinuxServer,
}

/// <summary>Reads template feeds into <see cref="AppTemplate"/>s.</summary>
public static partial class TemplateFeed
{
    /// <summary>The most templates read from one feed.</summary>
    public const int MaxTemplates = 5000;

    /// <exception cref="TemplateFeedException">It isn't a feed in a known format.</exception>
    public static (TemplateFeedFormat Format, IReadOnlyList<AppTemplate> Templates) Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw new TemplateFeedException($"It isn't valid JSON: {ex.Message}");
        }

        if (root is JsonObject feed && feed["templates"] is JsonArray templates)
            return (TemplateFeedFormat.Portainer, Unique(templates.OfType<JsonObject>().Take(MaxTemplates).Select(ReadPortainer).OfType<AppTemplate>()));
        if (root is JsonObject api && api["data"]?["repositories"] is JsonObject repositories)
        {
            var images = repositories.SelectMany(r => r.Value as JsonArray ?? []).OfType<JsonObject>();
            return (TemplateFeedFormat.LinuxServer, Unique(images.Take(MaxTemplates).Select(ReadLinuxServer).OfType<AppTemplate>()));
        }
        if (root is JsonArray)
            throw new TemplateFeedException("It's a list, not a template feed. Portainer's format (version 2 or 3) has the templates under \"templates\"; the version 1 format isn't supported.");
        throw new TemplateFeedException("It isn't a template feed Sockless Port knows: Portainer's app templates (version 2 or 3) or the linuxserver.io API.");
    }

    /// <summary>
    /// Lower-case ASCII letters, digits and dashes: <c>uptime-kuma</c> for "Uptime Kuma". Other
    /// letters are left out (the server runs with invariant globalization, which can't take accents off).
    /// </summary>
    public static string Slug(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
            else if (char.IsLetter(c))
                continue;
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        var slug = builder.ToString().Trim('-');
        if (slug.Length > 60)
            slug = slug[..60].TrimEnd('-');
        return slug.Length == 0 ? "app" : slug;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    public static partial Regex KeyPattern();

    private static List<AppTemplate> Unique(IEnumerable<AppTemplate> templates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<AppTemplate> result = [];
        foreach (var template in templates)
        {
            // Feeds often have a title twice, as a container and as a stack: the kind tells them apart.
            var key = template.Key;
            if (!seen.Add(key))
            {
                key = $"{template.Key}-{template.Kind.ToString().ToLowerInvariant()}";
                for (var n = 2; !seen.Add(key); n++)
                    key = $"{template.Key}-{n}";
            }
            result.Add(template with { Key = key });
        }
        return result;
    }

    private static AppTemplate? ReadPortainer(JsonObject t)
    {
        var title = Text(t["title"]);
        if (title is null)
            return null;
        var type = Int(t["type"]) ?? 1;
        var common = new AppTemplate
        {
            Key = Slug(title),
            Title = Truncate(title, 120),
            Kind = TemplateKind.Container,
            Description = Truncate(Text(t["description"]) ?? "", 2000),
            Note = Text(t["note"]) is { } note ? Truncate(note, 8000) : null,
            Categories = Strings(t["categories"]).Select(c => Truncate(c, 40)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList(),
            Logo = ImageUrl(Text(t["logo"])),
            Platform = Text(t["platform"])?.ToLowerInvariant(),
            Maintainer = Text(t["maintainer"]) is { } maintainer ? Truncate(maintainer, 200) : null,
            AdministratorOnly = t["administrator_only"]?.GetValueKind() == JsonValueKind.True,
            ServiceName = ServiceName(Text(t["name"])),
            Variables = Variables(t["env"] as JsonArray),
        };

        switch (type)
        {
            case 1:
                if (Text(t["image"]) is not { } image)
                    return null;
                return common with
                {
                    Image = image.Trim(),
                    Command = Text(t["command"]),
                    Ports = Strings(t["ports"]).Select(p => Port(p)).OfType<TemplatePort>().ToList(),
                    Volumes = (t["volumes"] as JsonArray ?? []).OfType<JsonObject>()
                        .Select(v => Text(v["container"]) is { } container ? Volume(container, Text(v["bind"]), v["readonly"]?.GetValueKind() == JsonValueKind.True) : null)
                        .OfType<TemplateVolume>().ToList(),
                    Labels = (t["labels"] as JsonArray ?? []).OfType<JsonObject>()
                        .Select(l => (Name: Text(l["name"]), Value: Text(l["value"]) ?? ""))
                        .Where(l => l.Name is not null)
                        .Select(l => KeyValuePair.Create(l.Name!, l.Value)).ToList(),
                    RestartPolicy = RestartPolicy(Text(t["restart_policy"])),
                    Network = Text(t["network"]),
                    Privileged = t["privileged"]?.GetValueKind() == JsonValueKind.True,
                    Hostname = Text(t["hostname"]),
                    Interactive = t["interactive"]?.GetValueKind() == JsonValueKind.True,
                };
            case 2 or 3 or 4:
                var kind = type == 2 ? TemplateKind.Swarm : TemplateKind.Compose;
                if (t["repository"] is JsonObject repository)
                {
                    var url = Text(repository["url"]);
                    var file = Text(repository["stackfile"]);
                    var raw = TemplateRepositories.RawUrl(url, file);
                    return common with
                    {
                        Kind = kind,
                        StackFile = raw,
                        StackFileSource = $"{url} ({file})",
                        Unavailable = raw is null
                            ? $"Its compose file is in a repository Sockless Port can't read files from ({url}). Only files on {TemplateRepositories.SupportedHosts} can be fetched."
                            : null,
                    };
                }
                // Type 4 (edge stacks) and some type 3 name the file's URL directly.
                if (Text(t["stackfile"]) is { } direct && Uri.TryCreate(direct, UriKind.Absolute, out var directUrl) && directUrl.Scheme == Uri.UriSchemeHttps)
                    return common with { Kind = kind, StackFile = directUrl, StackFileSource = direct };
                return common with { Kind = kind, Unavailable = "The template doesn't say where its compose file is." };
            default:
                return null;
        }
    }

    private static AppTemplate? ReadLinuxServer(JsonObject image)
    {
        var name = Text(image["name"]);
        if (name is null || image["deprecated"]?.GetValueKind() == JsonValueKind.True || !ImageName().IsMatch(name))
            return null;
        var config = image["config"] as JsonObject ?? [];
        bool Required(JsonNode? entry) => entry?["optional"]?.GetValueKind() != JsonValueKind.True;

        List<TemplateSetting> settings = [];
        foreach (var custom in (config["custom"] as JsonArray ?? []).OfType<JsonObject>().Where(Required))
        {
            if (Text(custom["name_compose"]) is not { } key || !TemplateSetting.AllowedKeys.Contains(key))
                continue;
            settings.Add(custom["value"] is JsonArray list
                ? new TemplateSetting(key, list.Select(Text).OfType<string>().ToList(), IsList: true)
                : new TemplateSetting(key, [Text(custom["value"]) ?? ""], IsList: false));
        }

        return new AppTemplate
        {
            Key = Slug(name),
            Title = name,
            Kind = TemplateKind.Container,
            Description = Truncate(Text(image["description"]) ?? "", 2000),
            Categories = (Text(image["category"]) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToList(),
            Logo = ImageUrl(Text(image["project_logo"])),
            Platform = "linux",
            Maintainer = "linuxserver.io",
            Link = HttpsUrl(Text(config["application_setup"])) ?? HttpsUrl(Text(image["github_url"])),
            ServiceName = ServiceName(name),
            Image = $"lscr.io/linuxserver/{name}:latest",
            Variables = (config["env_vars"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(e => Text(e["name"]) is { } env
                    ? new TemplateVariable(env, env, Text(e["desc"]), Text(e["value"]) ?? "", Preset: false, [], Optional: !Required(e))
                    : null)
                .OfType<TemplateVariable>().DistinctBy(v => v.Name).ToList(),
            Ports = (config["ports"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(p => Port($"{Text(p["external"])}:{Text(p["internal"])}", Text(p["desc"]), !Required(p)))
                .OfType<TemplatePort>().ToList(),
            Volumes = (config["volumes"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(v => Text(v["path"]) is { } path
                    ? new TemplateVolume(path, Text(v["host_path"]), false, Text(v["desc"]), !Required(v))
                    : null)
                .OfType<TemplateVolume>().ToList(),
            Devices = (config["devices"] as JsonArray ?? []).OfType<JsonObject>().Where(Required)
                .Select(d => (Host: Text(d["host_path"]), Container: Text(d["path"])))
                .Where(d => d.Host is not null && d.Container is not null)
                .Select(d => $"{d.Host}:{d.Container}").ToList(),
            CapAdd = (config["caps"] as JsonArray ?? []).OfType<JsonObject>().Where(Required).Select(c => Text(c["cap_add"])).OfType<string>().ToList(),
            SecurityOpt = (config["security_opt"] as JsonArray ?? []).OfType<JsonObject>().Where(Required).Select(s => Text(s["compose_var"])).OfType<string>().ToList(),
            Hostname = (config["hostname"] is JsonObject hostname && Required(hostname)) ? Text(hostname["hostname"]) : null,
            Network = Text(config["networking"]),
            Privileged = config["privileged"]?.GetValueKind() == JsonValueKind.True,
            RestartPolicy = "unless-stopped",
            Settings = settings,
        };
    }

    /// <summary>Some feeds put the mode in the paths (<c>/srv/media:ro</c>); it's taken out into <see cref="TemplateVolume.ReadOnly"/>.</summary>
    private static TemplateVolume Volume(string container, string? bind, bool readOnly)
    {
        static (string Path, string? Mode) Split(string path) =>
            path.EndsWith(":ro", StringComparison.Ordinal) || path.EndsWith(":rw", StringComparison.Ordinal) ? (path[..^3], path[^2..]) : (path, null);
        var (target, targetMode) = Split(container);
        var (source, sourceMode) = bind is null ? (null, null) : Split(bind);
        return new TemplateVolume(target, source, readOnly || targetMode == "ro" || sourceMode == "ro");
    }

    private static List<TemplateVariable> Variables(JsonArray? env)
    {
        List<TemplateVariable> variables = [];
        foreach (var e in (env ?? []).OfType<JsonObject>())
        {
            // Some feeds list a variable twice; the first one counts.
            if (Text(e["name"]) is not { } name || variables.Any(v => v.Name == name))
                continue;
            var choices = (e["select"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(c => new TemplateChoice(Text(c["text"]) ?? Text(c["value"]) ?? "", Text(c["value"]) ?? "", c["default"]?.GetValueKind() == JsonValueKind.True))
                .ToList();
            var initial = Text(e["default"]) ?? choices.FirstOrDefault(c => c.IsDefault)?.Value ?? choices.FirstOrDefault()?.Value ?? "";
            variables.Add(new TemplateVariable(name, Text(e["label"]) ?? name, Text(e["description"]), initial,
                e["preset"]?.GetValueKind() == JsonValueKind.True, choices));
        }
        return variables;
    }

    /// <summary>
    /// <c>80/tcp</c>, <c>8080:80</c>, <c>127.0.0.1:3000:3000/udp</c>, <c>8000-8010:8000-8010</c>.
    /// </summary>
    internal static TemplatePort? Port(string? spec, string? description = null, bool optional = false)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return null;
        var text = spec.Trim();
        var protocol = "tcp";
        var slash = text.LastIndexOf('/');
        if (slash >= 0)
        {
            protocol = text[(slash + 1)..].ToLowerInvariant();
            text = text[..slash];
        }
        if (protocol is not ("tcp" or "udp" or "sctp"))
            return null;

        var parts = text.Split(':');
        var (ip, host, container) = parts switch
        {
            [var c] => (null, null, c),
            [var h, var c] => (null, h, c),
            [var i, var h, var c] => (i, h, c),
            _ => (null, (string?)null, ""),
        };
        if (!PortRange().IsMatch(container) || (host is { Length: > 0 } && !PortRange().IsMatch(host)) || (ip is { Length: > 0 } && !System.Net.IPAddress.TryParse(ip, out _)))
            return null;
        return new TemplatePort(string.IsNullOrEmpty(ip) ? null : ip, string.IsNullOrEmpty(host) ? null : host, container, protocol, description, optional);
    }

    [GeneratedRegex("^[0-9]{1,5}(-[0-9]{1,5})?$")]
    internal static partial Regex PortRange();

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{0,127}$")]
    private static partial Regex ImageName();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,62}$")]
    private static partial Regex ServiceNamePattern();

    private static string? ServiceName(string? name) =>
        name is not null && ServiceNamePattern().IsMatch(name) ? name.ToLowerInvariant() : null;

    private static string? RestartPolicy(string? policy) =>
        policy?.ToLowerInvariant() is "no" or "always" or "on-failure" or "unless-stopped" ? policy.ToLowerInvariant() : null;

    /// <summary>An http(s) image URL, or a small <c>data:image/…</c> one.</summary>
    private static string? ImageUrl(string? url) => url switch
    {
        null => null,
        _ when url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && url.Length <= TemplateLogos.MaxBytes * 4 / 3 + 100 => url,
        _ when Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) => uri.AbsoluteUri,
        _ => null,
    };

    private static string? HttpsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri.AbsoluteUri : null;

    /// <summary>A string, number or boolean as text; null for anything else or blank.</summary>
    private static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        var text = value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? Int(JsonNode? node) => Text(node) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static IEnumerable<string> Strings(JsonNode? node) => (node as JsonArray ?? []).Select(Text).OfType<string>();

    private static string Truncate(string text, int max) => text.Length <= max ? text.Trim() : text[..max].Trim() + "…";
}
