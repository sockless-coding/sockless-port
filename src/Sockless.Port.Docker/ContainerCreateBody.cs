using System.Text.Json.Nodes;

namespace Sockless.Port.Docker;

/// <summary>
/// Turns a container's raw inspect JSON into a create body for a new container with the same
/// configuration, so settings nothing here models (capabilities, devices, ulimits, …) are kept.
/// Shared by the server's recreate and moves, and the agent's self-update.
/// </summary>
public static class ContainerCreateBody
{
    /// <summary>
    /// The create body, and the networks to connect after creating: only the primary one can be
    /// given at create on every API version (1.41 allows one).
    /// </summary>
    public static (JsonObject Body, List<(string Network, JsonObject Endpoint)> ExtraNetworks) FromInspect(JsonObject inspect)
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

    /// <summary>
    /// Removes from <paramref name="body"/> what the container only has because its image said so
    /// (<paramref name="imageConfig"/>, the old image's <c>Config</c>), so a new image's defaults
    /// apply instead of the old one's: environment entries and labels that match, and the command,
    /// entry point, user, working directory, health check and stop signal when they're the image's.
    /// What was set when the container was created stays.
    /// </summary>
    public static void RemoveImageDefaults(JsonObject body, JsonObject? imageConfig)
    {
        if (imageConfig is null)
            return;

        if (body["Env"] is JsonArray env && imageConfig["Env"] is JsonArray imageEnv)
        {
            var inherited = imageEnv.Select(e => e?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var kept = env.Select(e => e?.GetValue<string>()).OfType<string>().Where(e => !inherited.Contains(e)).ToList();
            body["Env"] = new JsonArray([.. kept.Select(e => (JsonNode)e)]);
        }

        if (body["Labels"] is JsonObject labels && imageConfig["Labels"] is JsonObject imageLabels)
        {
            foreach (var (key, value) in imageLabels)
            {
                if (labels[key] is { } own && JsonNode.DeepEquals(own, value))
                    labels.Remove(key);
            }
        }

        foreach (var key in (string[])["Cmd", "Entrypoint", "User", "WorkingDir", "Healthcheck", "StopSignal"])
        {
            if (body[key] is { } own && JsonNode.DeepEquals(own, imageConfig[key]))
                body.Remove(key);
        }

        foreach (var key in (string[])["ExposedPorts", "Volumes"])
        {
            if (body[key] is JsonObject own && imageConfig[key] is JsonObject image)
            {
                foreach (var (name, _) in image)
                    own.Remove(name);
                if (own.Count == 0)
                    body.Remove(key);
            }
        }
    }

    /// <summary>
    /// Mounts the volumes the engine made for the container itself (an image's <c>VOLUME</c> that
    /// nothing was mounted on) into the new container by name. Without this the new container
    /// gets empty ones, which for the agent would lose its identity.
    /// </summary>
    public static void KeepAnonymousVolumes(JsonObject body, JsonObject inspect)
    {
        if (inspect["Mounts"] is not JsonArray mounts)
            return;
        var host = body["HostConfig"] as JsonObject ?? (JsonObject)(body["HostConfig"] = new JsonObject());
        var binds = host["Binds"] as JsonArray;
        HashSet<string> configured = new(StringComparer.Ordinal);
        foreach (var bind in binds?.Select(b => b?.GetValue<string>()).OfType<string>() ?? [])
        {
            var parts = bind.Split(':');
            if (parts.Length >= 2)
                configured.Add(parts[1]);
        }
        foreach (var mount in (host["Mounts"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (mount["Target"]?.GetValue<string>() is { } target)
                configured.Add(target);
        }
        foreach (var mount in mounts.OfType<JsonObject>())
        {
            if (mount["Type"]?.GetValue<string>() != "volume" || mount["Name"]?.GetValue<string>() is not { Length: > 0 } name
                || mount["Destination"]?.GetValue<string>() is not { Length: > 0 } destination || configured.Contains(destination))
                continue;
            binds ??= (JsonArray)(host["Binds"] = new JsonArray());
            binds.Add((JsonNode)$"{name}:{destination}{(mount["RW"]?.GetValue<bool>() == false ? ":ro" : "")}");
        }
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
}
