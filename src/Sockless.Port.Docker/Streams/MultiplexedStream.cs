using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Sockless.Port.Docker.Streams;

public enum StreamKind : byte
{
    Stdin = 0,
    Stdout = 1,
    Stderr = 2,
    System = 3,
}

/// <summary>One frame of a multiplexed stream: which output it came from and its bytes.</summary>
public readonly record struct StreamFrame(StreamKind Kind, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Reads the engine's multiplexed output format, used by logs, attach and exec when the
/// container has no TTY: an 8-byte header <c>[kind, 0, 0, 0, size (uint32, big-endian)]</c>
/// followed by <c>size</c> bytes of payload.
/// </summary>
public static class MultiplexedStream
{
    private const int HeaderLength = 8;

    /// <summary>Largest frame accepted. The engine splits output into much smaller frames.</summary>
    internal const int MaxFrameLength = 16 * 1024 * 1024;

    public static async IAsyncEnumerable<StreamFrame> ReadFramesAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderLength];
        while (true)
        {
            var read = await stream.ReadAtLeastAsync(header, HeaderLength, throwOnEndOfStream: false, cancellationToken);
            if (read == 0)
                yield break;
            if (read < HeaderLength)
                throw new EndOfStreamException("The stream ended inside a frame header.");

            if (header[0] > (byte)StreamKind.System || header[1] != 0 || header[2] != 0 || header[3] != 0)
                throw new InvalidDataException("Invalid frame header; the stream is probably not multiplexed (the container may have a TTY).");

            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (length > MaxFrameLength)
                throw new InvalidDataException($"Frame of {length} bytes exceeds the {MaxFrameLength}-byte limit; the stream is probably not multiplexed.");

            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, cancellationToken);
            yield return new StreamFrame((StreamKind)header[0], payload);
        }
    }
}
