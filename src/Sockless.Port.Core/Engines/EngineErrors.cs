using System.Net.Sockets;
using Sockless.Port.Docker;

namespace Sockless.Port.Engines;

/// <summary>Turns connection failures into messages that say what to check.</summary>
public static class EngineErrors
{
    public static string Describe(Exception exception, DockerAddress address)
    {
        var socket = Find<SocketException>(exception);
        var where = address.ToString();

        return (exception, socket?.SocketErrorCode) switch
        {
            (DockerApiVersionException version, _) => version.Message,
            (_, SocketError.AccessDenied) =>
                $"Permission denied on {where}. Give the container the socket's group (compose: group_add with the socket's GID).",
            (_, SocketError.AddressNotAvailable or SocketError.HostNotFound) when address is TcpAddress =>
                $"Can't resolve or reach {where}.",
            (_, SocketError.ConnectionRefused) when address is TcpAddress =>
                $"Connection refused at {where}. Check that the engine listens there.",
            (_, SocketError.ConnectionRefused or SocketError.AddressNotAvailable) =>
                $"No engine is listening at {where}. Check that the socket is mounted and the engine is running.",
            (_, not null) when socket!.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase) =>
                $"No socket at {where}. Mount the Docker or Podman socket into the container.",
            (TimeoutException or TaskCanceledException, _) => $"Timed out connecting to {where}.",
            (IOException io, _) when address is NamedPipeAddress && io.Message.Contains("pipe", StringComparison.OrdinalIgnoreCase) =>
                $"No engine is listening at {where}.",
            (DockerApiException api, _) => $"The engine at {where} answered with an error: {api.Message}",
            (HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded }, _) =>
                $"The engine at {where} closed the connection. It may be restarting or stopped.",
            _ => $"Can't reach the engine at {where}: {Innermost(exception).Message}",
        };
    }

    private static T? Find<T>(Exception? exception) where T : Exception
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is T match)
                return match;
        }
        return null;
    }

    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException is not null)
            exception = exception.InnerException;
        return exception;
    }
}
