using System.Net;
using System.Text.Json.Nodes;
using Sockless.Port.Docker.Models;
using Sockless.Port.Docker.Streams;

namespace Sockless.Port.Docker.Tests;

/// <summary>Swarm models against captured responses, and the requests the client sends.</summary>
public sealed class SwarmUnitTests
{
    private static readonly DockerJsonContext Json = DockerJsonContext.Default;

    private readonly Dictionary<HttpRequestMessage, string> _bodies = [];

    private FakeEngineHandler Engine(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(r =>
        {
            if (r.Content is { } content)
                _bodies[r] = content.ReadAsStringAsync().GetAwaiter().GetResult();
            return r.RequestUri!.AbsolutePath == "/version" ? FakeEngineHandler.Version("1.51") : respond(r);
        });

    // ---------------------------------------------------------------- models

    [Fact]
    public void The_cluster_and_its_join_tokens_are_read()
    {
        var swarm = Fixture.Load("swarm-inspect.json", Json.SwarmInspect);

        Assert.Equal("scstdilu42ov4xdbcxl1m6x1p", swarm.Id);
        Assert.True(swarm.Version!.Index > 0);
        Assert.StartsWith("SWMTKN-1-", swarm.JoinTokens!.Worker);
        Assert.StartsWith("SWMTKN-1-", swarm.JoinTokens.Manager);
        Assert.NotEqual(swarm.JoinTokens.Worker, swarm.JoinTokens.Manager);
        Assert.Equal(4789, swarm.DataPathPort);
        Assert.False(swarm.Spec!.EncryptionConfig!.AutoLockManagers);
    }

    [Fact]
    public void Nodes_are_read_with_role_resources_and_manager_status()
    {
        var node = Assert.Single(Fixture.Load("swarm-nodes.json", Json.ListSwarmNode));

        Assert.True(node.IsManager);
        Assert.True(node.IsLeader);
        Assert.True(node.IsReady);
        Assert.Equal("active", node.Spec!.Availability);
        Assert.Equal(node.Description!.Hostname, node.DisplayName);
        Assert.Equal(16_000_000_000, node.Description.Resources!.NanoCpus);
        Assert.Equal("linux", node.Description.Platform!.Os);
        Assert.Equal("29.10.0", node.Description.Engine!.EngineVersion);
        Assert.EndsWith(":2377", node.ManagerStatus!.Addr);
        Assert.Equal("reachable", node.ManagerStatus.Reachability);
    }

    [Fact]
    public void Services_are_read_with_mode_stack_and_task_counts()
    {
        var services = Fixture.Load("swarm-services.json", Json.ListSwarmService).ToDictionary(s => s.Name);

        Assert.Equal(["agentish", "broken", "demo_api", "demo_worker", "web"], services.Keys.Order());
        Assert.True(services["agentish"].IsGlobal);
        Assert.False(services["web"].IsGlobal);
        Assert.Equal(2UL, services["web"].Spec!.Mode!.Replicated!.Replicas);
        Assert.Equal("demo", services["demo_api"].Stack);
        Assert.Null(services["web"].Stack);
        Assert.Equal(2UL, services["web"].ServiceStatus!.RunningTasks);
        Assert.Equal(0UL, services["broken"].ServiceStatus!.RunningTasks);
        Assert.Equal(1UL, services["broken"].ServiceStatus!.DesiredTasks);
        // Docker pins the image to a digest; the tag is what people know it by.
        Assert.StartsWith("busybox:1.37@sha256:", services["web"].Spec!.TaskTemplate!.ContainerSpec!.Image);
        Assert.Equal("busybox:1.37", services["web"].Image);
    }

    [Fact]
    public void A_service_inspect_carries_its_whole_spec_and_the_previous_one()
    {
        var service = Fixture.Load("swarm-service-inspect.json", Json.SwarmService);
        var spec = service.Spec!;
        var task = spec.TaskTemplate!;
        var container = task.ContainerSpec!;

        Assert.NotNull(service.PreviousSpec);
        Assert.Equal("completed", service.UpdateStatus!.State);
        Assert.Contains("FOO=bar", container.Env!);
        Assert.Equal(["sh", "-c"], container.Args!.Take(2));
        var mount = Assert.Single(container.Mounts!);
        Assert.Equal(("volume", "webdata", "/data"), (mount.Type, mount.Source, mount.Target));
        var secret = Assert.Single(container.Secrets!);
        Assert.Equal(("db_password", "db_password", 292), (secret.SecretName, secret.File!.Name, secret.File.Mode));
        Assert.Equal("/etc/app.conf", Assert.Single(container.Configs!).File!.Name);
        Assert.Equal(["node.role==manager"], task.Placement!.Constraints);
        Assert.Equal("node.labels.zone", Assert.Single(task.Placement.Preferences!).Spread!.SpreadDescriptor);
        Assert.Equal(64L * 1024 * 1024, task.Resources!.Limits!.MemoryBytes);
        Assert.Equal(100_000_000, task.Resources.Reservations!.NanoCpus);
        Assert.Equal(5_000_000_000, spec.UpdateConfig!.Delay);
        var port = Assert.Single(service.Endpoint!.Ports!);
        Assert.Equal((8080, 80, "ingress"), (port.PublishedPort, port.TargetPort, port.PublishMode));
        Assert.NotEmpty(Assert.Single(task.Networks!).Target!);
    }

    [Fact]
    public void Tasks_are_read_with_slot_node_and_container()
    {
        var tasks = Fixture.Load("swarm-tasks.json", Json.ListSwarmTask);

        Assert.Equal(6, tasks.Count);
        Assert.All(tasks, t => Assert.True(t.IsCurrent));
        Assert.All(tasks, t => Assert.Equal("running", t.Status!.State));
        Assert.All(tasks, t => Assert.NotNull(t.ContainerId));
        // Global services' tasks have no slot.
        Assert.Equal(2, tasks.Count(t => t.Slot is null));
        Assert.All(tasks, t => Assert.Equal("busybox:1.37", t.Image));
    }

    [Fact]
    public void Rejected_tasks_say_why()
    {
        var tasks = Fixture.Load("swarm-tasks-rejected.json", Json.ListSwarmTask);

        Assert.All(tasks, t => Assert.Equal("rejected", t.Status!.State));
        Assert.All(tasks, t => Assert.Null(t.ContainerId));
        Assert.Equal("invalid mount target, must be an absolute path: relative/path", tasks[0].Status!.Err);
        // The newest keeps being retried; the older ones are history.
        Assert.Single(tasks, t => t.IsCurrent);
    }

    [Fact]
    public void Secrets_have_no_value_and_configs_do()
    {
        var secret = Assert.Single(Fixture.Load("swarm-secrets.json", Json.ListSwarmSecret));
        var config = Assert.Single(Fixture.Load("swarm-configs.json", Json.ListSwarmConfig));

        Assert.Equal("db_password", secret.Name);
        Assert.Null(secret.Spec!.Data);
        Assert.Equal("app_conf", config.Name);
        Assert.Equal("hello=world\n", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(config.Spec!.Data!)));
    }

    [Fact]
    public async Task Service_log_lines_carry_their_task_and_node()
    {
        await using var stream = File.OpenRead(Fixture.Path("swarm-service-logs.bin"));
        var lines = new List<ServiceLogLine>();
        await foreach (var line in LogReader.ReadLinesAsync(stream, tty: false, timestamps: true, TestContext.Current.CancellationToken))
            lines.Add(ServiceLogLine.From(line));

        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.NotNull(l.TaskId));
        Assert.All(lines, l => Assert.Equal("o2eawpc0il5su5cfljwi3jhqr", l.NodeId));
        Assert.All(lines, l => Assert.Equal("w8rzaq2zbkwkqixivpijwbrau", l.ServiceId));
        Assert.All(lines, l => Assert.StartsWith("hello from ", l.Line.Text));
        Assert.All(lines, l => Assert.NotNull(l.Line.Timestamp));
    }

    [Theory]
    [InlineData("plain line", null, "plain line")]
    [InlineData("com.docker.swarm.node.id=n1,com.docker.swarm.service.id=s1,com.docker.swarm.task.id=t1 hello world", "t1", "hello world")]
    [InlineData("com.docker.swarm.task.id=t%2C1,extra=a+b message", "t,1", "message")]
    [InlineData("com.docker.swarm.task.id=t1", "t1", "")]
    public void Log_details_are_split_from_the_message(string text, string? task, string message)
    {
        var line = ServiceLogLine.From(new LogLine(StreamKind.Stdout, text, null));

        Assert.Equal(task, line.TaskId);
        Assert.Equal(message, line.Line.Text);
    }

    [Theory]
    [InlineData("nginx:1.27@sha256:abc", "nginx:1.27")]
    [InlineData("nginx:1.27", "nginx:1.27")]
    [InlineData("ghcr.io/org/app@sha256:abc", "ghcr.io/org/app")]
    [InlineData(null, null)]
    public void Images_read_without_their_digest(string? image, string? expected) =>
        Assert.Equal(expected, ServiceSpec.ImageWithoutDigest(image));

    // ---------------------------------------------------------------- requests

    [Fact]
    public async Task Services_are_listed_with_their_task_counts()
    {
        var handler = Engine(_ => FakeEngineHandler.Json("[]"));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        await client.ListServicesAsync(new Dictionary<string, string[]> { ["label"] = ["com.docker.stack.namespace=web"] }, TestContext.Current.CancellationToken);

        var query = Uri.UnescapeDataString(handler.Requests[1].RequestUri!.Query);
        Assert.Contains("status=true", query);
        Assert.Contains("""filters={"label":["com.docker.stack.namespace=web"]}""", query);
    }

    [Fact]
    public async Task Updates_name_the_version_they_start_from()
    {
        var handler = Engine(_ => FakeEngineHandler.Json("{}"));
        using var client = await FakeEngineHandler.ConnectAsync(handler);
        var ct = TestContext.Current.CancellationToken;

        await client.UpdateNodeAsync("n1", new ObjectVersion { Index = 151 }, new NodeSpec { Role = "worker", Availability = "drain", Labels = new Dictionary<string, string> { ["zone"] = "a" } }, ct);
        await client.UpdateServiceAsync("s1", new ObjectVersion { Index = 42 }, new JsonObject { ["Name"] = "web" }, registryAuth: "abc", rollback: true, cancellationToken: ct);

        var node = handler.Requests[1];
        Assert.Equal("/v1.51/nodes/n1/update?version=151", node.RequestUri!.PathAndQuery);
        var nodeBody = JsonNode.Parse(_bodies[node])!;
        Assert.Equal(("worker", "drain", "a"), (nodeBody["Role"]!.GetValue<string>(), nodeBody["Availability"]!.GetValue<string>(), nodeBody["Labels"]!["zone"]!.GetValue<string>()));

        var service = handler.Requests[2];
        Assert.Equal("/v1.51/services/s1/update?version=42&rollback=previous", service.RequestUri!.PathAndQuery);
        Assert.Equal("abc", service.Headers.GetValues("X-Registry-Auth").Single());
        Assert.Equal("web", JsonNode.Parse(_bodies[service])!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rotating_a_token_sends_the_current_spec_back()
    {
        var handler = Engine(r => r.RequestUri!.AbsolutePath.EndsWith("/swarm", StringComparison.Ordinal)
            ? FakeEngineHandler.Json(Fixture.Read("swarm-inspect.json"))
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        await client.RotateJoinTokenAsync(SwarmJoinRole.Manager, TestContext.Current.CancellationToken);

        var update = handler.Requests[2];
        Assert.Equal(HttpMethod.Post, update.Method);
        Assert.StartsWith("/v1.51/swarm/update?version=", update.RequestUri!.PathAndQuery);
        Assert.EndsWith("&rotateManagerToken=true", update.RequestUri.PathAndQuery);
        Assert.Equal("default", JsonNode.Parse(_bodies[update])!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Secrets_are_created_from_bytes_as_base64()
    {
        var handler = Engine(_ => FakeEngineHandler.Json("""{"ID":"sec1"}""", HttpStatusCode.Created));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        var id = await client.CreateSecretAsync("db", "pa55\n"u8.ToArray(), new Dictionary<string, string> { ["team"] = "infra" }, TestContext.Current.CancellationToken);

        Assert.Equal("sec1", id);
        var body = JsonNode.Parse(_bodies[handler.Requests[1]])!;
        Assert.Equal("db", body["Name"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String("pa55\n"u8.ToArray()), body["Data"]!.GetValue<string>());
        Assert.Equal("infra", body["Labels"]!["team"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_worker_answers_that_it_is_not_a_manager()
    {
        var handler = Engine(_ => FakeEngineHandler.Json(
            """{"message":"This node is not a swarm manager. Worker nodes can't be used to view or modify cluster state. Please run this command on a manager node or promote the current node to a manager."}""",
            HttpStatusCode.ServiceUnavailable));
        using var client = await FakeEngineHandler.ConnectAsync(handler);

        var error = await Assert.ThrowsAsync<DockerApiException>(() => client.ListNodesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.StartsWith("This node is not a swarm manager", error.Message);
    }
}
