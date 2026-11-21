using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Adds <c>aria-invalid</c> and <c>aria-describedby</c> to inputs bound with <c>asp-for</c> when
/// the model has errors for them, pointing at the error element rendered by
/// <c>asp-validation-for</c> (whose id is <c>{field id}-error</c>, see <see cref="ErrorId"/>).
/// </summary>
[HtmlTargetElement("input", Attributes = "asp-for")]
[HtmlTargetElement("textarea", Attributes = "asp-for")]
[HtmlTargetElement("select", Attributes = "asp-for")]
public sealed class FieldAccessibilityTagHelper : TagHelper
{
    // Run after the built-in input tag helpers have set the id.
    public override int Order => 1000;

    [HtmlAttributeName("asp-for")]
    public required ModelExpression For { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public required ViewContext ViewContext { get; set; }

    public static string ErrorId(string fieldId) => fieldId + "-error";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var name = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        if (!ViewContext.ViewData.ModelState.TryGetValue(name, out var entry) || entry.Errors.Count == 0)
            return;

        var id = output.Attributes["id"]?.Value?.ToString() ?? TagBuilder.CreateSanitizedId(name, "_");
        output.Attributes.SetAttribute("aria-invalid", "true");
        var existing = output.Attributes["aria-describedby"]?.Value?.ToString();
        output.Attributes.SetAttribute("aria-describedby", string.IsNullOrEmpty(existing) ? ErrorId(id) : $"{existing} {ErrorId(id)}");
    }
}
