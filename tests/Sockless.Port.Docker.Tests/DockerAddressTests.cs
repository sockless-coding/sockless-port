namespace Sockless.Port.Docker.Tests;

public sealed class DockerAddressTests
{
    [Theory]
    [InlineData("unix:///var/run/docker.sock", "unix:///var/run/docker.sock")]
    [InlineData("unix:///run/user/1000/podman/podman.sock", "unix:///run/user/1000/podman/podman.sock")]
    [InlineData("npipe:////./pipe/docker_engine", "npipe:////./pipe/docker_engine")]
    [InlineData("npipe://./pipe/dockerDesktopLinuxEngine", "npipe:////./pipe/dockerDesktopLinuxEngine")]
    [InlineData("tcp://10.0.0.5:2375", "tcp://10.0.0.5:2375")]
    [InlineData("tcp://docker-host", "tcp://docker-host:2375")]
    [InlineData("TCP://docker-host:80", "tcp://docker-host:80")]
    [InlineData("https://docker-host", "https://docker-host:2376")]
    [InlineData("tcp://[::1]:2375", "tcp://[::1]:2375")]
    [InlineData("  unix:///var/run/docker.sock  ", "unix:///var/run/docker.sock")]
    public void Parses_and_formats_engine_addresses(string input, string expected)
    {
        Assert.Equal(expected, DockerAddress.Parse(input).ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/var/run/docker.sock")]
    [InlineData("unix://relative.sock")]
    [InlineData("npipe:////./pipe/")]
    [InlineData("npipe://server/share")]
    [InlineData("tcp://host:2375/path")]
    [InlineData("tcp://host:2375?x=1")]
    [InlineData("ssh://user@host")]
    public void Rejects_invalid_addresses(string? input)
    {
        Assert.False(DockerAddress.TryParse(input, out _));
    }

    [Fact]
    public void Parsed_types_match_the_scheme()
    {
        Assert.IsType<UnixSocketAddress>(DockerAddress.Parse("unix:///var/run/docker.sock"));
        Assert.Equal("docker_engine", Assert.IsType<NamedPipeAddress>(DockerAddress.Parse("npipe:////./pipe/docker_engine")).PipeName);
        var tcp = Assert.IsType<TcpAddress>(DockerAddress.Parse("https://host:9443"));
        Assert.Equal(("host", 9443), (tcp.Host, tcp.Port));
        Assert.NotNull(tcp.Tls);
    }

    [Fact]
    public void Tls_settings_never_print_key_material()
    {
        var tls = new DockerTls { ClientCertificatePem = "CERT", ClientKeyPem = "-----BEGIN PRIVATE KEY-----secret" };

        Assert.DoesNotContain("secret", new TcpAddress("host", 2376, tls).ToString() + tls);
    }
}
