using Sockless.Port.Docker;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>Which exceptions mean "the engine call failed" (show it) rather than "a bug" (let it throw).</summary>
public static class EngineFailures
{
    public static bool IsFailure(Exception ex, CancellationToken requestAborted) =>
        !requestAborted.IsCancellationRequested &&
        ex is DockerApiException or DockerApiVersionException or HttpRequestException or IOException or TimeoutException
            or TaskCanceledException or ObjectDisposedException or InvalidDataException or NotSupportedException
            or System.Text.Json.JsonException;

    /// <summary>The engine's own message for API errors (it names the problem); a connection hint otherwise.</summary>
    public static string Describe(Exception ex, EngineConnection connection) => EngineErrors.ForCall(ex, connection.Address);

    /// <inheritdoc cref="EngineErrors.Sentence"/>
    public static string Sentence(string message) => EngineErrors.Sentence(message);
}
