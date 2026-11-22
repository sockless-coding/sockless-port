namespace Sockless.Port.Docker.Streams;

/// <summary>
/// A running exec with a TTY: the hijacked connection, readable (output) and writable (input).
/// Disposing it closes the connection, which ends the exec's input.
/// </summary>
public sealed class ExecSession : IAsyncDisposable, IDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly HttpClient? _connection;

    internal ExecSession(string execId, HttpResponseMessage response, Stream stream, HttpClient? connection = null)
    {
        ExecId = execId;
        _response = response;
        _connection = connection;
        Stream = stream;
    }

    public string ExecId { get; }

    /// <summary>Raw terminal bytes in both directions.</summary>
    public Stream Stream { get; }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        _response.Dispose();
        _connection?.Dispose();
    }

    public void Dispose()
    {
        Stream.Dispose();
        _response.Dispose();
        _connection?.Dispose();
    }
}
