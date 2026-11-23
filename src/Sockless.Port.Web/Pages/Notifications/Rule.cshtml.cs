using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Engines;
using Sockless.Port.Notifications;
using Sockless.Port.Settings;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Notifications;

/// <summary>Adds or edits a rule: which alerts, from which endpoint, go to which channel.</summary>
public sealed class RuleModel(Notifier notifier, EngineConnections connections, PortSettings settings) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    [BindProperty]
    public RuleForm Form { get; set; } = new();

    public RuleInfo? Existing { get; private set; }

    public IReadOnlyList<ChannelInfo> Channels { get; private set; } = [];

    public IReadOnlyList<EngineConnection> Endpoints => [.. connections.All.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Resource alerts are checked on the recorded history, which may be off.</summary>
    public bool HistoryOff => settings.Current.ResourceHistoryDays == 0;

    public sealed class RuleForm
    {
        public string? Name { get; set; }

        public bool Enabled { get; set; } = true;

        public int ChannelId { get; set; }

        public int? EndpointId { get; set; }

        /// <summary>Empty unless posted: a form with every box unchecked binds nothing.</summary>
        public List<AlertKind> Kinds { get; set; } = [];

        /// <summary>What a new rule starts with: problems, not news.</summary>
        public static readonly IReadOnlyList<AlertKind> DefaultKinds =
        [
            AlertKind.ContainerExited, AlertKind.ContainerRestarting, AlertKind.ContainerUnhealthy, AlertKind.EndpointOffline,
            AlertKind.Vulnerabilities, AlertKind.TaskFailed,
        ];

        public int QuietMinutes { get; set; } = 60;

        public bool SendResolved { get; set; } = true;

        public int? CpuPercent { get; set; } = 90;

        public int? MemoryPercent { get; set; } = 90;

        public int ForMinutes { get; set; } = 5;

        public RuleInput Input() => new(Name, Enabled, ChannelId, EndpointId, Kinds, QuietMinutes, SendResolved, CpuPercent, MemoryPercent, ForMinutes);

        public static RuleForm From(RuleInfo rule) => new()
        {
            Name = rule.Name, Enabled = rule.Enabled, ChannelId = rule.ChannelId, EndpointId = rule.EndpointId, Kinds = [.. rule.Kinds],
            QuietMinutes = rule.QuietMinutes, SendResolved = rule.SendResolved, CpuPercent = rule.CpuPercent, MemoryPercent = rule.MemoryPercent, ForMinutes = rule.ForMinutes,
        };
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (await LoadAsync(cancellationToken) is { } notFound)
            return notFound;
        if (Existing is not null)
            Form = RuleForm.From(Existing);
        else if (Channels.Count == 0)
        {
            Flash.Error(TempData, "Add a channel first: rules send to one.");
            return RedirectToPage("/Notifications/Index");
        }
        else
        {
            Form.ChannelId = Channels[0].Id;
            Form.Kinds = [.. RuleForm.DefaultKinds];
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        AuditTrail.Action(HttpContext, Id is null ? "Notifications: add rule" : "Notifications: edit rule");
        AuditTrail.Target(HttpContext, Form.Name?.Trim());
        if (await LoadAsync(cancellationToken) is { } notFound)
            return notFound;
        var (_, errors) = await notifier.SaveRuleAsync(Id, Form.Input(), cancellationToken);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
                ModelState.AddModelError(error.Field.Length == 0 ? "" : $"Form.{error.Field}", error.Message);
            return Page();
        }
        Flash.Success(TempData, $"Saved the rule {Form.Name?.Trim()}.");
        return RedirectToPage("/Notifications/Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        AuditTrail.Action(HttpContext, "Notifications: remove rule");
        if (await LoadAsync(cancellationToken) is { } notFound || Existing is null)
            return NotFound();
        AuditTrail.Target(HttpContext, Existing.Name);
        await notifier.DeleteRuleAsync(Existing.Id, cancellationToken);
        Flash.Success(TempData, $"Removed the rule {Existing.Name}.");
        return RedirectToPage("/Notifications/Index");
    }

    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        Channels = await notifier.ChannelsAsync(cancellationToken);
        if (Id is { } id)
        {
            Existing = (await notifier.RuleInfosAsync(cancellationToken)).FirstOrDefault(r => r.Id == id);
            if (Existing is null)
                return NotFound();
        }
        return null;
    }
}
