using System.Net;

namespace Sockless.Port.Docker;

/// <summary>The engine answered with an error status. <see cref="Exception.Message"/> is the engine's message.</summary>
public sealed class DockerApiException(HttpStatusCode statusCode, string message, string requestPath)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>The request path, without query string (which may carry filters but never secrets).</summary>
    public string RequestPath { get; } = requestPath;

    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;

    public bool IsConflict => StatusCode == HttpStatusCode.Conflict;
}

/// <summary>The engine's API is older than <see cref="DockerClient.MinimumApiVersion"/>.</summary>
public sealed class DockerApiVersionException(Version serverVersion)
    : Exception($"The engine's API version {serverVersion} is older than the minimum supported version {DockerClient.MinimumApiVersion}.")
{
    public Version ServerVersion { get; } = serverVersion;
}
