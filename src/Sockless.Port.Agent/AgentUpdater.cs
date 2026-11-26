using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Agent;

/// <summary>
/// <c>sockless-port-agent update --target {id} --image {ref}</c>: replaces the agent's container
/// with one from the new image, on the agent's host (see <see cref="AgentUpdate"/>). Runs in a
/// helper container the server starts from the new image, with the engine's socket; prints what it
/// does for the server to read afterwards, and exits with one of <see cref="AgentUpdate"/>'s codes.
/// </summary>
internal static class AgentUpdater
{
    /// <summary>How long the new agent has to report healthy (its health check starts after 10 s, every 30 s).</summary>
    internal static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Without a health check, running this long counts.</summary>
    internal static readonly TimeSpan RunningIsEnough = TimeSpan.FromSeconds(15);

    /// <summary>Lets the server's request that started the helper finish before the agent stops.</summary>
    internal static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(3);

    public static async Task<int> RunAsync(string[] args)
    {
        string? target = null, image = null;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--target": target = args[i + 1]; break;
                case "--image": image = args[i + 1]; break;
            }
        }
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(image))
        {
            await Console.Out.WriteLineAsync("Usage: sockless-port-agent update --target <container> --image <reference>. Sockless Port starts this itself.");
            return AgentUpdate.Refused;
        }

        DockerAddress engine;
        try
        {
            engine = AgentOptions.From(new ConfigurationBuilder().AddEnvironmentVariables().Build()).Engine;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            await Console.Out.WriteLineAsync(ex.Message);
            return AgentUpdate.Refused;
        }

        DockerClient client;
        try
        {
            client = await DockerClient.ConnectAsync(engine);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or DockerApiException)
        {
            await Console.Out.WriteLineAsync($"Can't reach the engine at {engine}: {ex.Message}");
            return AgentUpdate.Refused;
        }
        using (client)
            return await SwapAsync(client, target, image, Console.Out, new Timing());
    }

    /// <summary>How long to wait between steps; tests make it quick.</summary>
    internal sealed record Timing
    {
        public TimeSpan StartDelay { get; init; } = AgentUpdater.StartDelay;

        public TimeSpan HealthTimeout { get; init; } = AgentUpdater.HealthTimeout;

        public TimeSpan RunningIsEnough { get; init; } = AgentUpdater.RunningIsEnough;

        public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);
    }

    internal static async Task<int> SwapAsync(DockerClient client, string target, string image, TextWriter log, Timing timing)
    {
        ContainerInspect container;
        JsonObject inspect;
        try
        {
            (container, var json) = await client.InspectContainerAsync(target);
            inspect = JsonNode.Parse(json)!.AsObject();
            if (container.Config?.Labels is { } labels && labels.ContainsKey("com.docker.swarm.task.id"))
            {
                await log.WriteLineAsync($"{container.DisplayName} is a swarm task: update its service instead.");
                return AgentUpdate.Refused;
            }
            if (!await client.ImageExistsAsync(image))
            {
                await log.WriteLineAsync($"The image {image} isn't on this host. Sockless Port pulls it before starting the update.");
                return AgentUpdate.Refused;
            }
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        {
            await log.WriteLineAsync($"Can't read the agent's container {target}: {ex.Message}");
            return AgentUpdate.Refused;
        }

        var (body, extraNetworks) = ContainerCreateBody.FromInspect(inspect);
        ContainerCreateBody.KeepAnonymousVolumes(body, inspect);
        if (container.Image is { } oldImage)
        {
            try
            {
                var (_, imageJson) = await client.InspectImageAsync(oldImage);
                ContainerCreateBody.RemoveImageDefaults(body, JsonNode.Parse(imageJson)?["Config"] as JsonObject);
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
            {
                // The old image is gone: keep everything the container has.
                await log.WriteLineAsync($"Couldn't read the old image ({ex.Message}); keeping all of the container's settings.");
            }
        }
        body["Image"] = image;

        var name = container.DisplayName;
        var parked = $"{name}-replaced-{DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        var wasRunning = container.State?.Running == true || container.State?.Restarting == true;

        await Task.Delay(timing.StartDelay);
        await log.WriteLineAsync($"Replacing {name} ({ShortId(container.Id)}) with {image}.");

        string? newId = null;
        string? failure;
        try
        {
            if (wasRunning)
                await client.StopContainerAsync(container.Id, timeout: 10);
            await client.RenameContainerAsync(container.Id, parked);
            newId = (await client.CreateContainerAsync(name, body)).Id;
            foreach (var (network, endpoint) in extraNetworks)
                await client.ConnectNetworkAsync(network, newId, endpoint);
            await client.StartContainerAsync(newId);
            failure = await WaitHealthyAsync(client, newId, timing);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        {
            failure = ex.Message;
        }

        if (failure is null)
        {
            try
            {
                await client.RemoveContainerAsync(container.Id, force: true);
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TaskCanceledException)
            {
                await log.WriteLineAsync($"The old agent {parked} couldn't be removed ({ex.Message}); remove it by hand.");
            }
            await log.WriteLineAsync($"Updated {name} to {image} ({ShortId(newId!)}).");
            return AgentUpdate.Updated;
        }

        await log.WriteLineAsync($"The new agent didn't start: {failure.TrimEnd('.')}. Putting the old one back.");
        List<string> problems = [];
        if (newId is not null)
            await TryAsync(() => client.RemoveContainerAsync(newId, force: true), "remove the new container", problems);
        await TryAsync(() => client.RenameContainerAsync(container.Id, name), $"rename {parked} back to {name}", problems);
        if (wasRunning)
            await TryAsync(() => client.StartContainerAsync(container.Id), $"start {name}", problems);
        if (problems.Count > 0)
        {
            await log.WriteLineAsync($"Couldn't {string.Join(", or ", problems)}. Check the agent's containers on this host.");
            return AgentUpdate.Broken;
        }
        await log.WriteLineAsync($"The update to {image} failed: {failure.TrimEnd('.')}. The previous agent runs again.");
        return AgentUpdate.RolledBack;
    }

    /// <summary>Null once the container is healthy, or has run for a while without a health check; otherwise why not.</summary>
    private static async Task<string?> WaitHealthyAsync(DockerClient client, string id, Timing timing)
    {
        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            var (container, _) = await client.InspectContainerAsync(id);
            var state = container.State;
            if (state is null || (!state.Running && !state.Restarting))
                return $"it stopped with exit code {(state?.ExitCode ?? 0).ToString(CultureInfo.InvariantCulture)}{(state?.Error is { Length: > 0 } error ? $" ({error})" : "")}";
            var waited = DateTimeOffset.UtcNow - started;
            switch (state.Health?.Status)
            {
                case "healthy":
                    return null;
                case "unhealthy":
                    return $"its health check failed{(state.Health.Log?.LastOrDefault()?.Output is { Length: > 0 } output ? $": {output.Trim()}" : "")}";
                case null or "" or "none" when state.Running && waited >= timing.RunningIsEnough:
                    return null;
            }
            if (waited >= timing.HealthTimeout)
                return $"it wasn't healthy within {timing.HealthTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds";
            await Task.Delay(timing.Poll);
        }
    }

    private static async Task TryAsync(Func<Task> action, string what, List<string> problems)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TaskCanceledException)
        {
            problems.Add($"{what} ({ex.Message.TrimEnd('.')})");
        }
    }

    private static string ShortId(string id) => id.Length > 12 ? id[..12] : id;
}
