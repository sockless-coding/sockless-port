using System.Text;
using System.Text.RegularExpressions;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Swarm;

namespace Sockless.Port.Web.Infrastructure;

public enum SwarmObjectKind
{
    Secret,
    Config,
}

/// <summary>A secret or a config, as the pages show them.</summary>
/// <param name="Data">A config's value (base64); never set for secrets.</param>
/// <param name="Driver">The secret store plugin, when swarm doesn't keep the value itself.</param>
public sealed record SwarmObject(SwarmObjectKind Kind, string Id, string Name, ObjectVersion? Version, string? CreatedAt, string? UpdatedAt,
    IReadOnlyDictionary<string, string> Labels, string? Data, string? Driver)
{
    public static SwarmObject From(SwarmSecret secret) => new(SwarmObjectKind.Secret, secret.Id, secret.Name, secret.Version, secret.CreatedAt, secret.UpdatedAt,
        secret.Spec?.Labels ?? new Dictionary<string, string>(), null, secret.Spec?.Driver?.Name);

    public static SwarmObject From(SwarmConfig config) => new(SwarmObjectKind.Config, config.Id, config.Name, config.Version, config.CreatedAt, config.UpdatedAt,
        config.Spec?.Labels ?? new Dictionary<string, string>(), config.Spec?.Data, null);

    public byte[]? Bytes => Data is null ? null : Convert.FromBase64String(Data);

    /// <summary>The value as text, when it is UTF-8 without control characters other than whitespace.</summary>
    public string? Text
    {
        get
        {
            if (Bytes is not { } bytes)
                return null;
            try
            {
                var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                return text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ? null : text;
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }
    }
}

/// <summary>A service that uses a secret or config, and where its tasks find it.</summary>
public sealed record SwarmObjectUse(SwarmService Service, string? Target);

public static partial class SwarmObjects
{
    /// <summary>Most bytes the engine takes: 500 KB for a secret, 1000 KB for a config.</summary>
    public static int MaxSize(SwarmObjectKind kind) => kind == SwarmObjectKind.Secret ? 500 * 1024 : 1000 * 1024;

    public static string Noun(SwarmObjectKind kind) => kind == SwarmObjectKind.Secret ? "secret" : "config";

    public static string Title(SwarmObjectKind kind) => kind == SwarmObjectKind.Secret ? "Secrets" : "Configs";

    public static string Folder(SwarmObjectKind kind) => kind == SwarmObjectKind.Secret ? "/Engine/Swarm/Secrets" : "/Engine/Swarm/Configs";

    public static string Icon(SwarmObjectKind kind) => kind == SwarmObjectKind.Secret ? "lock-closed" : "document-text";

    public static async Task<IReadOnlyList<SwarmObject>> ListAsync(DockerClient client, SwarmObjectKind kind, CancellationToken cancellationToken) =>
        kind == SwarmObjectKind.Secret
            ? [.. (await client.ListSecretsAsync(cancellationToken)).Select(SwarmObject.From)]
            : [.. (await client.ListConfigsAsync(cancellationToken)).Select(SwarmObject.From)];

    public static async Task<(SwarmObject Object, string Json)> InspectAsync(DockerClient client, SwarmObjectKind kind, string id, CancellationToken cancellationToken)
    {
        if (kind == SwarmObjectKind.Secret)
        {
            var (secret, json) = await client.InspectSecretAsync(id, cancellationToken);
            return (SwarmObject.From(secret), json);
        }
        var (config, configJson) = await client.InspectConfigAsync(id, cancellationToken);
        return (SwarmObject.From(config), configJson);
    }

    public static Task<string> CreateAsync(DockerClient client, SwarmObjectKind kind, string name, byte[] value, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken) =>
        kind == SwarmObjectKind.Secret
            ? client.CreateSecretAsync(name, value, labels, cancellationToken)
            : client.CreateConfigAsync(name, value, labels, cancellationToken);

    public static Task RemoveAsync(DockerClient client, SwarmObjectKind kind, string id, CancellationToken cancellationToken) =>
        kind == SwarmObjectKind.Secret ? client.RemoveSecretAsync(id, cancellationToken) : client.RemoveConfigAsync(id, cancellationToken);

    /// <summary>The services using each object, by object ID, with the file their tasks see.</summary>
    public static ILookup<string, SwarmObjectUse> Uses(SwarmObjectKind kind, IEnumerable<SwarmService> services) =>
        services
            .SelectMany(s => kind == SwarmObjectKind.Secret
                ? (s.Spec?.TaskTemplate?.ContainerSpec?.Secrets ?? []).Select(r => (Id: r.SecretId, Use: new SwarmObjectUse(s, r.File?.Name is { } f ? $"/run/secrets/{f}" : null)))
                : (s.Spec?.TaskTemplate?.ContainerSpec?.Configs ?? []).Select(r => (Id: r.ConfigId, Use: new SwarmObjectUse(s, r.File?.Name))))
            .Where(x => x.Id is not null)
            .ToLookup(x => x.Id!, x => x.Use, StringComparer.Ordinal);

    /// <summary>Why a name can't be used, or null. Swarm allows letters, digits, '-', '_' and '.', starting and ending with a letter or digit.</summary>
    public static string? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Enter a name, e.g. db_password."
        : name.Trim().Length > 64 ? "Use at most 64 characters."
        : !ObjectName().IsMatch(name.Trim()) ? "Use letters, digits, '-', '_' and '.', starting and ending with a letter or digit."
        : null;

    [GeneratedRegex("^[a-zA-Z0-9](?:[a-zA-Z0-9._-]*[a-zA-Z0-9])?$")]
    private static partial Regex ObjectName();
}
