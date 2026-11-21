using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Sockless.Port.Docker.Streams;

/// <summary>
/// Reads newline-delimited JSON, the format of <c>/events</c> and of pull, push and build progress.
/// </summary>
public static class JsonLines
{
    public static async IAsyncEnumerable<T> ReadAsync<T>(
        Stream stream, JsonTypeInfo<T> typeInfo, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var item = JsonSerializer.Deserialize(line, typeInfo);
            if (item is not null)
                yield return item;
        }
    }
}
