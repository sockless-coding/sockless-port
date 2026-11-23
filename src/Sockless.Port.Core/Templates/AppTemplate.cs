namespace Sockless.Port.Templates;

/// <summary>What deploying a template makes.</summary>
public enum TemplateKind
{
    /// <summary>One container (Portainer type 1, linuxserver.io images), deployed as a one-service Compose stack.</summary>
    Container,

    /// <summary>A compose file fetched from a repository (Portainer type 3, and type 4 edge stacks).</summary>
    Compose,

    /// <summary>A swarm stack file fetched from a repository (Portainer type 2); swarm managers only.</summary>
    Swarm,
}

/// <summary>
/// One app from a template feed, read into one shape whatever the feed's format. Everything here
/// came from the feed, so it's untrusted: it is shown as text and turned into a compose file the
/// user reviews before anything runs.
/// </summary>
public sealed record AppTemplate
{
    /// <summary>Unique within its source, from the title: lower-case letters, digits and <c>-</c>.</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required TemplateKind Kind { get; init; }

    public string Description { get; init; } = "";

    /// <summary>The feed's note, in its limited HTML; render it through <see cref="TemplateNote"/>.</summary>
    public string? Note { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>An http(s) or <c>data:</c> image URL.</summary>
    public string? Logo { get; init; }

    /// <summary><c>linux</c> or <c>windows</c>, when the feed says.</summary>
    public string? Platform { get; init; }

    /// <summary>Who wrote the template, when the feed says (text or a URL).</summary>
    public string? Maintainer { get; init; }

    /// <summary>The project's page or setup guide, when the feed links one.</summary>
    public string? Link { get; init; }

    /// <summary>Portainer's <c>administrator_only</c>: shown to administrators only.</summary>
    public bool AdministratorOnly { get; init; }

    /// <summary>The service's name in the compose file it makes (Portainer's <c>name</c>, else the key).</summary>
    public string? ServiceName { get; init; }

    /// <summary>Variables the user fills in. They go into the stack's <c>.env</c>.</summary>
    public IReadOnlyList<TemplateVariable> Variables { get; init; } = [];

    // Container templates.

    public string? Image { get; init; }

    public string? Command { get; init; }

    public IReadOnlyList<TemplatePort> Ports { get; init; } = [];

    public IReadOnlyList<TemplateVolume> Volumes { get; init; } = [];

    public IReadOnlyList<KeyValuePair<string, string>> Labels { get; init; } = [];

    public string? RestartPolicy { get; init; }

    /// <summary>A network mode: <c>host</c>, <c>none</c>, or a network name.</summary>
    public string? Network { get; init; }

    public bool Privileged { get; init; }

    public string? Hostname { get; init; }

    /// <summary>Keep standard input open and allocate a terminal.</summary>
    public bool Interactive { get; init; }

    /// <summary>Host devices, <c>host:container</c>.</summary>
    public IReadOnlyList<string> Devices { get; init; } = [];

    public IReadOnlyList<string> CapAdd { get; init; } = [];

    public IReadOnlyList<string> SecurityOpt { get; init; } = [];

    /// <summary>A few more Compose settings, by key (<see cref="TemplateSetting.AllowedKeys"/>).</summary>
    public IReadOnlyList<TemplateSetting> Settings { get; init; } = [];

    // Stack templates.

    /// <summary>Where the compose file is fetched from; null for container templates, or when its host isn't supported.</summary>
    public Uri? StackFile { get; init; }

    /// <summary>The repository and path the feed names, for showing where the file comes from.</summary>
    public string? StackFileSource { get; init; }

    /// <summary>Why the template can't be deployed, e.g. a repository on an unsupported host.</summary>
    public string? Unavailable { get; init; }
}

/// <param name="Name">The variable's name, as the compose file uses it.</param>
/// <param name="Default">The value the form starts with.</param>
/// <param name="Preset">Set by the template and not shown in the form (Portainer's <c>preset</c>).</param>
/// <param name="Choices">The values to pick from, when the template limits them.</param>
/// <param name="Optional">Left out unless the user gives a value (linuxserver.io's optional variables); <paramref name="Default"/> is then an example.</param>
public sealed record TemplateVariable(string Name, string Label, string? Description, string Default, bool Preset, IReadOnlyList<TemplateChoice> Choices,
    bool Optional = false);

public sealed record TemplateChoice(string Text, string Value, bool IsDefault);

/// <param name="HostIp">The host address to publish on, when the template names one.</param>
/// <param name="Host">The host port, when the template names one; otherwise the engine picks one.</param>
/// <param name="Container">The container's port, or a range (<c>8000-8010</c>).</param>
/// <param name="Protocol"><c>tcp</c> or <c>udp</c>.</param>
/// <param name="Optional">Not published unless the user chooses it.</param>
public sealed record TemplatePort(string? HostIp, string? Host, string Container, string Protocol, string? Description = null, bool Optional = false)
{
    public string ContainerSpec => $"{Container}/{Protocol}";
}

/// <param name="Container">The path in the container.</param>
/// <param name="Bind">A host path the template binds; null for a volume.</param>
/// <param name="Optional">Left out unless the user chooses it.</param>
public sealed record TemplateVolume(string Container, string? Bind, bool ReadOnly, string? Description = null, bool Optional = false);

/// <summary>A Compose service setting the form doesn't ask about, e.g. <c>shm_size: 1gb</c>.</summary>
/// <param name="Values">One value, or the items of a list (<see cref="IsList"/>).</param>
public sealed record TemplateSetting(string Key, IReadOnlyList<string> Values, bool IsList)
{
    /// <summary>The only keys taken from feeds: harmless, and written as plain values.</summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal) { "shm_size", "stop_grace_period", "sysctls" };
}
