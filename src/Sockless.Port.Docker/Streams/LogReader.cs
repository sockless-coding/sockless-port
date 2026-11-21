using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Sockless.Port.Docker.Streams;

/// <summary>One line of container output.</summary>
/// <param name="Timestamp">Set when logs were requested with timestamps.</param>
public readonly record struct LogLine(StreamKind Stream, string Text, DateTimeOffset? Timestamp);

/// <summary>
/// Turns a logs or attach stream into lines. Containers without a TTY send multiplexed frames
/// that split lines arbitrarily; containers with a TTY send raw bytes (all on stdout).
/// </summary>
public static class LogReader
{
    /// <summary>Lines longer than this are emitted in pieces, so one runaway line can't exhaust memory.</summary>
    internal const int MaxLineLength = 64 * 1024;

    public static async IAsyncEnumerable<LogLine> ReadLinesAsync(
        Stream stream, bool tty, bool timestamps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stdout = new LineSplitter(StreamKind.Stdout, timestamps);
        var stderr = new LineSplitter(StreamKind.Stderr, timestamps);

        if (tty)
        {
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                foreach (var line in stdout.Push(buffer.AsSpan(0, read)))
                    yield return line;
            }
        }
        else
        {
            await foreach (var frame in MultiplexedStream.ReadFramesAsync(stream, cancellationToken))
            {
                var splitter = frame.Kind == StreamKind.Stderr ? stderr : stdout;
                foreach (var line in splitter.Push(frame.Payload.Span))
                    yield return line;
            }
        }

        if (stdout.Flush() is { } lastOut)
            yield return lastOut;
        if (stderr.Flush() is { } lastErr)
            yield return lastErr;
    }

    /// <summary>Accumulates bytes for one stream and cuts them into lines, decoding UTF-8 across chunk boundaries.</summary>
    private sealed class LineSplitter(StreamKind kind, bool timestamps)
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _line = new();
        private char[] _chars = new char[4096];

        public List<LogLine> Push(ReadOnlySpan<byte> bytes)
        {
            var lines = new List<LogLine>();
            if (_chars.Length < bytes.Length + 4)
                _chars = new char[bytes.Length + 4];
            var count = _decoder.GetChars(bytes, _chars, flush: false);

            foreach (var c in _chars.AsSpan(0, count))
            {
                if (c == '\n')
                {
                    lines.Add(Emit());
                }
                else
                {
                    _line.Append(c);
                    if (_line.Length >= MaxLineLength)
                        lines.Add(Emit());
                }
            }
            return lines;
        }

        public LogLine? Flush() => _line.Length > 0 ? Emit() : null;

        private LogLine Emit()
        {
            if (_line.Length > 0 && _line[^1] == '\r')
                _line.Length--;
            var text = _line.ToString();
            _line.Clear();

            DateTimeOffset? timestamp = null;
            if (timestamps)
            {
                // "2026-11-21T20:41:15.123456789Z message": RFC 3339 with nanoseconds, then a space.
                var space = text.IndexOf(' ', StringComparison.Ordinal);
                var stamp = space < 0 ? text : text[..space];
                if (TryParseTimestamp(stamp, out var parsed))
                {
                    timestamp = parsed;
                    text = space < 0 ? "" : text[(space + 1)..];
                }
            }
            return new LogLine(kind, text, timestamp);
        }
    }

    internal static bool TryParseTimestamp(string value, out DateTimeOffset timestamp)
    {
        // .NET parses at most 7 fractional digits; the engine sends 9.
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && value.EndsWith('Z'))
        {
            var fraction = value[(dot + 1)..^1];
            if (fraction.Length > 7)
                value = $"{value[..dot]}.{fraction[..7]}Z";
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
    }
}
