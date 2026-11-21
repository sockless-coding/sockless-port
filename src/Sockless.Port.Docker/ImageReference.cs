namespace Sockless.Port.Docker;

/// <summary>Splits an image reference into the parts <c>POST /images/create</c> takes.</summary>
public static class ImageReference
{
    /// <summary>
    /// <c>nginx</c> → (<c>nginx</c>, <c>latest</c>); <c>registry:5000/app:1.2</c> → (<c>registry:5000/app</c>, <c>1.2</c>);
    /// <c>app@sha256:…</c> → (<c>app@sha256:…</c>, null). Without a tag the engine would pull every
    /// tag, so <c>latest</c> is made explicit.
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
}
