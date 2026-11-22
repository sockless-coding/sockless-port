using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Registries;

namespace Sockless.Port.Swarm;

/// <summary>A service written to the swarm, and what the engine or Sockless Port warned about.</summary>
public sealed record ServiceWriteResult(string Id, IReadOnlyList<string> Warnings);

/// <summary>A service change was refused before reaching the engine; the message is for people.</summary>
public sealed class ServiceRejectedException(string message) : Exception(message);

/// <summary>
/// Creates and changes services the way <c>docker service create/update</c> does: the image is
/// pinned to the digest its tag points at now, so every node runs the same image, and the stored
/// registry credentials go along so nodes can pull private images.
/// </summary>
public sealed class SwarmServices(RegistryCredentials registries, ILogger<SwarmServices> logger)
{
    public async Task<ServiceWriteResult> CreateAsync(DockerClient client, JsonObject spec, CancellationToken cancellationToken = default)
    {
        var (auth, warnings) = await PinImageAsync(client, spec, cancellationToken);
        var created = await client.CreateServiceAsync(spec, auth, cancellationToken);
        return new ServiceWriteResult(created.Id, [.. warnings, .. created.Warnings ?? []]);
    }

    /// <summary>Replaces the service's spec with <paramref name="spec"/>, based on the version in <paramref name="service"/>.</summary>
    public async Task<ServiceWriteResult> UpdateAsync(DockerClient client, SwarmService service, JsonObject spec, CancellationToken cancellationToken = default)
    {
        var (auth, warnings) = await PinImageAsync(client, spec, cancellationToken);
        var updated = await client.UpdateServiceAsync(service.Id, service.Version, spec, auth, cancellationToken: cancellationToken);
        return new ServiceWriteResult(service.Id, [.. warnings, .. updated.Warnings ?? []]);
    }

    /// <exception cref="ServiceRejectedException">The service is global or a job.</exception>
    public async Task<ServiceWriteResult> ScaleAsync(DockerClient client, string id, ulong replicas, CancellationToken cancellationToken = default)
    {
        var (service, spec) = await InspectAsync(client, id, cancellationToken);
        if (ServiceSpecBuilder.ModeOf(spec) != "replicated")
            throw new ServiceRejectedException($"{service.Name} runs one task per node (global), so it can't be scaled.");
        var updated = await client.UpdateServiceAsync(service.Id, service.Version, ServiceSpecBuilder.Scale(spec, replicas), cancellationToken: cancellationToken);
        return new ServiceWriteResult(service.Id, updated.Warnings ?? []);
    }

    /// <summary>Replaces every task, as its update config says.</summary>
    /// <param name="pull">Resolve the image's tag again first, so tasks start from what the tag points at now.</param>
    public async Task<ServiceWriteResult> RedeployAsync(DockerClient client, string id, bool pull, CancellationToken cancellationToken = default)
    {
        var (service, spec) = await InspectAsync(client, id, cancellationToken);
        var forced = ServiceSpecBuilder.ForceUpdate(spec);
        List<string> warnings = [];
        string? auth = null;
        if (pull && forced["TaskTemplate"]?["ContainerSpec"] is JsonObject container && container["Image"]?.GetValue<string>() is { } image)
        {
            container["Image"] = ServiceSpec.ImageWithoutDigest(image);
            (auth, warnings) = await PinImageAsync(client, forced, cancellationToken);
        }
        var updated = await client.UpdateServiceAsync(service.Id, service.Version, forced, auth, cancellationToken: cancellationToken);
        return new ServiceWriteResult(service.Id, [.. warnings, .. updated.Warnings ?? []]);
    }

    /// <exception cref="ServiceRejectedException">There's no earlier spec to go back to.</exception>
    public async Task<ServiceWriteResult> RollbackAsync(DockerClient client, string id, CancellationToken cancellationToken = default)
    {
        var (service, spec) = await InspectAsync(client, id, cancellationToken);
        if (service.PreviousSpec is null)
            throw new ServiceRejectedException($"{service.Name} hasn't been updated since it was created, so there's nothing to roll back to.");
        var updated = await client.UpdateServiceAsync(service.Id, service.Version, spec, rollback: true, cancellationToken: cancellationToken);
        return new ServiceWriteResult(service.Id, updated.Warnings ?? []);
    }

    private static async Task<(SwarmService Service, JsonObject Spec)> InspectAsync(DockerClient client, string id, CancellationToken cancellationToken)
    {
        var (service, json) = await client.InspectServiceAsync(id, cancellationToken);
        return (service, JsonNode.Parse(json)?["Spec"]?.DeepClone() as JsonObject ?? new JsonObject());
    }

    /// <summary>
    /// Pins <c>ContainerSpec.Image</c> to a digest when it names only a tag. When the registry
    /// can't be asked, the tag is kept and a warning says nodes may end up with different images.
    /// </summary>
    internal async Task<(string? Auth, List<string> Warnings)> PinImageAsync(DockerClient client, JsonObject spec, CancellationToken cancellationToken)
    {
        List<string> warnings = [];
        if (spec["TaskTemplate"]?["ContainerSpec"] is not JsonObject container || container["Image"]?.GetValue<string>() is not { Length: > 0 } image)
            return (null, warnings);

        var auth = await registries.AuthHeaderForAsync(image, cancellationToken);
        if (image.Contains('@', StringComparison.Ordinal))
            return (auth, warnings);
        try
        {
            var distribution = await client.InspectDistributionAsync(image, auth, cancellationToken);
            container["Image"] = $"{image}@{distribution.Descriptor.Digest}";
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Couldn't resolve the digest of {Image}", image);
            warnings.Add($"Couldn't ask the registry which image {image} is ({ex.Message.TrimEnd('.')}). Each node pulls the tag itself, so nodes may end up running different versions.");
        }
        return (auth, warnings);
    }
}
