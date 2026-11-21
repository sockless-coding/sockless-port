using System.Text;
using Sockless.Port.Docker.Models;
using Sockless.Port.Docker.Streams;

namespace Sockless.Port.Docker.Tests;

public sealed class StreamTests
{
    private static byte[] Frame(StreamKind kind, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[8 + payload.Length];
        frame[0] = (byte)kind;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    [Fact]
    public async Task Reads_multiplexed_frames_in_order()
    {
        using var stream = new MemoryStream([.. Frame(StreamKind.Stdout, "hello\n"), .. Frame(StreamKind.Stderr, "oops\n"), .. Frame(StreamKind.Stdout, "")]);

        var frames = new List<(StreamKind, string)>();
        await foreach (var frame in MultiplexedStream.ReadFramesAsync(stream, TestContext.Current.CancellationToken))
            frames.Add((frame.Kind, Encoding.UTF8.GetString(frame.Payload.Span)));

        Assert.Equal([(StreamKind.Stdout, "hello\n"), (StreamKind.Stderr, "oops\n"), (StreamKind.Stdout, "")], frames);
    }

    [Fact]
    public async Task Frames_split_across_reads_are_reassembled()
    {
        // A stream that hands out one byte per read, like a slow socket.
        using var stream = new TrickleStream([.. Frame(StreamKind.Stdout, "abc"), .. Frame(StreamKind.Stderr, "de")]);

        var text = new StringBuilder();
        await foreach (var frame in MultiplexedStream.ReadFramesAsync(stream, TestContext.Current.CancellationToken))
            text.Append(Encoding.UTF8.GetString(frame.Payload.Span));

        Assert.Equal("abcde", text.ToString());
    }

    [Fact]
    public async Task Truncated_header_is_an_error()
    {
        using var stream = new MemoryStream([1, 0, 0]);

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
        {
            await foreach (var _ in MultiplexedStream.ReadFramesAsync(stream, TestContext.Current.CancellationToken)) { }
        });
    }

    [Fact]
    public async Task Non_multiplexed_data_is_rejected_instead_of_allocating_gigabytes()
    {
        // A plain-text log line misread as a header claims a frame of ~1.9 GB.
        using var stream = new MemoryStream("2026-11-21 log line\n"u8.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in MultiplexedStream.ReadFramesAsync(stream, TestContext.Current.CancellationToken)) { }
        });
    }

    [Fact]
    public async Task Reads_captured_event_stream()
    {
        await using var stream = File.OpenRead(Fixture.Path("docker-events.jsonl"));

        var events = new List<DockerEvent>();
        await foreach (var e in JsonLines.ReadAsync(stream, DockerJsonContext.Default.DockerEvent, TestContext.Current.CancellationToken))
            events.Add(e);

        Assert.Equal(13, events.Count);
        var volume = Assert.Single(events, e => e is { Type: "volume", Action: "create" });
        Assert.Equal("fixture-data", volume.Actor?.Id);
        Assert.Contains(events, e => e is { Type: "container", Action: "start" } && e.Actor?.Attributes?["name"] == "fixture-web");
        Assert.All(events, e => Assert.True(e.TimeNano > 0));
    }

    [Fact]
    public void Podman_volume_events_are_normalized_to_docker_shape()
    {
        // Captured from Podman 6.2's compatible API.
        var podman = System.Text.Json.JsonSerializer.Deserialize(
            """{"Type":"volume","Action":"remove","Actor":{"ID":"","Attributes":{"image":"","name":"probe-vol","podId":""}},"scope":"local","time":1795291568,"timeNano":1795291568451923917,"status":"remove"}""",
            DockerJsonContext.Default.DockerEvent)!;

        var normalized = EventNormalizer.Normalize(podman);

        Assert.Equal("probe-vol", normalized.Actor?.Id);
        Assert.Equal("destroy", normalized.Action);
    }

    [Fact]
    public void Docker_events_pass_through_unchanged()
    {
        var docker = new DockerEvent { Type = "container", Action = "start", Actor = new EventActor { Id = "abc", Attributes = new Dictionary<string, string> { ["name"] = "web" } } };

        Assert.Same(docker, EventNormalizer.Normalize(docker));
    }

    [Fact]
    public async Task Json_lines_skip_blank_lines()
    {
        using var stream = new MemoryStream("""
            {"Type":"volume","Action":"create"}

            {"Type":"volume","Action":"destroy"}
            """u8.ToArray());

        var actions = new List<string?>();
        await foreach (var e in JsonLines.ReadAsync(stream, DockerJsonContext.Default.DockerEvent, TestContext.Current.CancellationToken))
            actions.Add(e.Action);

        Assert.Equal(["create", "destroy"], actions);
    }

    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length || count == 0)
                return 0;
            buffer[offset] = data[_position++];
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
