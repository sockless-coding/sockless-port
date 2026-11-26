using System.Text.Json.Nodes;

namespace Sockless.Port.Docker.Tests;

public sealed class ContainerCreateBodyTests
{
    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void What_the_old_image_gave_is_left_to_the_new_one()
    {
        var body = Parse("""
            {
              "Env": ["PATH=/usr/bin", "AGENT_PORT=9001", "AGENT_SERVER=https://port.example.com"],
              "Labels": { "org.opencontainers.image.title": "Sockless Port agent", "org.opencontainers.image.version": "1", "mine": "yes" },
              "Cmd": null,
              "Entrypoint": ["/app/sockless-port-agent"],
              "User": "0",
              "WorkingDir": "/app",
              "Healthcheck": { "Test": ["CMD", "/app/sockless-port-agent", "healthcheck"], "Interval": 30000000000 },
              "ExposedPorts": { "9001/tcp": {}, "9100/tcp": {} },
              "Volumes": { "/data": {} },
              "Image": "ghcr.io/sockless-coding/sockless-port-agent:2026.10.0"
            }
            """);
        var image = Parse("""
            {
              "Env": ["PATH=/usr/bin", "AGENT_PORT=9001"],
              "Labels": { "org.opencontainers.image.title": "Sockless Port agent", "org.opencontainers.image.version": "0" },
              "Entrypoint": ["/app/sockless-port-agent"],
              "User": "1654",
              "WorkingDir": "/app",
              "Healthcheck": { "Test": ["CMD", "/app/sockless-port-agent", "healthcheck"], "Interval": 30000000000 },
              "ExposedPorts": { "9001/tcp": {} },
              "Volumes": { "/data": {} }
            }
            """);

        ContainerCreateBody.RemoveImageDefaults(body, image);

        Assert.Equal(["AGENT_SERVER=https://port.example.com"], body["Env"]!.AsArray().Select(e => e!.GetValue<string>()));
        // A label the container set differently stays; one it inherited goes.
        Assert.Equal(["org.opencontainers.image.version", "mine"], body["Labels"]!.AsObject().Select(l => l.Key));
        Assert.False(body.ContainsKey("Entrypoint"));
        Assert.False(body.ContainsKey("WorkingDir"));
        Assert.False(body.ContainsKey("Healthcheck"));
        Assert.False(body.ContainsKey("Volumes"));
        Assert.Equal("0", body["User"]!.GetValue<string>());
        Assert.Equal(["9100/tcp"], body["ExposedPorts"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void Without_the_old_image_everything_stays()
    {
        var body = Parse("""{ "Env": ["A=1"], "User": "1654" }""");

        ContainerCreateBody.RemoveImageDefaults(body, null);

        Assert.Equal("""{"Env":["A=1"],"User":"1654"}""", body.ToJsonString());
    }

    [Fact]
    public void Anonymous_volumes_are_kept_by_name()
    {
        var inspect = Parse("""
            {
              "Mounts": [
                { "Type": "volume", "Name": "0123abcd", "Destination": "/data", "RW": true },
                { "Type": "volume", "Name": "cache", "Destination": "/cache", "RW": false },
                { "Type": "volume", "Name": "named", "Destination": "/named", "RW": true },
                { "Type": "bind", "Source": "/var/run/docker.sock", "Destination": "/var/run/docker.sock", "RW": true },
                { "Type": "volume", "Name": "mounted", "Destination": "/mounted", "RW": true }
              ]
            }
            """);
        var body = Parse("""
            {
              "HostConfig": {
                "Binds": ["named:/named", "/var/run/docker.sock:/var/run/docker.sock"],
                "Mounts": [{ "Type": "volume", "Source": "mounted", "Target": "/mounted" }]
              }
            }
            """);

        ContainerCreateBody.KeepAnonymousVolumes(body, inspect);

        Assert.Equal(["named:/named", "/var/run/docker.sock:/var/run/docker.sock", "0123abcd:/data", "cache:/cache:ro"],
            body["HostConfig"]!["Binds"]!.AsArray().Select(b => b!.GetValue<string>()));
    }

    [Fact]
    public void A_container_without_binds_gets_them_for_its_anonymous_volumes()
    {
        var inspect = Parse("""{ "Mounts": [{ "Type": "volume", "Name": "0123abcd", "Destination": "/data", "RW": true }] }""");
        var body = Parse("""{ "HostConfig": { "Binds": null } }""");

        ContainerCreateBody.KeepAnonymousVolumes(body, inspect);

        Assert.Equal(["0123abcd:/data"], body["HostConfig"]!["Binds"]!.AsArray().Select(b => b!.GetValue<string>()));
    }
}
