namespace Sockless.Port;

/// <summary>Application-wide settings, bound from the <c>Port</c> configuration section.</summary>
public sealed class PortOptions
{
    public const string SectionName = "Port";

    /// <summary>
    /// Directory for everything Sockless Port persists: the SQLite database, Data Protection keys
    /// and stack files. In the container this is the <c>/data</c> volume.
    /// </summary>
    public string DataPath { get; set; } = "/data";

    /// <summary>
    /// Address of the local engine, e.g. <c>unix:///run/podman/podman.sock</c>. When empty, the
    /// address is detected on first start (<c>DOCKER_HOST</c>, then the well-known sockets). When
    /// set, it also replaces the stored local endpoint's address on every start.
    /// </summary>
    public string? LocalEngine { get; set; }

    /// <summary>
    /// Image for the short-lived helper containers that read a volume's files. It needs
    /// <c>find</c> and <c>stat</c> (busybox has both). Pulled when missing, so set one the engine
    /// already has on hosts without internet access.
    /// </summary>
    public string HelperImage { get; set; } = "busybox:1.37";

    /// <summary>
    /// The Compose command, e.g. <c>docker-compose</c>, <c>docker compose</c> or a full path. When
    /// empty, <c>docker-compose</c> is tried first, then <c>docker compose</c>. The image bundles
    /// <c>docker-compose</c>.
    /// </summary>
    public string? Compose { get; set; }

    public string DatabasePath => Path.Combine(DataPath, "port.db");

    public string KeysPath => Path.Combine(DataPath, "keys");

    /// <summary>Stack files: <c>stacks/{endpointId}/{name}/compose.yaml</c> and <c>.env</c>.</summary>
    public string StacksPath => Path.Combine(DataPath, "stacks");

    /// <summary>Uploaded build contexts while they're built: on the data volume, since they can be large. Emptied on start.</summary>
    public string TempPath => Path.Combine(DataPath, "tmp");
}
