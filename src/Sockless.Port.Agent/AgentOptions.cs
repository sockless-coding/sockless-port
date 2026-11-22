using System.Globalization;
using Microsoft.Extensions.Configuration;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;

namespace Sockless.Port.Agent;

/// <summary>
/// The agent's settings. Read by hand rather than bound, to stay trim-safe: <c>Agent:Token</c> or
/// <c>AGENT_TOKEN</c>, and likewise for the engine, port and data folder.
/// </summary>
public sealed class AgentOptions
{
    /// <summary>The enrolment token from the install command. Only used until the agent is enrolled.</summary>
    public AgentToken? Token { get; init; }

    /// <summary>The engine to relay to.</summary>
    public required DockerAddress Engine { get; init; }

    public int Port { get; init; } = AgentProtocol.DefaultPort;

    /// <summary>Where the agent's key and certificates live. In the container, the <c>/data</c> volume.</summary>
    public string DataPath { get; init; } = "/data";

    public static AgentOptions From(IConfiguration configuration)
    {
        string? Read(string key, string env) => configuration[$"Agent:{key}"] is { Length: > 0 } value ? value : configuration[env];

        var tokenText = Read("Token", "AGENT_TOKEN");
        AgentToken? token = null;
        if (!string.IsNullOrWhiteSpace(tokenText) && !AgentToken.TryParse(tokenText, out token))
            throw new InvalidOperationException("AGENT_TOKEN isn't a Sockless Port enrolment token. Copy the command from the endpoint's page again.");

        var engineText = Read("Engine", "AGENT_ENGINE") ?? configuration["DOCKER_HOST"];
        var engine = string.IsNullOrWhiteSpace(engineText) ? DefaultEngine() : DockerAddress.Parse(engineText);

        var port = AgentProtocol.DefaultPort;
        if (Read("Port", "AGENT_PORT") is { } portText && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            throw new InvalidOperationException($"AGENT_PORT '{portText}' isn't a port number.");

        return new AgentOptions
        {
            Token = token,
            Engine = engine,
            Port = port,
            DataPath = Read("DataPath", "AGENT_DATA") ?? "/data",
        };
    }

    /// <summary>The install commands mount the engine's socket here, whatever the engine.</summary>
    private static DockerAddress DefaultEngine()
    {
        if (OperatingSystem.IsWindows())
            return new NamedPipeAddress("docker_engine");
        foreach (var path in (string[])["/var/run/docker.sock", "/run/podman/podman.sock"])
        {
            if (File.Exists(path))
                return new UnixSocketAddress(path);
        }
        return new UnixSocketAddress("/var/run/docker.sock");
    }
}
