using System.Text.RegularExpressions;

namespace Sockless.Port.Docker;

/// <summary>
/// An image reference split the way the engine and registries read it:
/// <c>[registry/]path[:tag][@digest]</c>. Docker Hub names are normalised, so <c>nginx</c>,
/// <c>library/nginx</c> and <c>docker.io/library/nginx</c> have the same <see cref="Repository"/>.
/// </summary>
/// <param name="Registry">The registry host, with port: <c>docker.io</c>, <c>ghcr.io</c>, <c>localhost:5000</c>.</param>
/// <param name="Path">The repository path in the registry: <c>library/nginx</c>, <c>org/app</c>.</param>
public sealed partial record ImageReference(string Registry, string Path, string? Tag, string? Digest)
{
    public const string DockerHub = "docker.io";

    /// <summary><c>docker.io/library/nginx</c>: the same for every spelling of a repository.</summary>
    public string Repository => $"{Registry}/{Path}";

    /// <summary>The tag, or <c>latest</c> when neither a tag nor a digest was given.</summary>
    public string? EffectiveTag => Tag ?? (Digest is null ? "latest" : null);

    /// <summary>
    /// Parses a reference. Returns null for an image ID (<c>sha256:…</c> or bare hex) or text that
    /// isn't a reference, since neither names a registry.
    /// </summary>
    public static ImageReference? Parse(string? reference)
    {
        reference = reference?.Trim();
        if (string.IsNullOrEmpty(reference) || reference.StartsWith("sha256:", StringComparison.Ordinal) || reference.Any(char.IsWhiteSpace))
            return null;

        string? digest = null;
        var at = reference.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            digest = reference[(at + 1)..];
            reference = reference[..at];
            if (digest.Length == 0)
                return null;
        }

        string? tag = null;
        var slash = reference.LastIndexOf('/');
        var colon = reference.LastIndexOf(':');
        if (colon > slash)
        {
            tag = reference[(colon + 1)..];
            reference = reference[..colon];
            if (tag.Length == 0)
                return null;
        }
        if (reference.Length == 0 || reference.EndsWith('/') || reference.StartsWith('/'))
            return null;

        // The first component is a registry when it looks like a host: it has a dot or a port, or is localhost.
        var first = reference.IndexOf('/', StringComparison.Ordinal);
        string registry, path;
        if (first > 0 && reference[..first] is var host && (host.Contains('.', StringComparison.Ordinal) || host.Contains(':', StringComparison.Ordinal) || host == "localhost"))
        {
            registry = NormalizeRegistry(host);
            path = reference[(first + 1)..];
        }
        else
        {
            registry = DockerHub;
            path = reference;
        }

        // The grammar of distribution/reference: what an engine would refuse isn't a reference.
        if (!PathPattern().IsMatch(path) || (tag is not null && !TagPattern().IsMatch(tag)) || (digest is not null && !DigestPattern().IsMatch(digest))
            || !HostPattern().IsMatch(registry))
            return null;

        // A bare hex string of ID length is an image ID, not a repository called that.
        if (registry == DockerHub && tag is null && digest is null && path.Length == 64 && path.All(char.IsAsciiHexDigitLower))
            return null;
        if (registry == DockerHub && !path.Contains('/', StringComparison.Ordinal))
            path = "library/" + path;
        return new ImageReference(registry, path, tag, digest);
    }

    /// <summary>
    /// A registry host as stored and matched: lower case, no scheme or path, and Docker Hub's
    /// aliases (<c>index.docker.io</c>, <c>registry-1.docker.io</c>, <c>https://index.docker.io/v1/</c>) as <c>docker.io</c>.
    /// </summary>
    public static string NormalizeRegistry(string address)
    {
        var host = address.Trim();
        var scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            host = host[(scheme + 3)..];
        var path = host.IndexOf('/', StringComparison.Ordinal);
        if (path >= 0)
            host = host[..path];
        host = host.ToLowerInvariant();
        return host is "index.docker.io" or "registry-1.docker.io" or "registry.hub.docker.com" ? DockerHub : host;
    }

    /// <summary>
    /// <c>nginx</c> → (<c>nginx</c>, <c>latest</c>); <c>registry:5000/app:1.2</c> → (<c>registry:5000/app</c>, <c>1.2</c>);
    /// <c>app@sha256:…</c> → (<c>app@sha256:…</c>, null). Without a tag the engine would pull every
    /// tag, so <c>latest</c> is made explicit. These are the parts <c>POST /images/create</c> takes.
    /// </summary>
    public static (string Name, string? Tag) Split(string reference)
    {
        reference = reference.Trim();
        if (reference.Contains('@', StringComparison.Ordinal))
            return (reference, null);

        // A colon after the last slash is a tag; before it, it's a registry port.
        var slash = reference.LastIndexOf('/');
        var colon = reference.LastIndexOf(':');
        return colon > slash ? (reference[..colon], reference[(colon + 1)..]) : (reference, "latest");
    }

    public override string ToString() =>
        $"{Repository}{(Tag is null ? "" : ":" + Tag)}{(Digest is null ? "" : "@" + Digest)}";

    /// <summary>Lower-case components separated by slashes; inside one, single separators ., _, __ or dashes.</summary>
    [GeneratedRegex("^[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*$")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^[\w][\w.-]{0,127}$")]
    private static partial Regex TagPattern();

    [GeneratedRegex("^[a-z0-9]+(?:[.+_-][a-z0-9]+)*:[0-9a-fA-F]{32,}$")]
    private static partial Regex DigestPattern();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*(:\d{1,5})?$")]
    private static partial Regex HostPattern();
}
