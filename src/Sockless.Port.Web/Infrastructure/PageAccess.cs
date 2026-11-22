using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Sockless.Port.Access;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>Whether the current page's actions are allowed, set by <see cref="EndpointPageModel"/> for its views.</summary>
public static class PageAccess
{
    private const string WriteKey = "page.can-write";

    internal static void SetWrite(HttpContext context, bool allowed) => context.Items[WriteKey] = allowed;

    /// <summary>
    /// Is <paramref name="what"/> allowed here? <c>write</c>: the page's own actions;
    /// <c>operate</c> and <c>swarm</c>: those roles on the route's endpoint; <c>admin</c>: administrators.
    /// </summary>
    public static bool Can(HttpContext context, string what)
    {
        var access = context.Access();
        if (access is null)
            return false;
        int? endpointId = int.TryParse(context.GetRouteValue(EndpointSelection.RouteKey)?.ToString(), out var id) ? id : null;
        return what switch
        {
            "admin" => access.IsAdmin,
            "write" => context.Items[WriteKey] as bool? ?? (endpointId is { } e && access.Can(e, EndpointPermission.Operate)),
            "operate" => endpointId is { } e && access.Can(e, EndpointPermission.Operate),
            "swarm" => endpointId is { } e && access.Can(e, EndpointPermission.ManageSwarm),
            _ => throw new ArgumentOutOfRangeException(nameof(what), what, "Use write, operate, swarm or admin."),
        };
    }
}

/// <summary>
/// <c>sp-can="write|operate|swarm|admin"</c> on any element renders it only when the user may do
/// that (see <see cref="PageAccess.Can"/>). It hides actions; the pages still check every post.
/// </summary>
[HtmlTargetElement(Attributes = AttributeName)]
public sealed class AccessTagHelper : TagHelper
{
    private const string AttributeName = "sp-can";

    /// <summary>Before the other tag helpers, so hidden elements cost nothing.</summary>
    public override int Order => -1000;

    [HtmlAttributeName(AttributeName)]
    public string Can { get; set; } = "write";

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (!PageAccess.Can(ViewContext.HttpContext, Can))
            output.SuppressOutput();
    }
}
