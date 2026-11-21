using System.Text;
using Sockless.Port.Docker.Models;
using Sockless.Port.Docker.Streams;

namespace Sockless.Port.Docker.Tests;

public sealed class ContainerUnitTests
{
    private static readonly DockerJsonContext Json = DockerJsonContext.Default;

    private static byte[] Frame(StreamKind kind, byte[] payload)
    {
        var frame = new byte[8 + payload.Length];
        frame[0] = (byte)kind;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    private static async Task<List<LogLine>> ReadAsync(byte[] data, bool tty, bool timestamps = false)
    {
        var lines = new List<LogLine>();
        await foreach (var line in LogReader.ReadLinesAsync(new MemoryStream(data), tty, timestamps, TestContext.Current.CancellationToken))
            lines.Add(line);
        return lines;
    }

    [Fact]
    public async Task Captured_multiplexed_logs_keep_stdout_and_stderr_apart()
    {
        var lines = await ReadAsync(File.ReadAllBytes(Fixture.Path("docker-container-logs.bin")), tty: false);

        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, l => l.Stream == StreamKind.Stdout && l.Text.StartsWith("tick ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l is { Stream: StreamKind.Stderr, Text: "oops" });
    }

    [Fact]
    public async Task Captured_tty_logs_are_raw_with_crlf()
    {
        var lines = await ReadAsync(File.ReadAllBytes(Fixture.Path("docker-container-logs-tty.bin")), tty: true);

        Assert.Equal(["tty-line", "tty-line"], lines.Select(l => l.Text));
    }

    [Fact]
    public async Task Lines_split_across_frames_and_utf8_split_mid_character_are_rejoined()
    {
        var text = Encoding.UTF8.GetBytes("grüße\nzweite");
        var split = Array.IndexOf(text, (byte)0xC3) + 1; // inside the two-byte ü
        byte[] data = [.. Frame(StreamKind.Stdout, text[..split]), .. Frame(StreamKind.Stdout, text[split..])];

        var lines = await ReadAsync(data, tty: false);

        Assert.Equal(["grüße", "zweite"], lines.Select(l => l.Text));
    }

    [Fact]
    public async Task Timestamps_are_parsed_off_the_line()
    {
        var data = Frame(StreamKind.Stdout, "2026-11-21T20:41:15.123456789Z hello world\n"u8.ToArray());

        var line = Assert.Single(await ReadAsync(data, tty: false, timestamps: true));

        Assert.Equal("hello world", line.Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 20, 41, 15, 123, TimeSpan.Zero).AddTicks(4567), line.Timestamp);
    }

    [Fact]
    public async Task Overlong_lines_are_cut()
    {
        var data = Encoding.UTF8.GetBytes(new string('x', LogReader.MaxLineLength + 10));

        var lines = await ReadAsync(data, tty: true);

        Assert.Equal([LogReader.MaxLineLength, 10], lines.Select(l => l.Text.Length));
    }

    [Fact]
    public void Stats_match_docker_stats_arithmetic()
    {
        var stats = Fixture.Load("docker-container-stats.json", Json.ContainerStats);

        var sample = StatsSample.From(stats);

        var cpuDelta = (double)stats.CpuStats!.CpuUsage!.TotalUsage - stats.PreCpuStats!.CpuUsage!.TotalUsage;
        var systemDelta = (double)stats.CpuStats.SystemCpuUsage - stats.PreCpuStats.SystemCpuUsage;
        Assert.Equal(cpuDelta / systemDelta * stats.CpuStats.OnlineCpus * 100, sample.CpuPercent, precision: 6);
        Assert.Equal(stats.MemoryStats!.Usage - stats.MemoryStats.Stats!["inactive_file"], sample.MemoryUsage);
        Assert.Equal(64 * 1024 * 1024, sample.MemoryLimit); // --memory 64m
        Assert.Equal(stats.Networks!["eth0"].RxBytes, sample.NetworkRx);
        Assert.Equal(0, sample.BlockRead); // blkio is null on this engine
    }

    [Fact]
    public void First_stats_sample_without_previous_reading_has_zero_cpu()
    {
        var stats = new ContainerStats
        {
            CpuStats = new CpuStats { CpuUsage = new CpuUsage { TotalUsage = 500 }, SystemCpuUsage = 10_000, OnlineCpus = 4 },
            PreCpuStats = new CpuStats { CpuUsage = new CpuUsage { TotalUsage = 0 }, SystemCpuUsage = 0 },
        };

        Assert.Equal(0, StatsSample.From(stats).CpuPercent);
    }

    [Fact]
    public void Cgroup_v1_cache_is_subtracted_from_memory()
    {
        var memory = new MemoryStats { Usage = 1000, Stats = new Dictionary<string, long> { ["total_inactive_file"] = 300 } };

        Assert.Equal(700, StatsSample.ComputeMemoryUsage(memory));
    }

    [Theory]
    [InlineData("nginx", "nginx", "latest")]
    [InlineData("nginx:1.27", "nginx", "1.27")]
    [InlineData("ghcr.io/org/app:2", "ghcr.io/org/app", "2")]
    [InlineData("registry:5000/app", "registry:5000/app", "latest")]
    [InlineData("registry:5000/app:v1", "registry:5000/app", "v1")]
    [InlineData("app@sha256:abc", "app@sha256:abc", null)]
    public void Image_references_split_into_name_and_tag(string reference, string name, string? tag)
    {
        Assert.Equal((name, tag), ImageReference.Split(reference));
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("podman")]
    public void Captured_inspects_deserialize(string engine)
    {
        var container = Fixture.Load($"{engine}-container-inspect.json", Json.ContainerInspect);

        Assert.Equal("fixture-web", container.DisplayName);
        Assert.True(container.State!.Running);
        Assert.Contains("GREETING=hello", container.Config!.Env!);
        Assert.Equal("fixture", container.Config.Labels!["app"]);
        Assert.NotNull(container.NetworkSettings?.Networks);
    }

    [Fact]
    public void Captured_docker_inspect_has_ports_mounts_and_limits()
    {
        var container = Fixture.Load("docker-container-inspect.json", Json.ContainerInspect);

        Assert.Equal("unless-stopped", container.HostConfig!.RestartPolicy!.Name);
        Assert.Equal(500_000_000, container.HostConfig.NanoCpus);
        Assert.Equal("5353", container.HostConfig.PortBindings!["53/udp"]![0].HostPort);
        Assert.Contains(container.Mounts!, m => m is { Type: "bind", Destination: "/host-tmp", ReadWrite: false });
        Assert.Contains(container.Mounts!, m => m is { Type: "volume", Name: "fixture-data", ReadWrite: true });
        Assert.Equal("healthy", container.State!.Health!.Status);
        Assert.Equal("172.19.0.2", container.NetworkSettings!.Networks!["fixture-net"].IpAddress);
    }
}
