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

    /// <summary>Turns inspect output into a create body; see <see cref="ContainerCreateBody.FromInspect"/>.</summary>
    internal static (JsonObject Body, List<(string Network, JsonObject Endpoint)> ExtraNetworks) BuildCreateBody(JsonObject inspect) =>
        ContainerCreateBody.FromInspect(inspect);

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
