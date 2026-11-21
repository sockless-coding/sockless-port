using System.Text;
using System.Text.Json;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker;

/// <summary>Encodes credentials for the engine's registry headers.</summary>
public static class RegistryAuth
{
    /// <summary>The server address Docker uses for Docker Hub credentials.</summary>
    public const string DockerHubServerAddress = "https://index.docker.io/v1/";

    /// <summary>The address the engine expects for a registry host (<see cref="ImageReference.Registry"/>).</summary>
    public static string ServerAddress(string registry) =>
        ImageReference.NormalizeRegistry(registry) is ImageReference.DockerHub ? DockerHubServerAddress : ImageReference.NormalizeRegistry(registry);

    /// <summary><c>X-Registry-Auth</c>: base64url (padded, as the Docker CLI sends it) JSON of one credential.</summary>
    public static string Encode(AuthConfig auth) =>
        Base64Url(JsonSerializer.SerializeToUtf8Bytes(auth, DockerJsonContext.Default.AuthConfig));

    /// <summary><c>X-Registry-Config</c> for builds: server address → credential, for every registry the build may pull from.</summary>
    public static string EncodeConfig(IReadOnlyDictionary<string, AuthConfig> auths) =>
        Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, AuthConfig>(auths), DockerJsonContext.Default.DictionaryStringAuthConfig));

    private static string Base64Url(byte[] json) => Convert.ToBase64String(json).Replace('+', '-').Replace('/', '_');

    /// <summary>For tests and diagnostics: decodes <see cref="Encode"/>'s output.</summary>
    internal static string Decode(string header) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(header.Replace('-', '+').Replace('_', '/')));
}
