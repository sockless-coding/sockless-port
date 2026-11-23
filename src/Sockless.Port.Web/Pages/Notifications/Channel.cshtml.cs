using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Notifications;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Notifications;

/// <summary>Adds (<c>?kind=webhook</c>) or edits a channel. Secrets are write-only: the form never shows them.</summary>
public sealed class ChannelModel(Notifier notifier) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    /// <summary>webhook, ntfy, gotify or email; from the query for a new channel, from the channel otherwise.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Kind { get; set; }

    [BindProperty]
    public ChannelForm Form { get; set; } = new();

    public ChannelInfo? Existing { get; private set; }

    public sealed class ChannelForm
    {
        public string? Name { get; set; }

        public string? Url { get; set; }

        public string? HeaderName { get; set; }

        public string? Host { get; set; }

        public int Port { get; set; } = 587;

        public EmailSecurity Security { get; set; } = EmailSecurity.StartTls;

        public string? UserName { get; set; }

        public string? From { get; set; }

        public string? To { get; set; }

        /// <summary>A new secret; empty keeps the stored one.</summary>
        public string? Secret { get; set; }

        public bool ClearSecret { get; set; }

        public ChannelTarget Target(string kind) => kind switch
        {
            "ntfy" => new NtfyTarget(Url?.Trim() ?? ""),
            "gotify" => new GotifyTarget(Url?.Trim() ?? ""),
            "email" => new EmailTarget(Host?.Trim() ?? "", Port, Security, string.IsNullOrWhiteSpace(UserName) ? null : UserName.Trim(), From?.Trim() ?? "", To?.Trim() ?? ""),
            _ => new WebhookTarget(Url?.Trim() ?? "", string.IsNullOrWhiteSpace(HeaderName) ? null : HeaderName.Trim()),
        };

        public static ChannelForm Of(ChannelInfo channel) => channel.Target switch
        {
            WebhookTarget w => new() { Name = channel.Name, Url = w.Url, HeaderName = w.HeaderName },
            NtfyTarget n => new() { Name = channel.Name, Url = n.Url },
            GotifyTarget g => new() { Name = channel.Name, Url = g.Url },
            EmailTarget e => new() { Name = channel.Name, Host = e.Host, Port = e.Port, Security = e.Security, UserName = e.UserName, From = e.From, To = e.To },
            null => new() { Name = channel.Name },
        };
    }

    public string KindName => Kind switch { "ntfy" => "ntfy", "gotify" => "Gotify", "email" => "Email", _ => "Webhook" };

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (await LoadAsync(cancellationToken) is { } notFound)
            return notFound;
        if (Existing is not null)
            Form = ChannelForm.Of(Existing);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        AuditTrail.Action(HttpContext, Id is null ? "Notifications: add channel" : "Notifications: edit channel");
        AuditTrail.Target(HttpContext, Form.Name?.Trim());
        if (await LoadAsync(cancellationToken) is { } notFound)
            return notFound;

        var (id, errors) = await notifier.SaveChannelAsync(Id, Form.Name, Form.Target(Kind!), Form.Secret, Form.ClearSecret, cancellationToken);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
                ModelState.AddModelError(error.Field.Length == 0 ? "" : $"Form.{error.Field}", error.Message);
            Form.Secret = null;
            return Page();
        }
        Flash.Success(TempData, Id is null
            ? $"Added the channel {Form.Name?.Trim()}. Send it a test, then add a rule that uses it."
            : $"Saved the channel {Form.Name?.Trim()}.");
        return RedirectToPage("/Notifications/Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        AuditTrail.Action(HttpContext, "Notifications: remove channel");
        if (await LoadAsync(cancellationToken) is { } notFound || Existing is null)
            return NotFound();
        AuditTrail.Target(HttpContext, Existing.Name);
        await notifier.DeleteChannelAsync(Existing.Id, cancellationToken);
        Flash.Success(TempData, $"Removed the channel {Existing.Name} and the rules that sent to it.");
        return RedirectToPage("/Notifications/Index");
    }

    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        if (Id is { } id)
        {
            Existing = await notifier.GetChannelAsync(id, cancellationToken);
            if (Existing is null)
                return NotFound();
            Kind = Existing.Target switch
            {
                NtfyTarget => "ntfy",
                GotifyTarget => "gotify",
                EmailTarget => "email",
                WebhookTarget or null => "webhook",
            };
        }
        else if (Kind is not ("webhook" or "ntfy" or "gotify" or "email"))
            return NotFound();
        return null;
    }
}
