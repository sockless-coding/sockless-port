namespace Sockless.Port.Templates;

/// <summary>
/// A template feed an administrator added. The feed itself is kept in
/// <c>{DataPath}/templates/feeds/{Id}.json</c>, as it was last downloaded.
/// </summary>
public sealed class TemplateSource
{
    public const int MaxName = 64;
    public const int MaxUrl = 1024;

    public int Id { get; set; }

    public required string Name { get; set; }

    public required string Url { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the feed was last downloaded and read successfully.</summary>
    public DateTimeOffset? FetchedAt { get; set; }

    /// <summary>When it was last tried, successfully or not.</summary>
    public DateTimeOffset? AttemptedAt { get; set; }

    /// <summary>Why the last try failed; null when it worked.</summary>
    public string? Error { get; set; }

    public int TemplateCount { get; set; }

    public TemplateFeedFormat? Format { get; set; }
}
