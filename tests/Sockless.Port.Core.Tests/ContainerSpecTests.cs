using System.Text.Json;
using System.Text.Json.Nodes;
using Sockless.Port.Containers;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Core.Tests;

public sealed class ContainerSpecTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Builds_a_full_create_body()
    {
        var spec = new ContainerSpec
        {
            Name = "web",
            Image = "nginx:1.27",
            Command = "sh -c 'echo hi && sleep 1'",
            Environment = "A=1\n# comment\n\nB=with spaces = fine",
            Ports = "8080:80\n127.0.0.1:5353:53/udp\n9000",
            Volumes = "data:/data\n/srv/conf:/etc/nginx/conf.d:ro\n/cache",
            Labels = "app=web\nflag",
            Network = "frontend",
            RestartPolicy = "on-failure",
            MaxRetries = 3,
            MemoryLimit = 256,
            CpuLimit = 0.5m,
            User = "1000:1000",
        };

        var (body, errors) = ContainerSpecBuilder.Build(spec);

        Assert.Empty(errors);
        var expected = JsonNode.Parse("""
            {
              "Image": "nginx:1.27",
              "Env": ["A=1", "B=with spaces = fine"],
              "Labels": {"app": "web", "flag": ""},
              "Tty": false,
              "OpenStdin": false,
              "HostConfig": {
                "RestartPolicy": {"Name": "on-failure", "MaximumRetryCount": 3},
                "PortBindings": {
                  "80/tcp": [{"HostIp": "", "HostPort": "8080"}],
                  "53/udp": [{"HostIp": "127.0.0.1", "HostPort": "5353"}],
                  "9000/tcp": [{"HostIp": "", "HostPort": ""}]
                },
                "Binds": ["data:/data", "/srv/conf:/etc/nginx/conf.d:ro"],
                "NetworkMode": "frontend",
                "Memory": 268435456,
                "NanoCpus": 500000000
              },
              "Cmd": ["sh", "-c", "echo hi && sleep 1"],
              "ExposedPorts": {"80/tcp": {}, "53/udp": {}, "9000/tcp": {}},
              "Volumes": {"/cache": {}},
              "User": "1000:1000"
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, body), body!.ToJsonString());
    }

    [Theory]
    [InlineData(nameof(ContainerSpec.Image), "")]
    [InlineData(nameof(ContainerSpec.Name), "bad name")]
    [InlineData(nameof(ContainerSpec.Environment), "NOVALUE")]
    [InlineData(nameof(ContainerSpec.Ports), "80:99999")]
    [InlineData(nameof(ContainerSpec.Ports), "8000-8010:8000-8010")]
    [InlineData(nameof(ContainerSpec.Ports), "80/xyz")]
    [InlineData(nameof(ContainerSpec.Ports), "999.1.1.1:80:80")]
    [InlineData(nameof(ContainerSpec.Volumes), "data:relative")]
    [InlineData(nameof(ContainerSpec.Volumes), "data:/data:bogus")]
    [InlineData(nameof(ContainerSpec.Command), "echo 'unterminated")]
    public void Invalid_fields_are_reported_on_that_field(string field, string value)
    {
        var spec = new ContainerSpec { Image = "busybox" };
        typeof(ContainerSpec).GetProperty(field)!.SetValue(spec, value);

        var (body, errors) = ContainerSpecBuilder.Build(spec);

        Assert.Null(body);
        Assert.Equal(field, Assert.Single(errors).Field);
    }

    [Fact]
    public void Ipv6_host_addresses_are_accepted()
    {
        var ports = ContainerSpecBuilder.ParsePorts("[::1]:8080:80");

        Assert.Equal("::1", ports.Bindings["80/tcp"]![0]!["HostIp"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("nginx -g 'daemon off;'", new[] { "nginx", "-g", "daemon off;" })]
    [InlineData("echo \"a \\\"b\\\" c\"", new[] { "echo", "a \"b\" c" })]
    [InlineData("a\\ b c", new[] { "a b", "c" })]
    [InlineData("  x   y  ", new[] { "x", "y" })]
    [InlineData("''", new[] { "" })]
    public void Shell_words_split_like_a_shell(string text, string[] expected)
    {
        Assert.Equal(expected, ContainerSpecBuilder.ShellWords(text));
    }

    [Theory]
    [InlineData((object)new[] { "nginx", "-g", "daemon off;" })]
    [InlineData((object)new[] { "sh", "-c", "echo it's here" })]
    [InlineData((object)new[] { "" })]
    public void Joined_shell_words_split_back_to_the_same_words(string[] words)
    {
        Assert.Equal(words, ContainerSpecBuilder.ShellWords(ContainerSpecBuilder.JoinShellWords(words)));
    }

    [Fact]
    public void Duplicate_prefills_the_form_from_a_captured_container()
    {
        var container = JsonSerializer.Deserialize<ContainerInspect>(Fixture("docker-container-inspect.json"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var spec = ContainerSpecMapper.FromInspect(container);

        Assert.Equal("fixture-web-copy", spec.Name);
        Assert.Equal("busybox:1.37", spec.Image);
        Assert.Contains("GREETING=hello", spec.Environment);
        Assert.Equal(["127.0.0.1:5353:53/udp", "8080:80"], spec.Ports!.Split('\n'));
        Assert.Contains("fixture-data:/data", spec.Volumes);
        Assert.Contains("/tmp:/host-tmp:ro", spec.Volumes);
        Assert.Equal("app=fixture", spec.Labels); // the compose label is dropped
        Assert.Equal("fixture-net", spec.Network);
        Assert.Equal("unless-stopped", spec.RestartPolicy);
        Assert.Equal(64, spec.MemoryLimit);
        Assert.Equal(0.5m, spec.CpuLimit);

        // And what it prefills is valid input.
        Assert.Empty(ContainerSpecBuilder.Build(spec).Errors);
    }

    [Fact]
    public void Recreate_body_keeps_configuration_and_drops_engine_assigned_values()
    {
        var inspect = JsonNode.Parse(Fixture("docker-container-inspect.json"))!.AsObject();

        var (body, extra) = ContainerRecreator.BuildCreateBody(inspect);

        Assert.Null(body["Hostname"]); // was the short id
        Assert.Equal("busybox:1.37", body["Image"]!.GetValue<string>());
        Assert.Equal("unless-stopped", body["HostConfig"]!["RestartPolicy"]!["Name"]!.GetValue<string>());
        Assert.Equal(67108864, body["HostConfig"]!["Memory"]!.GetValue<long>());
        var endpoint = body["NetworkingConfig"]!["EndpointsConfig"]!["fixture-net"]!.AsObject();
        Assert.False(endpoint.ContainsKey("IPAddress"));
        Assert.False(endpoint.ContainsKey("MacAddress"));
        Assert.Empty(extra);
    }

    [Fact]
    public void Recreate_body_connects_extra_networks_afterwards()
    {
        var inspect = JsonNode.Parse("""
            {
              "Id": "0123456789abcdef0123",
              "Config": {"Image": "busybox", "Hostname": "custom"},
              "HostConfig": {"NetworkMode": "front"},
              "NetworkSettings": {"Networks": {
                "front": {"Aliases": ["web", "0123456789ab"], "IPAddress": "10.0.0.2"},
                "back": {"IPAMConfig": {"IPv4Address": "10.1.0.9"}}
              }}
            }
            """)!.AsObject();

        var (body, extra) = ContainerRecreator.BuildCreateBody(inspect);

        Assert.Equal("custom", body["Hostname"]!.GetValue<string>());
        Assert.Equal(["web"], body["NetworkingConfig"]!["EndpointsConfig"]!["front"]!["Aliases"]!.AsArray().Select(a => a!.GetValue<string>()));
        var (network, endpoint) = Assert.Single(extra);
        Assert.Equal("back", network);
        Assert.Equal("10.1.0.9", endpoint["IPAMConfig"]!["IPv4Address"]!.GetValue<string>());
    }

    [Fact]
    public void Host_networked_containers_have_no_endpoints()
    {
        var inspect = JsonNode.Parse("""{"Id":"x","Config":{"Hostname":"the-host"},"HostConfig":{"NetworkMode":"host"},"NetworkSettings":{"Networks":{"host":{}}}}""")!.AsObject();

        var (body, extra) = ContainerRecreator.BuildCreateBody(inspect);

        Assert.Null(body["NetworkingConfig"]);
        Assert.Null(body["Hostname"]); // the host's name; setting it with host networking is an error
        Assert.Empty(extra);
    }
}
