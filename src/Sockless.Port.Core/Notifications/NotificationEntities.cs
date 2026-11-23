using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sockless.Port.Notifications;

/// <summary>Where a channel sends to, by kind. Stored as JSON; its secret is kept apart, encrypted.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(WebhookTarget), "webhook")]
[JsonDerivedType(typeof(NtfyTarget), "ntfy")]
[JsonDerivedType(typeof(GotifyTarget), "gotify")]
[JsonDerivedType(typeof(EmailTarget), "email")]
public closed record ChannelTarget
{
    public string KindName => this switch
    {
        WebhookTarget => "Webhook",
        NtfyTarget => "ntfy",
        GotifyTarget => "Gotify",
        EmailTarget => "Email",
    };

    /// <summary>Where it goes, for the channel list (never the secret).</summary>
    public string Destination => this switch
    {
        WebhookTarget w => w.Url,
        NtfyTarget n => n.Url,
        GotifyTarget g => g.Url,
        EmailTarget e => e.To,
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ChannelTarget? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ChannelTarget>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A JSON POST to any URL. The secret, when set, goes in <paramref name="HeaderName"/> (default <c>Authorization</c>).</summary>
public sealed record WebhookTarget(string Url, string? HeaderName) : ChannelTarget;

/// <summary>An ntfy topic: <c>https://ntfy.sh/my-topic</c>. The secret, when set, is an access token.</summary>
public sealed record NtfyTarget(string Url) : ChannelTarget;

/// <summary>A Gotify server: <c>https://gotify.example.com</c>. The secret is the application's token.</summary>
public sealed record GotifyTarget(string Url) : ChannelTarget;

public enum EmailSecurity
{
    /// <summary>STARTTLS: plain connection upgraded to TLS (usually port 587). Required.</summary>
    StartTls,

    /// <summary>No encryption, for a relay on a trusted network (port 25).</summary>
    None,
}

/// <param name="To">Comma-separated addresses.</param>
public sealed record EmailTarget(string Host, int Port, EmailSecurity Security, string? UserName, string From, string To) : ChannelTarget;

/// <summary>Somewhere notifications are sent.</summary>
public sealed class NotificationChannel
{
    public const int MaxName = 64;

    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary><see cref="ChannelTarget"/> as JSON.</summary>
    public required string Target { get; set; }

    /// <summary>The token, password or header value, encrypted with Data Protection; null when there's none.</summary>
    public string? ProtectedSecret { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastSentAt { get; set; }

    public DateTimeOffset? LastFailedAt { get; set; }

    /// <summary>Why the last delivery failed; cleared by the next one that works.</summary>
    public string? LastError { get; set; }
}

/// <summary>Which alerts go to which channel.</summary>
public sealed class NotificationRule
{
    public const int MaxName = 64;

    public int Id { get; set; }

    public required string Name { get; set; }

    public bool Enabled { get; set; } = true;

    public int ChannelId { get; set; }

    /// <summary>Null for every endpoint.</summary>
    public int? EndpointId { get; set; }

    /// <summary><see cref="AlertKind"/> names, comma-separated.</summary>
    public string Kinds { get; set; } = "";

    /// <summary>After sending about something, stay quiet about the same thing this long.</summary>
    public int QuietMinutes { get; set; } = 60;

    /// <summary>Also say when the problem is over.</summary>
    public bool SendResolved { get; set; } = true;

    /// <summary>For <see cref="AlertKind.ResourceUsage"/>: CPU as a share of the host, in percent; null to ignore CPU.</summary>
    public int? CpuPercent { get; set; }

    /// <summary>For <see cref="AlertKind.ResourceUsage"/>: memory as a share of the host's, in percent; null to ignore memory.</summary>
    public int? MemoryPercent { get; set; }

    /// <summary>For <see cref="AlertKind.ResourceUsage"/>: how many minutes in a row it must stay above.</summary>
    public int ForMinutes { get; set; } = 5;

    public DateTimeOffset CreatedAt { get; set; }

    public IReadOnlySet<AlertKind> KindSet =>
        Kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => Enum.TryParse<AlertKind>(k, out var kind) ? kind : (AlertKind?)null)
            .OfType<AlertKind>().ToHashSet();
}

public enum NotificationOutcome
{
    Sent,
    Failed,
}

/// <summary>A notification that was sent, or failed to be. Kept as long as the audit log.</summary>
public sealed class NotificationEntry
{
    public const int MaxTitle = 256;
    public const int MaxMessage = 2048;

    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    public AlertKind Kind { get; set; }

    public AlertSeverity Severity { get; set; }

    /// <summary>A "resolved" notice rather than an alert.</summary>
    public bool Resolved { get; set; }

    public bool Test { get; set; }

    public string? EndpointName { get; set; }

    public required string Title { get; set; }

    public string Message { get; set; } = "";

    public required string ChannelName { get; set; }

    public string? RuleName { get; set; }

    public NotificationOutcome Outcome { get; set; }

    public string? Error { get; set; }
}
