using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Data;
using Sockless.Port.Settings;

namespace Sockless.Port.Notifications;

/// <summary>A channel as the pages show it, without its secret.</summary>
/// <param name="SecretReadable">False when the Data Protection keys that encrypted the secret are gone, so it must be entered again.</param>
public sealed record ChannelInfo(int Id, string Name, ChannelTarget? Target, bool HasSecret, bool SecretReadable, DateTimeOffset? LastSentAt,
    DateTimeOffset? LastFailedAt, string? LastError);

/// <summary>A rule as the pages show it.</summary>
public sealed record RuleInfo(int Id, string Name, bool Enabled, int ChannelId, string ChannelName, int? EndpointId, IReadOnlySet<AlertKind> Kinds,
    int QuietMinutes, bool SendResolved, int? CpuPercent, int? MemoryPercent, int ForMinutes);

/// <summary>What a rule form posts.</summary>
public sealed record RuleInput(string? Name, bool Enabled, int ChannelId, int? EndpointId, IReadOnlyCollection<AlertKind> Kinds, int QuietMinutes, bool SendResolved,
    int? CpuPercent, int? MemoryPercent, int ForMinutes);

/// <summary>A problem with a form field.</summary>
public sealed record NotificationFieldError(string Field, string Message);

public sealed record NotificationPage(IReadOnlyList<NotificationEntry> Entries, int Page, bool HasMore);

/// <summary>
/// Notifications: the channels they go to, the rules that choose them, and the alerts that set
/// them off. An alert fires once until it clears (<see cref="RaiseAsync"/>, <see cref="ClearAsync"/>);
/// each rule then stays quiet about the same alert for its quiet period. Deliveries are queued,
/// sent in the background with two retries, and recorded in the history. Which alerts are active
/// is kept in memory only, so a restart forgets them.
/// </summary>
public sealed partial class Notifier(IServiceScopeFactory scopes, IDataProtectionProvider protection, NotificationSender sender, TimeProvider time,
    ILogger<Notifier> logger) : BackgroundService
{
    public const int PageSize = 50;
    public const int MaxChannels = 50;
    public const int MaxRules = 200;
    public const string PublicUrlKey = "notifications-public-url";

    /// <summary>The quiet periods offered, in minutes.</summary>
    public static readonly IReadOnlyList<int> QuietChoices = [0, 15, 60, 6 * 60, 24 * 60];

    private const string Purpose = "Sockless.Port.Notifications.Secret.v1";
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);
    private readonly Channel<Delivery> _queue = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ConcurrentDictionary<string, Alert> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(int Rule, string Key), DateTimeOffset> _lastSent = new();
    /// <summary>The rules that sent each active alert this time round: only they say it's resolved.</summary>
    private readonly ConcurrentDictionary<string, List<int>> _sentBy = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<NotificationRule>? _rules;
    private volatile string? _publicUrl;
    private volatile bool _publicUrlLoaded;

    internal sealed record Delivery(int ChannelId, string? RuleName, NotificationMessage Message);

    /// <summary>Alerts that fired and haven't cleared, by key.</summary>
    public IReadOnlyCollection<Alert> Active => [.. _active.Values];

    public bool IsActive(string key) => _active.ContainsKey(key);

    // Alerts.

    /// <summary>Sends <paramref name="alert"/> to the rules that want it, unless it's active already.</summary>
    public async Task RaiseAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        if (!_active.TryAdd(alert.Key, alert))
            return;
        var now = time.GetUtcNow();
        List<int> sentBy = [];
        foreach (var rule in await RulesForAsync(alert, cancellationToken))
        {
            var quiet = TimeSpan.FromMinutes(rule.QuietMinutes);
            if (_lastSent.TryGetValue((rule.Id, alert.Key), out var last) && now - last < quiet)
                continue;
            _lastSent[(rule.Id, alert.Key)] = now;
            sentBy.Add(rule.Id);
            await EnqueueAsync(rule.ChannelId, rule.Name, alert, resolved: false, now, cancellationToken);
        }
        _sentBy[alert.Key] = sentBy;
    }

    /// <summary>The alert is over. Rules that sent it, and want to, say so.</summary>
    /// <param name="announce">Send "resolved" notices; false when the thing is simply gone (a removed container).</param>
    public async Task ClearAsync(string key, bool announce = true, CancellationToken cancellationToken = default)
    {
        if (!_active.TryRemove(key, out var alert))
            return;
        _sentBy.TryRemove(key, out var sentBy);
        if (!announce || !alert.Resolvable || sentBy is not { Count: > 0 })
            return;
        var now = time.GetUtcNow();
        foreach (var rule in await RulesForAsync(alert, cancellationToken))
        {
            // Only rules that told someone about it this time (not those its quiet period held back).
            if (rule.SendResolved && sentBy.Contains(rule.Id))
                await EnqueueAsync(rule.ChannelId, rule.Name, alert, resolved: true, now, cancellationToken);
        }
    }

    /// <summary>Clears every active alert whose key starts with <paramref name="prefix"/>.</summary>
    public async Task ClearPrefixAsync(string prefix, bool announce, CancellationToken cancellationToken = default)
    {
        foreach (var key in _active.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            await ClearAsync(key, announce, cancellationToken);
    }

    /// <summary>The enabled rules with a resource threshold for the endpoint, for <see cref="AlertKind.ResourceUsage"/>.</summary>
    public async Task<IReadOnlyList<NotificationRule>> ResourceRulesAsync(int endpointId, CancellationToken cancellationToken = default) =>
        [.. (await RulesAsync(cancellationToken)).Where(r => r.Enabled && (r.EndpointId is null || r.EndpointId == endpointId)
            && r.KindSet.Contains(AlertKind.ResourceUsage) && (r.CpuPercent is not null || r.MemoryPercent is not null))];

    private async Task<IEnumerable<NotificationRule>> RulesForAsync(Alert alert, CancellationToken cancellationToken) =>
        (await RulesAsync(cancellationToken)).Where(r => r.Enabled && r.KindSet.Contains(alert.Kind)
            && (alert.RuleId is null || alert.RuleId == r.Id)
            && (r.EndpointId is null || alert.EndpointId is null || r.EndpointId == alert.EndpointId));

    private async Task EnqueueAsync(int channelId, string ruleName, Alert alert, bool resolved, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var message = new NotificationMessage(alert.Kind, alert.Severity, resolved ? $"Resolved: {alert.Title}" : alert.Title, resolved ? "" : alert.Message,
            alert.EndpointName, await AbsoluteAsync(alert.Link, cancellationToken), now, resolved);
        if (!_queue.Writer.TryWrite(new Delivery(channelId, ruleName, message)))
            LogDropped(logger, alert.Title);
    }

    // Delivery.

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Several at once, so one slow server doesn't hold up the rest.
        using var slots = new SemaphoreSlim(4);
        List<Task> running = [];
        try
        {
            await foreach (var delivery in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await slots.WaitAsync(stoppingToken);
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        await DeliverAsync(delivery, stoppingToken);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await Task.WhenAll(running);
    }

    /// <summary>For tests: delivers what's queued now, without retries, instead of the background loop.</summary>
    /// <returns>How many were delivered or failed.</returns>
    internal async Task<int> DeliverQueuedAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        while (_queue.Reader.TryRead(out var delivery))
        {
            await DeliverAsync(delivery, cancellationToken, retry: false);
            count++;
        }
        return count;
    }

    /// <summary>Delivers one notification, retrying twice, and records how it went.</summary>
    /// <returns>Why it failed, or null when it was delivered.</returns>
    internal async Task<string?> DeliverAsync(Delivery delivery, CancellationToken cancellationToken, bool retry = true)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var channel = await db.NotificationChannels.FirstOrDefaultAsync(c => c.Id == delivery.ChannelId, cancellationToken);
        if (channel is null)
            return "The channel doesn't exist.";

        string? error = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (ChannelTarget.FromJson(channel.Target) is not { } target)
                    throw new DeliveryException("The channel's settings can't be read. Edit and save it again.");
                await sender.SendAsync(target, Secret(channel), delivery.Message, cancellationToken);
                error = null;
                break;
            }
            catch (DeliveryException ex)
            {
                error = ex.Message;
            }
            catch (CryptographicException)
            {
                error = "The channel's secret can't be decrypted: the encryption keys changed. Enter it again.";
                break;
            }
            if (!retry || attempt >= RetryDelays.Length)
                break;
            try
            {
                await Task.Delay(RetryDelays[attempt], time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return error;
            }
        }

        var now = time.GetUtcNow();
        if (error is null)
        {
            channel.LastSentAt = now;
            channel.LastError = null;
        }
        else
        {
            channel.LastFailedAt = now;
            channel.LastError = Truncate(error, 512);
            LogDeliveryFailed(logger, delivery.Message.Title, channel.Name, error);
        }
        var m = delivery.Message;
        db.NotificationHistory.Add(new NotificationEntry
        {
            At = now, Kind = m.Kind, Severity = m.Severity, Resolved = m.Resolved, Test = m.Test, EndpointName = m.EndpointName,
            Title = Truncate(m.Title, NotificationEntry.MaxTitle), Message = Truncate(m.Message, NotificationEntry.MaxMessage),
            ChannelName = channel.Name, RuleName = delivery.RuleName, Outcome = error is null ? NotificationOutcome.Sent : NotificationOutcome.Failed, Error = error,
        });
        await db.SaveChangesAsync(CancellationToken.None);
        return error;
    }

    /// <summary>Sends a test notification now, without retries.</summary>
    /// <returns>Why it failed, or null when it was delivered.</returns>
    public async Task<string?> SendTestAsync(int channelId, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var message = new NotificationMessage(AlertKind.TaskFailed, AlertSeverity.Info, "Test notification from Sockless Port",
            "If you can read this, the channel works.", null, await AbsoluteAsync("/notifications", cancellationToken), now, Test: true);
        return await DeliverAsync(new Delivery(channelId, null, message), cancellationToken, retry: false);
    }

    // The server's address, for links.

    /// <summary>The address people reach this server at, e.g. <c>https://port.example.com</c>; null when it isn't set, and notifications carry no links.</summary>
    public async Task<string?> PublicUrlAsync(CancellationToken cancellationToken = default)
    {
        if (_publicUrlLoaded)
            return _publicUrl;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        _publicUrl = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == PublicUrlKey, cancellationToken))?.Value;
        _publicUrlLoaded = true;
        return _publicUrl;
    }

    /// <returns>A problem, or null when it was saved.</returns>
    public async Task<string?> SavePublicUrlAsync(string? url, CancellationToken cancellationToken = default)
    {
        url = url?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(url) && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                                           || uri.Query.Length > 0 || uri.Fragment.Length > 0 || url.Length > 512))
            return "Enter the address people open Sockless Port at, e.g. https://port.example.com.";

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var entry = await db.Settings.FirstOrDefaultAsync(s => s.Key == PublicUrlKey, cancellationToken);
        if (string.IsNullOrEmpty(url))
        {
            if (entry is not null)
                db.Settings.Remove(entry);
        }
        else if (entry is null)
            db.Settings.Add(new SettingEntry { Key = PublicUrlKey, Value = url });
        else
            entry.Value = url;
        await db.SaveChangesAsync(cancellationToken);
        _publicUrl = string.IsNullOrEmpty(url) ? null : url;
        _publicUrlLoaded = true;
        return null;
    }

    private async Task<string?> AbsoluteAsync(string? link, CancellationToken cancellationToken) =>
        link is not null && await PublicUrlAsync(cancellationToken) is { } root ? root + link : null;

    // Channels.

    public async Task<IReadOnlyList<ChannelInfo>> ChannelsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var rows = await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken);
        return [.. rows.Select(Info)];
    }

    public async Task<ChannelInfo?> GetChannelAsync(int id, CancellationToken cancellationToken = default) =>
        (await ChannelsAsync(cancellationToken)).FirstOrDefault(c => c.Id == id);

    /// <summary>Adds (<paramref name="id"/> null) or changes a channel.</summary>
    /// <param name="secret">A new secret; null keeps the stored one.</param>
    /// <param name="clearSecret">Remove the stored secret.</param>
    /// <returns>The channel's id, or what's wrong (nothing was saved then).</returns>
    public async Task<(int? Id, IReadOnlyList<NotificationFieldError> Errors)> SaveChannelAsync(int? id, string? name, ChannelTarget target, string? secret,
        bool clearSecret, CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? "";
        secret = string.IsNullOrEmpty(secret) ? null : secret;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var existing = await db.NotificationChannels.ToListAsync(cancellationToken);
        var channel = id is null ? null : existing.FirstOrDefault(c => c.Id == id);
        if (id is not null && channel is null)
            return (null, [new("", "The channel doesn't exist any more.")]);

        List<NotificationFieldError> errors = [.. Validate(target)];
        if (name.Length == 0)
            errors.Add(new("Name", "Give the channel a name."));
        else if (name.Length > NotificationChannel.MaxName)
            errors.Add(new("Name", $"Use at most {NotificationChannel.MaxName} characters."));
        else if (existing.Any(c => c.Id != id && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            errors.Add(new("Name", $"There's already a channel named {name}."));
        if (id is null && existing.Count >= MaxChannels)
            errors.Add(new("Name", $"There are {MaxChannels} channels already."));
        var keepsSecret = channel?.ProtectedSecret is not null && !clearSecret;
        if (target is GotifyTarget && secret is null && !keepsSecret)
            errors.Add(new("Secret", "Enter the Gotify application's token."));
        if (target is EmailTarget { UserName.Length: > 0 } && secret is null && !keepsSecret)
            errors.Add(new("Secret", "Enter the password for the user name, or leave the user name empty."));
        if (channel is not null && ChannelTarget.FromJson(channel.Target) is { } old && old.GetType() != target.GetType())
            errors.Add(new("", "A channel's kind can't change. Add another channel instead."));
        if (errors.Count > 0)
            return (null, errors);

        var now = time.GetUtcNow();
        if (channel is null)
        {
            channel = new NotificationChannel { Name = name, Target = target.ToJson(), CreatedAt = now };
            db.NotificationChannels.Add(channel);
        }
        channel.Name = name;
        channel.Target = target.ToJson();
        channel.UpdatedAt = now;
        if (secret is not null)
            channel.ProtectedSecret = _protector.Protect(secret);
        else if (clearSecret)
            channel.ProtectedSecret = null;
        await db.SaveChangesAsync(cancellationToken);
        _rules = null;
        return (channel.Id, []);
    }

    /// <summary>Removes a channel and the rules that send to it.</summary>
    public async Task<bool> DeleteChannelAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var removed = await db.NotificationChannels.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
        _rules = null;
        return removed;
    }

    internal static IEnumerable<NotificationFieldError> Validate(ChannelTarget target)
    {
        static bool IsHttp(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && url!.Length <= 1024;

        switch (target)
        {
            case WebhookTarget w:
                if (!IsHttp(w.Url))
                    yield return new("Url", "Enter the webhook's http:// or https:// address.");
                if (w.HeaderName is { Length: > 0 } header && !System.Text.RegularExpressions.Regex.IsMatch(header, "^[A-Za-z0-9-]{1,64}$"))
                    yield return new("HeaderName", "Use a header name: letters, digits and -.");
                break;
            case NtfyTarget n:
                if (!IsHttp(n.Url) || new Uri(n.Url).AbsolutePath.Trim('/').Length == 0)
                    yield return new("Url", "Enter the topic's address, e.g. https://ntfy.sh/my-topic.");
                break;
            case GotifyTarget g:
                if (!IsHttp(g.Url))
                    yield return new("Url", "Enter the Gotify server's address, e.g. https://gotify.example.com.");
                break;
            case EmailTarget e:
                if (string.IsNullOrWhiteSpace(e.Host) || e.Host.Length > 255 || e.Host.Any(char.IsWhiteSpace))
                    yield return new("Host", "Enter the mail server's host name.");
                if (e.Port is < 1 or > 65535)
                    yield return new("Port", "Enter a port between 1 and 65535 (587 for STARTTLS, 25 for a plain relay).");
                if (!IsAddress(e.From))
                    yield return new("From", "Enter the address mail is sent from.");
                IReadOnlyList<System.Net.Mail.MailAddress>? to = null;
                try
                {
                    to = NotificationSender.Addresses(e.To);
                }
                catch (FormatException)
                {
                }
                if (to is not { Count: > 0 and <= 20 })
                    yield return new("To", "Enter one or more addresses, separated by commas.");
                break;
        }
    }

    private static bool IsAddress(string? address)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(address) && new System.Net.Mail.MailAddress(address).Address.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private string? Secret(NotificationChannel channel) => channel.ProtectedSecret is null ? null : _protector.Unprotect(channel.ProtectedSecret);

    private ChannelInfo Info(NotificationChannel c)
    {
        var readable = true;
        if (c.ProtectedSecret is not null)
        {
            try
            {
                _protector.Unprotect(c.ProtectedSecret);
            }
            catch (CryptographicException)
            {
                readable = false;
            }
        }
        return new ChannelInfo(c.Id, c.Name, ChannelTarget.FromJson(c.Target), c.ProtectedSecret is not null, readable, c.LastSentAt, c.LastFailedAt, c.LastError);
    }

    // Rules.

    public async Task<IReadOnlyList<RuleInfo>> RuleInfosAsync(CancellationToken cancellationToken = default)
    {
        var channels = (await ChannelsAsync(cancellationToken)).ToDictionary(c => c.Id, c => c.Name);
        return [.. (await RulesAsync(cancellationToken)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => new RuleInfo(r.Id, r.Name, r.Enabled, r.ChannelId,
            channels.GetValueOrDefault(r.ChannelId, "?"), r.EndpointId, r.KindSet, r.QuietMinutes, r.SendResolved, r.CpuPercent, r.MemoryPercent, r.ForMinutes))];
    }

    /// <returns>The rule's id, or what's wrong (nothing was saved then).</returns>
    public async Task<(int? Id, IReadOnlyList<NotificationFieldError> Errors)> SaveRuleAsync(int? id, RuleInput input, CancellationToken cancellationToken = default)
    {
        var name = input.Name?.Trim() ?? "";
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var rules = await db.NotificationRules.ToListAsync(cancellationToken);
        var rule = id is null ? null : rules.FirstOrDefault(r => r.Id == id);
        if (id is not null && rule is null)
            return (null, [new("", "The rule doesn't exist any more.")]);

        List<NotificationFieldError> errors = [];
        if (name.Length == 0)
            errors.Add(new("Name", "Give the rule a name."));
        else if (name.Length > NotificationRule.MaxName)
            errors.Add(new("Name", $"Use at most {NotificationRule.MaxName} characters."));
        else if (rules.Any(r => r.Id != id && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            errors.Add(new("Name", $"There's already a rule named {name}."));
        if (id is null && rules.Count >= MaxRules)
            errors.Add(new("Name", $"There are {MaxRules} rules already."));
        if (!await db.NotificationChannels.AnyAsync(c => c.Id == input.ChannelId, cancellationToken))
            errors.Add(new("ChannelId", "Choose a channel."));
        if (input.EndpointId is { } endpoint && !await db.Endpoints.AnyAsync(e => e.Id == endpoint, cancellationToken))
            errors.Add(new("EndpointId", "Choose an endpoint from the list."));
        if (input.Kinds.Count == 0)
            errors.Add(new("Kinds", "Choose at least one thing to send."));
        if (!QuietChoices.Contains(input.QuietMinutes))
            errors.Add(new("QuietMinutes", "Choose a quiet period from the list."));
        if (input.Kinds.Contains(AlertKind.ResourceUsage))
        {
            if (input.CpuPercent is null && input.MemoryPercent is null)
                errors.Add(new("CpuPercent", "Set a CPU or memory threshold, or don't send resource alerts."));
            if (input.CpuPercent is < 1 or > 100)
                errors.Add(new("CpuPercent", "Use a share of the host between 1 and 100 percent."));
            if (input.MemoryPercent is < 1 or > 100)
                errors.Add(new("MemoryPercent", "Use a share of the host's memory between 1 and 100 percent."));
            if (input.ForMinutes is < 1 or > 120)
                errors.Add(new("ForMinutes", "Use between 1 and 120 minutes."));
        }
        if (errors.Count > 0)
            return (null, errors);

        if (rule is null)
        {
            rule = new NotificationRule { Name = name, CreatedAt = time.GetUtcNow() };
            db.NotificationRules.Add(rule);
        }
        rule.Name = name;
        rule.Enabled = input.Enabled;
        rule.ChannelId = input.ChannelId;
        rule.EndpointId = input.EndpointId;
        rule.Kinds = string.Join(',', input.Kinds.Distinct().Order());
        rule.QuietMinutes = input.QuietMinutes;
        rule.SendResolved = input.SendResolved;
        var resources = input.Kinds.Contains(AlertKind.ResourceUsage);
        rule.CpuPercent = resources ? input.CpuPercent : null;
        rule.MemoryPercent = resources ? input.MemoryPercent : null;
        rule.ForMinutes = resources ? input.ForMinutes : 5;
        await db.SaveChangesAsync(cancellationToken);
        _rules = null;
        return (rule.Id, []);
    }

    public async Task<bool> DeleteRuleAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var removed = await db.NotificationRules.Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
        _rules = null;
        return removed;
    }

    private async Task<IReadOnlyList<NotificationRule>> RulesAsync(CancellationToken cancellationToken)
    {
        if (_rules is { } cached)
            return cached;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var rules = await db.NotificationRules.AsNoTracking().ToListAsync(cancellationToken);
        _rules = rules;
        return rules;
    }

    // History.

    public async Task<NotificationPage> HistoryAsync(int page, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var rows = await db.NotificationHistory.AsNoTracking().OrderByDescending(e => e.Id)
            .Skip((page - 1) * PageSize).Take(PageSize + 1).ToListAsync(cancellationToken);
        return new NotificationPage([.. rows.Take(PageSize)], page, rows.Count > PageSize);
    }

    /// <summary>Deletes history older than <paramref name="retention"/> (the audit log's).</summary>
    public async Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        var cutoff = time.GetUtcNow() - retention;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var old = await db.NotificationHistory.Where(e => e.At < cutoff).Select(e => e.Id).ToListAsync(cancellationToken);
        var removed = 0;
        foreach (var chunk in old.Chunk(500))
            removed += await db.NotificationHistory.Where(e => chunk.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
        return removed;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [LoggerMessage(Level = LogLevel.Warning, Message = "The notification queue is full; dropped \"{Title}\"")]
    private static partial void LogDropped(ILogger logger, string title);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't send \"{Title}\" to {Channel}: {Reason}")]
    private static partial void LogDeliveryFailed(ILogger logger, string title, string channel, string reason);
}
