using System.Text.Json.Nodes;
using Sockless.Port.Agents;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;

namespace Sockless.Port.Core.Tests;

/// <summary>
/// The update helper replacing a real agent container, started as the server starts it. Skipped
/// unless <c>SOCKLESS_PORT_TEST_ENGINE</c> is set and <c>SOCKLESS_PORT_TEST_AGENT_IMAGE</c> names
/// an agent image on that engine; <c>SOCKLESS_PORT_TEST_AGENT_SOCKET</c> is the engine's socket on
/// its host (default <c>/var/run/docker.sock</c>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentUpdateIntegrationTests : IAsyncLifetime
{
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private static readonly string? AgentImage = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_AGENT_IMAGE");
    private static readonly string Socket = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_AGENT_SOCKET") is { Length: > 0 } s ? s : "/var/run/docker.sock";

    private readonly string _suffix = Guid.NewGuid().ToString("n")[..10];
    private DockerClient _client = null!;

    private string Name => $"sp-agent-{_suffix}";

    private string Volume => $"sp-agent-data-{_suffix}";

    private string Repository => $"sockless-port-agent-update-test-{_suffix}";

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address) || string.IsNullOrEmpty(AgentImage),
            "Set SOCKLESS_PORT_TEST_ENGINE and SOCKLESS_PORT_TEST_AGENT_IMAGE to run the agent update integration test.");
        _client = await DockerClient.ConnectAsync(DockerAddress.Parse(Address!), cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        foreach (var container in await _client.ListContainersAsync(new Dictionary<string, string[]> { ["name"] = [_suffix] }))
        {
            try { await _client.RemoveContainerAsync(container.Id, force: true, removeVolumes: true); } catch (DockerApiException) { }
        }
        try { await _client.RemoveVolumeAsync(Volume, force: true); } catch (DockerApiException) { }
        foreach (var tag in (string[])["old", "new"])
        {
            try { await _client.RemoveImageAsync($"{Repository}:{tag}", noPrune: true); } catch (DockerApiException) { }
        }
        _client.Dispose();
    }

    [Fact]
    public async Task The_helper_replaces_the_agent_and_keeps_its_volume()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.TagImageAsync(AgentImage!, Repository, "old", ct);
        await _client.TagImageAsync(AgentImage!, Repository, "new", ct);
        var oldImage = $"{Repository}:old";
        var newImage = $"{Repository}:new";

        var agent = (await _client.CreateContainerAsync(Name, new JsonObject
        {
            ["Image"] = oldImage,
            ["Env"] = new JsonArray((JsonNode)"AGENT_DATA=/data"),
            // Root in the container, so it may use the socket whatever its group is on this host.
            ["User"] = "0",
            ["HostConfig"] = new JsonObject
            {
                ["Binds"] = new JsonArray((JsonNode)$"{Socket}:/var/run/docker.sock", (JsonNode)$"{Volume}:/data"),
                ["RestartPolicy"] = new JsonObject { ["Name"] = "unless-stopped" },
                ["SecurityOpt"] = new JsonArray((JsonNode)"label=disable"),
            },
        }, ct)).Id;
        await _client.StartContainerAsync(agent, ct);
        await WaitAsync(async () => (await _client.InspectContainerAsync(agent, ct)).Container.State?.Health?.Status == "healthy", TimeSpan.FromSeconds(90));

        var (_, json) = await _client.InspectContainerAsync(agent, ct);
        var body = AgentUpdates.HelperBody(JsonNode.Parse(json)!.AsObject(), new AgentInfo { Engine = "unix:///var/run/docker.sock" }, newImage);
        Assert.NotNull(body);
        var helper = (await _client.CreateContainerAsync($"sp-agent-update-{_suffix}", body, ct)).Id;
        await _client.StartContainerAsync(helper, ct);
        await WaitAsync(async () => (await _client.InspectContainerAsync(helper, ct)).Container.State is { Running: false, Status: not "created" }, TimeSpan.FromMinutes(3));

        var (finished, _) = await _client.InspectContainerAsync(helper, ct);
        List<string> output = [];
        await foreach (var line in _client.StreamLogsAsync(helper, new LogOptions { Tail = 50 }, ct))
            output.Add(line.Text);
        Assert.True(finished.State!.ExitCode == AgentUpdate.Updated, string.Join('\n', output));

        var (replaced, _) = await _client.InspectContainerAsync(Name, ct);
        Assert.NotEqual(agent, replaced.Id);
        Assert.Equal(newImage, replaced.Config?.Image);
        Assert.True(replaced.State?.Running);
        Assert.Equal("healthy", replaced.State?.Health?.Status);
        Assert.Contains(replaced.Mounts!, m => m.Name == Volume && m.Destination == "/data");
        Assert.Equal("unless-stopped", replaced.HostConfig?.RestartPolicy?.Name);
        Assert.Equal("0", replaced.Config?.User);
        var gone = await Assert.ThrowsAsync<DockerApiException>(() => _client.InspectContainerAsync(agent, ct));
        Assert.True(gone.IsNotFound);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"Not done within {timeout}.");
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }
    }
}
