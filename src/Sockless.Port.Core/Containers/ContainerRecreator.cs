using System.Globalization;
using System.Text.Json.Nodes;
using Sockless.Port.Docker;

namespace Sockless.Port.Containers;

/// <summary>
/// Replaces a container with a new one from the same configuration, optionally pulling its
/// image first (the usual way to update a container). Works on the engine's raw inspect JSON so
/// settings the UI doesn't model (capabilities, devices, ulimits, …) are kept.
/// </summary>
public static class ContainerRecreator
{
    /// <param name="registryAuth">Finds the <c>X-Registry-Auth</c> header for the image, for private registries.</param>
    /// <returns>The new container's id.</returns>
    public static async Task<string> RecreateAsync(DockerClient client, string id, bool pull, CancellationToken cancellationToken = default,
        Func<string, CancellationToken, Task<string?>>? registryAuth = null)
    {
        var (container, json) = await client.InspectContainerAsync(id, cancellationToken);
        var inspect = JsonNode.Parse(json)!.AsObject();
        var (body, extraNetworks) = BuildCreateBody(inspect);
        var name = container.DisplayName;
        var wasRunning = container.State?.Running == true || container.State?.Restarting == true;

        if (pull && body["Image"]?.GetValue<string>() is { } image && !image.StartsWith("sha256:", StringComparison.Ordinal))
        {
            var auth = registryAuth is null ? null : await registryAuth(image, cancellationToken);
            await foreach (var _ in client.PullImageAsync(image, auth, cancellationToken)) { }
        }

        // From here on, any failure puts the original back.
        var parked = $"{name}-replaced-{DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        if (wasRunning)
            await client.StopContainerAsync(container.Id, cancellationToken: cancellationToken);
        await client.RenameContainerAsync(container.Id, parked, cancellationToken);

        string? newId = null;
        try
        {
            newId = (await client.CreateContainerAsync(name, body, cancellationToken)).Id;
            foreach (var (network, endpoint) in extraNetworks)
                await client.ConnectNetworkAsync(network, newId, endpoint, cancellationToken);
            if (wasRunning)
                await client.StartContainerAsync(newId, cancellationToken);
        }
        catch
        {
            // Not cancellable: the rollback must finish even if the request was aborted.
            if (newId is not null)
                await TryAsync(() => client.RemoveContainerAsync(newId, force: true));
            await TryAsync(() => client.RenameContainerAsync(container.Id, name));
            if (wasRunning)
                await TryAsync(() => client.StartContainerAsync(container.Id));
            throw;
        }

        await client.RemoveContainerAsync(container.Id, force: true, cancellationToken: CancellationToken.None);
        return newId;
    }

    /// <summary>
    /// Turns inspect output into a create body. Networks: only the primary one can be given at
    /// create on every API version (1.41 allows one), so the others are returned to connect after.
    /// </summary>
    internal static (JsonObject Body, List<(string Network, JsonObject Endpoint)> ExtraNetworks) BuildCreateBody(JsonObject inspect)
    {
        var id = inspect["Id"]?.GetValue<string>() ?? "";
        var shortId = id.Length >= 12 ? id[..12] : id;

        var body = inspect["Config"]?.DeepClone().AsObject() ?? new JsonObject();
        var host = inspect["HostConfig"]?.DeepClone().AsObject() ?? new JsonObject();

        var mode = host["NetworkMode"]?.GetValue<string>() ?? "default";

        // The engine sets the hostname to the short id unless one was given; a new container gets
        // its own. In the host's UTS namespace (host networking) it's the host's name and setting
        // it is an error on both Docker and Podman.
        if (body["Hostname"]?.GetValue<string>() == shortId || mode == "host" || host["UTSMode"]?.GetValue<string>() == "host")
        {
            body.Remove("Hostname");
            body.Remove("Domainname");
        }
        // Inspect reports the default /dev/shm size even when the container shares the host's IPC
        // namespace, where Podman rejects setting it (and it means nothing).
        if (host["IpcMode"]?.GetValue<string>() == "host")
            host.Remove("ShmSize");
        body["HostConfig"] = host;
        var extra = new List<(string, JsonObject)>();
        if (inspect["NetworkSettings"]?["Networks"] is JsonObject networks && !IsSpecialMode(mode))
        {
            var primary = mode == "default" ? "bridge" : mode;
            var endpoints = new JsonObject();
            foreach (var (network, settings) in networks)
            {
                if (settings is not JsonObject endpoint)
                    continue;
                var clean = CleanEndpoint(endpoint, shortId);
                if (network == primary)
                    endpoints[network] = clean;
                else
                    extra.Add((network, clean));
            }
            if (endpoints.Count > 0)
                body["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = endpoints };
        }
        return (body, extra);
    }

    /// <summary><c>host</c>, <c>none</c> and <c>container:…</c> take no network endpoints.</summary>
    private static bool IsSpecialMode(string mode) =>
        mode is "host" or "none" || mode.StartsWith("container:", StringComparison.Ordinal);

    /// <summary>Keeps what the user configured (aliases, static IPs, links); drops what the engine assigned.</summary>
    private static JsonObject CleanEndpoint(JsonObject endpoint, string shortId)
    {
        var clean = new JsonObject();
        if (endpoint["Aliases"] is JsonArray aliases)
        {
            var kept = aliases.Select(a => a?.GetValue<string>()).Where(a => a is not null && a != shortId).ToList();
            if (kept.Count > 0)
                clean["Aliases"] = new JsonArray([.. kept.Select(a => (JsonNode)a!)]);
        }
        foreach (var key in (string[])["IPAMConfig", "Links", "DriverOpts"])
        {
            if (endpoint[key] is { } value)
                clean[key] = value.DeepClone();
        }
        return clean;
    }

    private static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            // Best effort during rollback; the original error is what the caller sees.
        }
    }
}
