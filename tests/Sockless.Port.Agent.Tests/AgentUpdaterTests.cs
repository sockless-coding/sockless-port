using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;

namespace Sockless.Port.Agent.Tests;

/// <summary>The update helper's swap, against an engine kept in memory.</summary>
public sealed class AgentUpdaterTests
{
    private const string NewImage = "ghcr.io/sockless-coding/sockless-port-agent:2026.12.0";
    private const string OldId = "0123456789ab0123456789ab0123456789ab0123456789ab0123456789ab0123";

    private static readonly AgentUpdater.Timing Quick = new()
    {
        StartDelay = TimeSpan.Zero,
        Poll = TimeSpan.FromMilliseconds(5),
        RunningIsEnough = TimeSpan.FromMilliseconds(20),
        HealthTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task The_agent_is_replaced_with_its_settings_and_its_volume()
    {
        var engine = new MemoryEngine { NewContainerHealth = "healthy" };
        engine.AddAgent();
        using var client = await engine.ConnectAsync();
        var log = new StringWriter();

        var code = await AgentUpdater.SwapAsync(client, OldId, NewImage, log, Quick);

        Assert.Equal(AgentUpdate.Updated, code);
        var (id, agent) = Assert.Single(engine.Containers);
        Assert.NotEqual(OldId, id);
        Assert.Equal("/sockless-port-agent", agent["Name"]!.GetValue<string>());
        var created = engine.Created.Single();
        Assert.Equal(NewImage, created["Image"]!.GetValue<string>());
        // The image's own environment and health check come from the new image; what the install command set stays.
        Assert.Equal(["AGENT_SERVER=https://port.example.com"], created["Env"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.False(created.ContainsKey("Healthcheck"));
        Assert.Equal(["/var/run/docker.sock:/var/run/docker.sock", "sockless-port-agent:/data"],
            created["HostConfig"]!["Binds"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.Equal("unless-stopped", created["HostConfig"]!["RestartPolicy"]!["Name"]!.GetValue<string>());
        Assert.Contains("Updated sockless-port-agent", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_agent_that_stops_puts_the_old_one_back()
    {
        var engine = new MemoryEngine { NewContainerExits = true };
        engine.AddAgent();
        using var client = await engine.ConnectAsync();
        var log = new StringWriter();

        var code = await AgentUpdater.SwapAsync(client, OldId, NewImage, log, Quick);

        Assert.Equal(AgentUpdate.RolledBack, code);
        var (id, agent) = Assert.Single(engine.Containers);
        Assert.Equal(OldId, id);
        Assert.Equal("/sockless-port-agent", agent["Name"]!.GetValue<string>());
        Assert.True(agent["State"]!["Running"]!.GetValue<bool>());
        Assert.Contains("stopped with exit code 3", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("The previous agent runs again", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unhealthy_agent_is_rolled_back()
    {
        var engine = new MemoryEngine { NewContainerHealth = "unhealthy" };
        engine.AddAgent();
        using var client = await engine.ConnectAsync();
        var log = new StringWriter();

        var code = await AgentUpdater.SwapAsync(client, OldId, NewImage, log, Quick);

        Assert.Equal(AgentUpdate.RolledBack, code);
        Assert.Equal(OldId, Assert.Single(engine.Containers).Key);
        Assert.Contains("health check failed", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_changes_without_the_new_image_or_for_a_swarm_task()
    {
        var missing = new MemoryEngine();
        missing.AddAgent();
        missing.Images.Remove(NewImage);
        using var missingClient = await missing.ConnectAsync();
        var swarm = new MemoryEngine();
        swarm.AddAgent(swarmTask: true);
        using var swarmClient = await swarm.ConnectAsync();

        Assert.Equal(AgentUpdate.Refused, await AgentUpdater.SwapAsync(missingClient, OldId, NewImage, TextWriter.Null, Quick));
        Assert.Equal(AgentUpdate.Refused, await AgentUpdater.SwapAsync(swarmClient, OldId, NewImage, TextWriter.Null, Quick));
        Assert.Empty(missing.Created);
        Assert.Empty(swarm.Created);
        Assert.DoesNotContain(missing.Calls, c => c.Contains("/stop", StringComparison.Ordinal));
    }

    /// <summary>Containers and images as JSON, and the calls the update makes on them.</summary>
    private sealed class MemoryEngine : HttpMessageHandler
    {
        public Dictionary<string, JsonObject> Containers { get; } = [];

        public HashSet<string> Images { get; } = ["sha256:old", NewImage];

        public List<JsonObject> Created { get; } = [];

        public List<string> Calls { get; } = [];

        public string? NewContainerHealth { get; init; }

        public bool NewContainerExits { get; init; }

        public Task<DockerClient> ConnectAsync() =>
            DockerClient.ConnectAsync(this, new Uri("http://docker/"), new UnixSocketAddress("/fake.sock"), null, TestContext.Current.CancellationToken);

        public void AddAgent(bool swarmTask = false) => Containers[OldId] = JsonNode.Parse($$"""
            {
              "Id": "{{OldId}}",
              "Name": "/sockless-port-agent",
              "Image": "sha256:old",
              "State": { "Status": "running", "Running": true, "Health": { "Status": "healthy" } },
              "Config": {
                "Hostname": "0123456789ab",
                "Image": "ghcr.io/sockless-coding/sockless-port-agent:2026.11.0",
                "Env": ["PATH=/usr/bin", "AGENT_PORT=9001", "AGENT_SERVER=https://port.example.com"],
                "Labels": { "org.opencontainers.image.title": "Sockless Port agent"{{(swarmTask ? """, "com.docker.swarm.task.id": "t1" """ : "")}} },
                "Healthcheck": { "Test": ["CMD", "/app/sockless-port-agent", "healthcheck"] },
                "Volumes": { "/data": {} }
              },
              "HostConfig": {
                "Binds": ["/var/run/docker.sock:/var/run/docker.sock", "sockless-port-agent:/data"],
                "RestartPolicy": { "Name": "unless-stopped" },
                "NetworkMode": "bridge"
              },
              "Mounts": [
                { "Type": "bind", "Source": "/var/run/docker.sock", "Destination": "/var/run/docker.sock", "RW": true },
                { "Type": "volume", "Name": "sockless-port-agent", "Destination": "/data", "RW": true }
              ],
              "NetworkSettings": { "Networks": { "bridge": { "Aliases": null } } }
            }
            """)!.AsObject();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Calls)
                Calls.Add($"{request.Method} {path}");
            if (path == "/version")
                return Json("""{"ApiVersion":"1.51"}""");
            path = path["/v1.51".Length..];

            if (path.StartsWith("/images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal))
            {
                var reference = path["/images/".Length..^"/json".Length];
                return Images.Contains(reference)
                    ? Json(ImageJson.Replace("{id}", reference, StringComparison.Ordinal))
                    : NotFound();
            }
            if (path == "/containers/create" && request.Method == HttpMethod.Post)
            {
                var created = JsonNode.Parse(body!)!.AsObject();
                Created.Add(created.DeepClone().AsObject());
                var id = Guid.NewGuid().ToString("n") + Guid.NewGuid().ToString("n");
                Containers[id] = new JsonObject
                {
                    ["Id"] = id,
                    ["Name"] = "/" + query["name"],
                    ["Image"] = "sha256:new",
                    ["State"] = new JsonObject { ["Status"] = "created", ["Running"] = false },
                    ["Config"] = created,
                };
                return Json($$"""{"Id":"{{id}}","Warnings":[]}""", HttpStatusCode.Created);
            }

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts is not ["containers", var target, ..] || Find(target) is not { } container)
                return NotFound();
            var key = container["Id"]!.GetValue<string>();
            switch (parts[2..], request.Method.Method)
            {
                case (["json"], "GET"):
                    return Json(container.ToJsonString());
                case ([], "DELETE"):
                    Containers.Remove(key);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case (["stop"], "POST"):
                    container["State"] = new JsonObject { ["Status"] = "exited", ["Running"] = false, ["ExitCode"] = 0 };
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case (["rename"], "POST"):
                    container["Name"] = "/" + query["name"];
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case (["start"], "POST"):
                    container["State"] = key == OldId
                        ? new JsonObject { ["Status"] = "running", ["Running"] = true, ["Health"] = new JsonObject { ["Status"] = "healthy" } }
                        : NewContainerExits
                            ? new JsonObject { ["Status"] = "exited", ["Running"] = false, ["ExitCode"] = 3 }
                            : new JsonObject { ["Status"] = "running", ["Running"] = true, ["Health"] = NewContainerHealth is null ? null : new JsonObject { ["Status"] = NewContainerHealth } };
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                default:
                    return NotFound();
            }
        }

        private JsonObject? Find(string idOrName) =>
            Containers.TryGetValue(idOrName, out var byId) ? byId
            : Containers.Values.FirstOrDefault(c => c["Name"]?.GetValue<string>() == "/" + idOrName);

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private const string ImageJson = """{"Id":"{id}","Config":{"Env":["PATH=/usr/bin","AGENT_PORT=9001"],"Labels":{"org.opencontainers.image.title":"Sockless Port agent"},"Healthcheck":{"Test":["CMD","/app/sockless-port-agent","healthcheck"]},"Volumes":{"/data":{}}}}""";

        private static HttpResponseMessage NotFound() => Json("""{"message":"No such object"}""", HttpStatusCode.NotFound);
    }
}
