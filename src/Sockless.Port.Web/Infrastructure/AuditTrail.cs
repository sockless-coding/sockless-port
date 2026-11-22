using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Sockless.Port.Audit;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>A post handler that changes nothing (validation, previews, preferences), left out of the audit log.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class NoAuditAttribute : Attribute;

/// <summary>
/// What a page tells the audit log about the current post, when the defaults don't say it well.
/// The defaults: the action from the page's folder and the handler (or the form's <c>action</c>
/// field), e.g. <c>Containers: stop</c>; the target from the page (<see cref="EndpointPageModel.AuditTarget"/>),
/// else the route's values and the form's <c>ids</c> or <c>name</c>.
/// </summary>
public static partial class AuditTrail
{
    private const string ActionKey = "audit.action";
    private const string TargetKey = "audit.target";
    private const string DeniedKey = "audit.denied";
    private const string SkipKey = "audit.skip";

    public static void Action(HttpContext context, string action) => context.Items[ActionKey] = action;

    public static void Target(HttpContext context, string? target) => context.Items[TargetKey] = target;

    /// <summary>The post was refused because of the user's role.</summary>
    public static void Deny(HttpContext context, string reason) => context.Items[DeniedKey] = reason;

    /// <summary>Leaves this post out, e.g. when the page writes its own entry.</summary>
    public static void Skip(HttpContext context) => context.Items[SkipKey] = true;

    internal static string? ActionOf(HttpContext context) => context.Items[ActionKey] as string;
    internal static string? TargetOf(HttpContext context) => context.Items[TargetKey] as string;
    internal static string? DeniedOf(HttpContext context) => context.Items[DeniedKey] as string;
    internal static bool Skipped(HttpContext context) => context.Items.ContainsKey(SkipKey);

    /// <summary>Writes an entry for something outside a page post, e.g. a sign-in or a hub call.</summary>
    public static Task WriteAsync(HttpContext context, string action, string? target = null, EngineConnection? endpoint = null,
        AuditOutcome outcome = AuditOutcome.Succeeded, string? detail = null, string? userName = null)
    {
        var access = context.Access();
        return context.RequestServices.GetRequiredService<AuditLog>().WriteAsync(new AuditRecord(
            access?.UserId, userName ?? access?.UserName ?? context.User.Identity?.Name ?? "anonymous", action, target,
            endpoint?.Id, endpoint?.Name, outcome, detail, context.Connection.RemoteIpAddress?.ToString()));
    }

    /// <summary>"Swarm services" for <c>/Engine/Swarm/Services/Edit</c>, "Users" for <c>/Users/Edit</c>.</summary>
    internal static string Area(string page)
    {
        var parts = page.Trim('/').Split('/');
        var folders = parts.Length > 1 ? parts[..^1] : parts;
        if (folders is ["Engine", .. var rest])
            folders = rest.Length == 0 ? ["Dashboard"] : rest;
        var area = string.Join(" ", folders.Select(f => f.ToLowerInvariant()));
        return area.Length == 0 ? "Page" : char.ToUpperInvariant(area[0]) + area[1..];
    }

    /// <summary>"remove volumes" for <c>RemoveVolumes</c>.</summary>
    internal static string Words(string name) => SplitWords().Replace(name, " $1").Trim().ToLowerInvariant().Replace('-', ' ');

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex SplitWords();
}

/// <summary>Records every post to a page in the audit log: who, what, which target, which endpoint, and how it went.</summary>
internal sealed class AuditFilter(AuditLog audit, ITempDataDictionaryFactory tempData) : IAsyncPageFilter, IOrderedFilter
{
    private static readonly string[] IgnoredRouteKeys = ["page", "handler", "area", EndpointSelection.RouteKey];

    /// <summary>Before the page model's own filter, so posts it refuses are recorded too.</summary>
    public int Order => int.MinValue;

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
            || context.HandlerMethod?.MethodInfo.IsDefined(typeof(NoAuditAttribute), inherit: true) == true
            || context.HandlerInstance.GetType().IsDefined(typeof(NoAuditAttribute), inherit: true))
        {
            await next();
            return;
        }

        var executed = await next();
        if (AuditTrail.Skipped(http))
            return;

        var page = context.ActionDescriptor.ViewEnginePath;
        var model = context.HandlerInstance as EndpointPageModel;
        // Null when the page stopped before resolving its endpoint (an unknown one).
        var endpoint = (EngineConnection?)model?.Connection;
        var (outcome, detail) = Outcome(http, executed, tempData.GetTempData(http), context.ModelState);
        var access = http.Access();
        // Already read for the antiforgery check; this returns it without reading the body again.
        var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(CancellationToken.None) : null;

        await audit.WriteAsync(new AuditRecord(
            access?.UserId,
            access?.UserName ?? http.User.Identity?.Name ?? "anonymous",
            AuditTrail.ActionOf(http) ?? $"{AuditTrail.Area(page)}: {Verb(http, form, context.HandlerMethod?.Name, page)}",
            AuditTrail.TargetOf(http) ?? (endpoint is not null ? model!.AuditTarget : null) ?? RouteTarget(http, form),
            endpoint?.Id,
            endpoint?.Name,
            outcome,
            detail,
            http.Connection.RemoteIpAddress?.ToString()));
    }

    private static (AuditOutcome, string?) Outcome(HttpContext http, PageHandlerExecutedContext executed, ITempDataDictionary tempData,
        Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelState)
    {
        if (AuditTrail.DeniedOf(http) is { } denied)
            return (AuditOutcome.Denied, denied);
        if (executed.Exception is { } exception && !executed.ExceptionHandled)
            return (AuditOutcome.Failed, $"Server error: {exception.GetType().Name}");
        if (executed.Result is StatusCodeResult { StatusCode: StatusCodes.Status403Forbidden } or ForbidResult)
            return (AuditOutcome.Denied, null);
        if (executed.Result is NotFoundResult or NotFoundObjectResult)
            return (AuditOutcome.Failed, "Not found.");
        if (Flash.PeekError(tempData) is { } error)
            return (AuditOutcome.Failed, error);
        if (!modelState.IsValid)
        {
            var first = modelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault(m => m.Length > 0);
            return (AuditOutcome.Failed, first ?? "The form had errors.");
        }
        return (AuditOutcome.Succeeded, null);
    }

    /// <summary>The form's <c>action</c> (bulk and action menus), else the handler, else the page (<c>Create</c>, <c>Edit</c>).</summary>
    private static string Verb(HttpContext http, IFormCollection? form, string? handler, string page)
    {
        if (form?["action"].FirstOrDefault() is { Length: > 0 and < 32 } action)
            return AuditTrail.Words(action);
        if (http.Request.Query["action"].FirstOrDefault() is { Length: > 0 and < 32 } queryAction)
            return AuditTrail.Words(queryAction);
        if (!string.IsNullOrEmpty(handler))
            return AuditTrail.Words(handler);
        var name = page[(page.LastIndexOf('/') + 1)..];
        return AuditTrail.Words(name == "Index" ? "Save" : name);
    }

    private static string? RouteTarget(HttpContext http, IFormCollection? form)
    {
        List<string> parts = [];
        foreach (var (key, value) in http.GetRouteData().Values)
        {
            if (!IgnoredRouteKeys.Contains(key, StringComparer.OrdinalIgnoreCase) && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text)
                parts.Add(Short(text));
        }
        if (form is not null)
        {
            var ids = form["ids"].Concat(http.Request.Query["ids"]).Where(v => !string.IsNullOrEmpty(v)).Select(v => Short(v!)).Distinct().ToList();
            if (ids.Count > 0)
                parts.AddRange(ids);
            else if (parts.Count == 0 && form["name"].FirstOrDefault() is { Length: > 0 } name)
                parts.Add(name);
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Engine ids are 64 hex digits; the short form is what people see.</summary>
    private static string Short(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigitLower) ? value[..12] : value;
}
