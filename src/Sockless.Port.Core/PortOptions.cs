namespace Sockless.Port;

/// <summary>Application-wide settings, bound from the <c>Port</c> configuration section.</summary>
public sealed class PortOptions
{
    public const string SectionName = "Port";

    /// <summary>
    /// Directory for everything Sockless Port persists: the SQLite database, Data Protection keys
    /// and (later) stack files. In the container this is the <c>/data</c> volume.
    /// </summary>
    public string DataPath { get; set; } = "/data";

    /// <summary>
    /// Address of the local engine, e.g. <c>unix:///run/podman/podman.sock</c>. When empty, the
    /// address is detected on first start (<c>DOCKER_HOST</c>, then the well-known sockets). When
    /// set, it also replaces the stored local endpoint's address on every start.
    /// </summary>
    public string? LocalEngine { get; set; }

    public string DatabasePath => Path.Combine(DataPath, "port.db");

    public string KeysPath => Path.Combine(DataPath, "keys");
}
