using Sockless.Port.Docker.Engine;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// What a rootless engine (Docker or Podman run by an ordinary user) can't do, said where it
/// matters. Null on other engines. When an engine call fails for one of these reasons,
/// <see cref="Engines.EngineHints"/> explains the error too.
/// </summary>
public static class RootlessNotes
{
    /// <summary>For the dashboard's engine badge.</summary>
    public const string Badge = "Runs as an ordinary user: it can't publish ports below 1024, and limits and stats need cgroups v2 with controllers delegated to that user.";

    public static string? Ports(EngineDescriptor? engine) =>
        engine?.Has(EngineCapabilities.Rootless) == true
            ? "This engine is rootless, so it can't publish host ports below 1024 (publish 8080:80 rather than 80:80), unless net.ipv4.ip_unprivileged_port_start is lowered on its host."
            : null;

    public static string? Limits(EngineDescriptor? engine) => engine switch
    {
        { RootlessWithoutCgroups: true } =>
            "This engine is rootless on cgroups v1, which gives ordinary users no cgroups: memory and CPU limits are refused or ignored.",
        not null when engine.Has(EngineCapabilities.Rootless) =>
            "This engine is rootless: memory and CPU limits apply only if its host delegates the cpu and memory cgroup controllers to the engine's user.",
        _ => null,
    };

    public static string? Stats(EngineDescriptor? engine) =>
        engine?.RootlessWithoutCgroups == true
            ? "This engine is rootless on cgroups v1, so it can't measure its containers: CPU, memory and disk readings stay empty or fail. Moving the host to cgroups v2 fixes this."
            : null;
}
