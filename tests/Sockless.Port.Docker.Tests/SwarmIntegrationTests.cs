using System.Text;
using System.Text.Json.Nodes;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Docker.Streams;

namespace Sockless.Port.Docker.Tests;

/// <summary>
/// Services, tasks, logs, secrets, configs and nodes against a real swarm manager. Skipped unless
/// <c>SOCKLESS_PORT_TEST_ENGINE</c> is a swarm manager (CI's <c>swarm</c> job).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SwarmIntegrationTests : IAsyncLifetime
{
    private const string Image = "busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private static readonly TimeSpan Converge = TimeSpan.FromSeconds(90);

    private readonly string _name = $"sp-test-{Guid.NewGuid():n}"[..20];
    private readonly List<string> _services = [];
    private readonly List<string> _secrets = [];
    private readonly List<string> _configs = [];
    private DockerClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run engine integration tests.");
        _client = await DockerClient.ConnectAsync(DockerAddress.Parse(Address!), cancellationToken: Ct);
        var engine = EngineDescriptor.Detect(_client.ServerVersion, await _client.GetInfoAsync(Ct), _client.ApiVersion);
        Assert.SkipUnless(engine.Has(EngineCapabilities.SwarmManager), "The test engine isn't a swarm manager.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        foreach (var id in _services)
        {
            try { await _client.RemoveServiceAsync(id); } catch (DockerApiException) { }
        }
        // Secrets and configs can only go once their services' tasks are gone.
        for (var attempt = 0; attempt < 20 && (_secrets.Count > 0 || _configs.Count > 0); attempt++)
        {
            foreach (var id in _secrets.ToList())
            {
                try { await _client.RemoveSecretAsync(id); _secrets.Remove(id); } catch (DockerApiException ex) when (ex.IsNotFound) { _secrets.Remove(id); } catch (DockerApiException) { }
            }
            foreach (var id in _configs.ToList())
            {
                try { await _client.RemoveConfigAsync(id); _configs.Remove(id); } catch (DockerApiException ex) when (ex.IsNotFound) { _configs.Remove(id); } catch (DockerApiException) { }
            }
            if (_secrets.Count > 0 || _configs.Count > 0)
                await Task.Delay(500);
        }
        _client.Dispose();
    }

    private JsonObject Spec(string name, int replicas, params string[] command) => new()
    {
        ["Name"] = name,
        ["Labels"] = new JsonObject { ["sockless-port-test"] = "true" },
        ["TaskTemplate"] = new JsonObject
        {
            ["ContainerSpec"] = new JsonObject
            {
                ["Image"] = Image,
                ["Args"] = new JsonArray([.. command.Select(c => (JsonNode)c)]),
            },
            ["RestartPolicy"] = new JsonObject { ["Condition"] = "none" },
        },
        ["Mode"] = new JsonObject { ["Replicated"] = new JsonObject { ["Replicas"] = replicas } },
    };

    private async Task<string> CreateAsync(JsonObject spec)
    {
        var created = await _client.CreateServiceAsync(spec, cancellationToken: Ct);
        _services.Add(created.Id);
        return created.Id;
    }

    private async Task<IReadOnlyList<SwarmTask>> WaitForRunningAsync(string serviceId, int count)
    {
        var deadline = DateTime.UtcNow + Converge;
        IReadOnlyList<SwarmTask> tasks = [];
        while (DateTime.UtcNow < deadline)
        {
            tasks = await _client.ListTasksAsync(new Dictionary<string, string[]> { ["service"] = [serviceId] }, Ct);
            var running = tasks.Where(t => t.Status?.State == "running" && t.IsCurrent).ToList();
            if (running.Count == count)
                return running;
            await Task.Delay(500, Ct);
        }
        Assert.Fail($"The service didn't reach {count} running tasks: {string.Join(", ", tasks.Select(t => $"{t.Status?.State} {t.Status?.Err}"))}");
        return [];
    }

    private async Task WaitForUpdateAsync(string serviceId, params string[] states)
    {
        var deadline = DateTime.UtcNow + Converge;
        while (DateTime.UtcNow < deadline)
        {
            var (service, _) = await _client.InspectServiceAsync(serviceId, Ct);
            if (service.UpdateStatus?.State is { } state && states.Contains(state))
                return;
            await Task.Delay(500, Ct);
        }
        Assert.Fail($"The service's update didn't reach {string.Join(" or ", states)}.");
    }

    [Fact]
    public async Task A_service_scales_updates_and_rolls_back()
    {
        var id = await CreateAsync(Spec(_name, 1, "sleep", "600"));
        await WaitForRunningAsync(id, 1);

        // Scale through the raw spec, as the pages do, so unmodelled settings survive.
        var (service, json) = await _client.InspectServiceAsync(id, Ct);
        var spec = JsonNode.Parse(json)!["Spec"]!.AsObject();
        spec["Mode"]!["Replicated"]!["Replicas"] = 2;
        await _client.UpdateServiceAsync(id, service.Version, spec, cancellationToken: Ct);
        await WaitForRunningAsync(id, 2);
        var listed = (await _client.ListServicesAsync(new Dictionary<string, string[]> { ["id"] = [id] }, Ct)).Single();
        Assert.Equal(2UL, listed.ServiceStatus!.DesiredTasks);

        // An update leaves the previous spec behind, and a rollback returns to it.
        (service, json) = await _client.InspectServiceAsync(id, Ct);
        spec = JsonNode.Parse(json)!["Spec"]!.AsObject();
        spec["TaskTemplate"]!["ContainerSpec"]!["Env"] = new JsonArray("STAGE=two");
        await _client.UpdateServiceAsync(id, service.Version, spec, cancellationToken: Ct);
        await WaitForUpdateAsync(id, "completed");
        (service, json) = await _client.InspectServiceAsync(id, Ct);
        Assert.Equal(["STAGE=two"], service.Spec!.TaskTemplate!.ContainerSpec!.Env);
        Assert.NotNull(service.PreviousSpec);

        await _client.UpdateServiceAsync(id, service.Version, JsonNode.Parse(json)!["Spec"]!.AsObject(), rollback: true, cancellationToken: Ct);
        await WaitForUpdateAsync(id, "rollback_completed");
        (service, _) = await _client.InspectServiceAsync(id, Ct);
        Assert.Null(service.Spec!.TaskTemplate!.ContainerSpec!.Env);

        // An update based on an old version is refused, not applied over the newer one.
        var stale = await Assert.ThrowsAsync<DockerApiException>(() => _client.UpdateServiceAsync(id, new ObjectVersion { Index = 1 }, spec, cancellationToken: Ct));
        Assert.Contains("out of sequence", stale.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Service_logs_say_which_task_wrote_each_line()
    {
        var id = await CreateAsync(Spec(_name, 2, "sh", "-c", "echo hello from $(hostname); sleep 600"));
        var tasks = await WaitForRunningAsync(id, 2);

        var lines = new List<ServiceLogLine>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (lines.Count < 2 && DateTime.UtcNow < deadline)
        {
            lines.Clear();
            await foreach (var line in _client.StreamServiceLogsAsync(id, new LogOptions { Tail = null, Timestamps = true }, Ct))
                lines.Add(line);
            if (lines.Count < 2)
                await Task.Delay(500, Ct);
        }

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.StartsWith("hello from ", l.Line.Text));
        Assert.All(lines, l => Assert.NotNull(l.Line.Timestamp));
        Assert.Equal(tasks.Select(t => t.Id).Order(), lines.Select(l => l.TaskId!).Order());
        Assert.All(lines, l => Assert.Equal(id, l.ServiceId));
    }

    [Fact]
    public async Task Secrets_and_configs_reach_tasks_as_files_and_are_kept_while_used()
    {
        var secretId = await _client.CreateSecretAsync($"{_name}-secret", Encoding.UTF8.GetBytes("pa55"), cancellationToken: Ct);
        _secrets.Add(secretId);
        var configId = await _client.CreateConfigAsync($"{_name}-config", Encoding.UTF8.GetBytes("key=value\n"), new Dictionary<string, string> { ["team"] = "infra" }, Ct);
        _configs.Add(configId);

        var (secret, secretJson) = await _client.InspectSecretAsync(secretId, Ct);
        Assert.Equal($"{_name}-secret", secret.Name);
        Assert.DoesNotContain("pa55", secretJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("pa55")), secretJson, StringComparison.Ordinal);
        var (config, _) = await _client.InspectConfigAsync(configId, Ct);
        Assert.Equal("key=value\n", Encoding.UTF8.GetString(Convert.FromBase64String(config.Spec!.Data!)));
        Assert.Equal("infra", config.Spec.Labels!["team"]);

        var spec = Spec(_name, 1, "sh", "-c", $"cat /run/secrets/{_name}-secret /{_name}-config; sleep 600");
        var container = spec["TaskTemplate"]!["ContainerSpec"]!.AsObject();
        container["Secrets"] = new JsonArray(new JsonObject
        {
            ["File"] = new JsonObject { ["Name"] = $"{_name}-secret", ["UID"] = "0", ["GID"] = "0", ["Mode"] = 292 },
            ["SecretID"] = secretId,
            ["SecretName"] = secret.Name,
        });
        container["Configs"] = new JsonArray(new JsonObject
        {
            ["File"] = new JsonObject { ["Name"] = $"/{_name}-config", ["UID"] = "0", ["GID"] = "0", ["Mode"] = 292 },
            ["ConfigID"] = configId,
            ["ConfigName"] = config.Name,
        });
        var id = await CreateAsync(spec);
        await WaitForRunningAsync(id, 1);

        var output = new StringBuilder();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!output.ToString().Contains("key=value", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            output.Clear();
            await foreach (var line in _client.StreamServiceLogsAsync(id, new LogOptions { Tail = null }, Ct))
                output.AppendLine(line.Line.Text);
            await Task.Delay(300, Ct);
        }
        Assert.Contains("pa55", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("key=value", output.ToString(), StringComparison.Ordinal);

        var inUse = await Assert.ThrowsAsync<DockerApiException>(() => _client.RemoveSecretAsync(secretId, Ct));
        Assert.Contains("in use", inUse.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nodes_take_labels_and_the_cluster_rotates_tokens()
    {
        var node = (await _client.ListNodesAsync(Ct)).First(n => n.IsLeader);
        var label = $"sockless-port-test-{_name}";
        var original = node.Spec!;
        try
        {
            var labels = new Dictionary<string, string>(original.Labels ?? new Dictionary<string, string>()) { [label] = "yes" };
            await _client.UpdateNodeAsync(node.Id, node.Version, original with { Labels = labels }, Ct);
            var (updated, json) = await _client.InspectNodeAsync(node.Id, Ct);
            Assert.Equal("yes", updated.Spec!.Labels![label]);
            Assert.Equal(original.Availability, updated.Spec.Availability);
            Assert.Contains(label, json, StringComparison.Ordinal);
        }
        finally
        {
            var (current, _) = await _client.InspectNodeAsync(node.Id, Ct);
            var labels = new Dictionary<string, string>(current.Spec!.Labels ?? new Dictionary<string, string>());
            labels.Remove(label);
            await _client.UpdateNodeAsync(current.Id, current.Version, current.Spec with { Labels = labels }, Ct);
        }

        var before = (await _client.InspectSwarmAsync(Ct)).Swarm.JoinTokens!;
        await _client.RotateJoinTokenAsync(SwarmJoinRole.Worker, Ct);
        var after = (await _client.InspectSwarmAsync(Ct)).Swarm.JoinTokens!;
        Assert.NotEqual(before.Worker, after.Worker);
        Assert.Equal(before.Manager, after.Manager);
    }
}
