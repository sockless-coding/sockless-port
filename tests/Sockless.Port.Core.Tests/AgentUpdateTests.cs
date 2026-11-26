using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Sockless.Port.Agents;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;

namespace Sockless.Port.Core.Tests;

public sealed class AgentUpdateTests
{
    private const string Image = "ghcr.io/sockless-coding/sockless-port-agent:2026.12.0";

    private static JsonObject Inspect(string user = "", string socketSource = "/run/user/1000/podman/podman.sock") => JsonNode.Parse($$"""
        {
          "Id": "aaaabbbbccccddddeeeeffff0000111122223333444455556666777788889999",
          "Config": { "User": "{{user}}" },
          "HostConfig": { "NetworkMode": "bridge", "GroupAdd": ["974"], "SecurityOpt": ["label=disable"], "PortBindings": { "9001/tcp": [{ "HostPort": "9001" }] } },
          "Mounts": [
            { "Type": "bind", "Source": "{{socketSource}}", "Destination": "/var/run/docker.sock" },
            { "Type": "volume", "Name": "sockless-port-agent", "Destination": "/data" }
          ]
        }
        """)!.AsObject();

    [Fact]
    public void The_helper_gets_the_socket_and_permissions_the_agent_has_and_nothing_else()
    {
        var body = AgentUpdates.HelperBody(Inspect(user: "0"), new AgentInfo { Engine = "unix:///var/run/docker.sock" }, Image)!;

        Assert.Equal(Image, body["Image"]!.GetValue<string>());
        Assert.Equal(["update", "--target", "aaaabbbbccccddddeeeeffff0000111122223333444455556666777788889999", "--image", Image],
            body["Cmd"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(["AGENT_ENGINE=unix:///var/run/docker.sock"], body["Env"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Equal("0", body["User"]!.GetValue<string>());
        Assert.Equal(AgentUpdate.HelperKind, body["Labels"]![AgentUpdate.HelperLabel]!.GetValue<string>());
        Assert.Equal("NONE", body["Healthcheck"]!["Test"]![0]!.GetValue<string>());
        var host = body["HostConfig"]!.AsObject();
        Assert.Equal(["/run/user/1000/podman/podman.sock:/var/run/docker.sock"], host["Binds"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.Equal("none", host["NetworkMode"]!.GetValue<string>());
        Assert.Equal("974", host["GroupAdd"]![0]!.GetValue<string>());
        Assert.Equal("label=disable", host["SecurityOpt"]![0]!.GetValue<string>());
        // Not the agent's port, or its volume: the helper only needs the socket.
        Assert.False(host.ContainsKey("PortBindings"));
        Assert.Single(host["Binds"]!.AsArray());
    }

    [Fact]
    public void The_helper_keeps_the_image_user_when_the_agent_had_none()
    {
        var body = AgentUpdates.HelperBody(Inspect(), new AgentInfo { Engine = "unix:///var/run/docker.sock" }, Image)!;

        Assert.False(body.ContainsKey("User"));
    }

    [Fact]
    public void Without_the_socket_mounted_there_is_no_helper()
    {
        Assert.Null(AgentUpdates.HelperBody(Inspect(), new AgentInfo { Engine = "unix:///run/podman/podman.sock" }, Image));
    }

    [Fact]
    public async Task The_agent_names_its_own_container()
    {
        List<string> calls = [];
        using var client = await ConnectAsync(calls, containers: "[]");

        var found = await AgentUpdates.FindContainerAsync(client, new AgentInfo { ContainerIds = ["gone", "mine"] }, TestContext.Current.CancellationToken);

        Assert.Equal("mine", found?.Container.Id);
        Assert.DoesNotContain(calls, c => c.Contains("/containers/json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_older_agent_is_found_by_its_image_preferring_the_install_commands_name()
    {
        List<string> calls = [];
        using var client = await ConnectAsync(calls, containers: """
            [
              { "Id": "other", "Names": ["/second-agent"], "Labels": { "org.opencontainers.image.title": "Sockless Port agent" } },
              { "Id": "helper", "Names": ["/sockless-port-agent-update-1"], "Labels": { "sockless-port.helper": "agent-update" } },
              { "Id": "mine", "Names": ["/sockless-port-agent"], "Labels": { "org.opencontainers.image.title": "Sockless Port agent" } }
            ]
            """);

        var found = await AgentUpdates.FindContainerAsync(client, new AgentInfo(), TestContext.Current.CancellationToken);

        Assert.Equal("mine", found?.Container.Id);
        var list = Assert.Single(calls, c => c.Contains("/containers/json", StringComparison.Ordinal));
        Assert.Contains("Sockless Port agent", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_agent_container_nothing_is_found()
    {
        using var client = await ConnectAsync([], containers: "[]");

        Assert.Null(await AgentUpdates.FindContainerAsync(client, new AgentInfo(), TestContext.Current.CancellationToken));
    }

    private static Task<DockerClient> ConnectAsync(List<string> calls, string containers) =>
        DockerClient.ConnectAsync(new Fake(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add($"{request.Method} {path}{Uri.UnescapeDataString(request.RequestUri.Query)}");
            return path switch
            {
                "/version" => Json("""{"ApiVersion":"1.51"}"""),
                "/v1.51/containers/json" => Json(containers),
                "/v1.51/containers/mine/json" => Json("""{"Id":"mine","Name":"/sockless-port-agent"}"""),
                _ => Json("""{"message":"No such container"}""", HttpStatusCode.NotFound),
            };
        }), new Uri("http://docker/"), new UnixSocketAddress("/fake.sock"), null, TestContext.Current.CancellationToken);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
