using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Sockless.Port.Docker.Models;
using Sockless.Port.Docker.Streams;

namespace Sockless.Port.Docker;

/// <summary>Which join token <see cref="DockerClient.RotateJoinTokenAsync"/> replaces.</summary>
public enum SwarmJoinRole
{
    Worker,
    Manager,
}

/// <summary>
/// Swarm calls. They work on managers only; a worker answers 503 ("This node is not a swarm
/// manager"). Every update names the object's version, so a change made meanwhile by someone else
/// fails with a conflict instead of being overwritten.
/// </summary>
public sealed partial class DockerClient
{
    // ---------------------------------------------------------------- cluster

    /// <summary>The cluster, with its join tokens. Returns the typed view and the engine's JSON, verbatim.</summary>
    public Task<(SwarmInspect Swarm, string Json)> InspectSwarmAsync(CancellationToken cancellationToken = default) =>
        InspectRawAsync("/swarm", Json.SwarmInspect, "the swarm", cancellationToken);

    /// <summary>Replaces a join token: nodes that already joined stay, the old token stops working.</summary>
    public async Task RotateJoinTokenAsync(SwarmJoinRole role, CancellationToken cancellationToken = default)
    {
        var (swarm, json) = await InspectSwarmAsync(cancellationToken);
        var spec = JsonNode.Parse(json)?["Spec"]?.DeepClone() ?? new JsonObject();
        var flag = role == SwarmJoinRole.Worker ? "rotateWorkerToken" : "rotateManagerToken";
        await SendAsync(HttpMethod.Post, Path("/swarm/update", Query(("version", Version(swarm.Version)), (flag, "true"))),
            JsonBody(spec), cancellationToken);
    }

    // ---------------------------------------------------------------- nodes

    public async Task<IReadOnlyList<SwarmNode>> ListNodesAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("/nodes", Json.ListSwarmNode, cancellationToken);

    public Task<(SwarmNode Node, string Json)> InspectNodeAsync(string id, CancellationToken cancellationToken = default) =>
        InspectRawAsync($"/nodes/{Escape(id)}", Json.SwarmNode, $"node {id}", cancellationToken);

    /// <summary>Replaces the node's spec (name, labels, role, availability).</summary>
    /// <param name="version">The version the change is based on, from the node's inspect.</param>
    public Task UpdateNodeAsync(string id, ObjectVersion? version, NodeSpec spec, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Path($"/nodes/{Escape(id)}/update", Query(("version", Version(version)))),
            JsonContent.Create(spec, Json.NodeSpec), cancellationToken);

    /// <summary>Removes a node from the swarm. Without <paramref name="force"/>, the engine refuses a node that is still up.</summary>
    public Task RemoveNodeAsync(string id, bool force = false, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, Path($"/nodes/{Escape(id)}", Query(("force", force ? "true" : null))), content: null, cancellationToken);

    // ---------------------------------------------------------------- services

    /// <summary>Every service, with its running and desired task counts.</summary>
    public async Task<IReadOnlyList<SwarmService>> ListServicesAsync(IReadOnlyDictionary<string, string[]>? filters = null, CancellationToken cancellationToken = default) =>
        await GetAsync(Path("/services", Query(("status", "true"), ("filters", Filters(filters)))), Json.ListSwarmService, cancellationToken);

    /// <summary>Inspects a service by ID or name. The JSON is what an update starts from, so unmodelled settings survive.</summary>
    public Task<(SwarmService Service, string Json)> InspectServiceAsync(string idOrName, CancellationToken cancellationToken = default) =>
        InspectRawAsync($"/services/{Escape(idOrName)}", Json.SwarmService, $"service {idOrName}", cancellationToken);

    /// <param name="spec">A service spec, as <c>Spec</c> in a service's inspect.</param>
    /// <param name="registryAuth">An <c>X-Registry-Auth</c> value (<see cref="RegistryAuth.Encode"/>) for a private image; nodes pull with it.</param>
    public async Task<ServiceCreateResponse> CreateServiceAsync(JsonObject spec, string? registryAuth = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithAuthAsync(HttpMethod.Post, "/services/create", spec, registryAuth, cancellationToken);
        return await response.Content.ReadFromJsonAsync(Json.ServiceCreateResponse, cancellationToken)
               ?? throw new InvalidDataException("The engine returned an empty response for /services/create.");
    }

    /// <summary>Replaces a service's spec. Tasks are replaced as its update config says.</summary>
    /// <param name="version">The version the change is based on, from the service's inspect.</param>
    /// <param name="rollback">Return to the previous spec instead (<paramref name="spec"/> is then the current one, unchanged).</param>
    public async Task<ServiceUpdateResponse> UpdateServiceAsync(string id, ObjectVersion? version, JsonObject spec, string? registryAuth = null,
        bool rollback = false, CancellationToken cancellationToken = default)
    {
        var path = Path($"/services/{Escape(id)}/update", Query(("version", Version(version)), ("rollback", rollback ? "previous" : null)));
        using var response = await SendWithAuthAsync(HttpMethod.Post, path, spec, registryAuth, cancellationToken);
        return await response.Content.ReadFromJsonAsync(Json.ServiceUpdateResponse, cancellationToken) ?? new ServiceUpdateResponse();
    }

    /// <summary>Removes a service; its tasks stop on every node.</summary>
    public Task RemoveServiceAsync(string idOrName, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"/services/{Escape(idOrName)}", content: null, cancellationToken);

    /// <summary>
    /// The logs of every task of a service, interleaved as nodes send them. Each line says which
    /// task and node it came from (<see cref="ServiceLogLine"/>).
    /// </summary>
    public async IAsyncEnumerable<ServiceLogLine> StreamServiceLogsAsync(string id, LogOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = Query(
            ("stdout", "true"),
            ("stderr", "true"),
            ("details", "true"),
            ("follow", options.Follow ? "true" : null),
            ("timestamps", options.Timestamps ? "true" : null),
            ("tail", options.Tail is { } tail ? tail.ToString(CultureInfo.InvariantCulture) : "all"));

        using var response = await SendCoreAsync(HttpMethod.Get, Path($"/services/{Escape(id)}/logs", query), content: null,
            cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var line in LogReader.ReadLinesAsync(stream, options.Tty, options.Timestamps, cancellationToken))
            yield return ServiceLogLine.From(line);
    }

    // ---------------------------------------------------------------- tasks

    /// <summary>Tasks matching engine filters, e.g. <c>{"service": ["web"]}</c>, <c>{"node": [id]}</c>, <c>{"desired-state": ["running"]}</c>.</summary>
    public async Task<IReadOnlyList<SwarmTask>> ListTasksAsync(IReadOnlyDictionary<string, string[]>? filters = null, CancellationToken cancellationToken = default) =>
        await GetAsync(Path("/tasks", Query(("filters", Filters(filters)))), Json.ListSwarmTask, cancellationToken);

    // ---------------------------------------------------------------- secrets and configs

    public async Task<IReadOnlyList<SwarmSecret>> ListSecretsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("/secrets", Json.ListSwarmSecret, cancellationToken);

    public Task<(SwarmSecret Secret, string Json)> InspectSecretAsync(string idOrName, CancellationToken cancellationToken = default) =>
        InspectRawAsync($"/secrets/{Escape(idOrName)}", Json.SwarmSecret, $"secret {idOrName}", cancellationToken);

    /// <returns>The new secret's ID.</returns>
    public async Task<string> CreateSecretAsync(string name, ReadOnlyMemory<byte> value, IReadOnlyDictionary<string, string>? labels = null, CancellationToken cancellationToken = default) =>
        (await SendAsync(HttpMethod.Post, "/secrets/create", JsonContent.Create(ObjectRequest(name, value, labels), Json.SwarmObjectCreateRequest),
            Json.IdResponse, cancellationToken)).Id;

    /// <summary>Removes a secret. The engine refuses while a service uses it.</summary>
    public Task RemoveSecretAsync(string idOrName, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"/secrets/{Escape(idOrName)}", content: null, cancellationToken);

    public async Task<IReadOnlyList<SwarmConfig>> ListConfigsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("/configs", Json.ListSwarmConfig, cancellationToken);

    public Task<(SwarmConfig Config, string Json)> InspectConfigAsync(string idOrName, CancellationToken cancellationToken = default) =>
        InspectRawAsync($"/configs/{Escape(idOrName)}", Json.SwarmConfig, $"config {idOrName}", cancellationToken);

    /// <returns>The new config's ID.</returns>
    public async Task<string> CreateConfigAsync(string name, ReadOnlyMemory<byte> value, IReadOnlyDictionary<string, string>? labels = null, CancellationToken cancellationToken = default) =>
        (await SendAsync(HttpMethod.Post, "/configs/create", JsonContent.Create(ObjectRequest(name, value, labels), Json.SwarmObjectCreateRequest),
            Json.IdResponse, cancellationToken)).Id;

    /// <summary>Removes a config. The engine refuses while a service uses it.</summary>
    public Task RemoveConfigAsync(string idOrName, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"/configs/{Escape(idOrName)}", content: null, cancellationToken);

    // ---------------------------------------------------------------- helpers

    private async Task<(T Value, string Json)> InspectRawAsync<T>(string path, JsonTypeInfo<T> typeInfo, string what, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);
        using var response = await SendCoreAsync(HttpMethod.Get, path, content: null, cts.Token);
        var json = await response.Content.ReadAsStringAsync(cts.Token);
        var value = JsonSerializer.Deserialize(json, typeInfo) ?? throw new InvalidDataException($"The engine returned an empty inspect for {what}.");
        return (value, json);
    }

    private async Task<HttpResponseMessage> SendWithAuthAsync(HttpMethod method, string pathAndQuery, JsonNode body, string? registryAuth, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);
        using var request = new HttpRequestMessage(method, pathAndQuery.StartsWith(_prefix, StringComparison.Ordinal) ? pathAndQuery : _prefix + pathAndQuery)
        {
            Content = JsonBody(body),
        };
        if (registryAuth is not null)
            request.Headers.Add("X-Registry-Auth", registryAuth);
        var response = await _http.SendAsync(request, cts.Token);
        try
        {
            await EnsureSuccessAsync(response, StripQuery(pathAndQuery), cts.Token);
            await response.Content.LoadIntoBufferAsync(cts.Token);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static StringContent JsonBody(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static string Version(ObjectVersion? version) => (version?.Index ?? 0).ToString(CultureInfo.InvariantCulture);

    private static SwarmObjectCreateRequest ObjectRequest(string name, ReadOnlyMemory<byte> value, IReadOnlyDictionary<string, string>? labels) =>
        new() { Name = name, Data = Convert.ToBase64String(value.Span), Labels = labels is { Count: > 0 } ? labels : null };
}
