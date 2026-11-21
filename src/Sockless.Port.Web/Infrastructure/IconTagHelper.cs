using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Renders a Heroicons outline icon inline: <c>&lt;icon name="cube" class="w-5 h-5" /&gt;</c>.
/// Icons are decorative (<c>aria-hidden</c>) and take the colour of the surrounding text.
/// Icon-only buttons must carry their own <c>aria-label</c>.
/// </summary>
[HtmlTargetElement("icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed partial class IconTagHelper : TagHelper
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    public required string Name { get; set; }

    /// <summary>2 for UI icons, 1.5 for 48 px empty-state icons (guideline: iconography).</summary>
    public string StrokeWidth { get; set; } = "2";

    public string? Class { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var svg = Cache.GetOrAdd(Name, Load);
        svg = StrokeWidthAttribute().Replace(svg, $"stroke-width=\"{StrokeWidth}\"", 1);
        if (!string.IsNullOrEmpty(Class))
            svg = svg.Replace("<svg ", $"<svg class=\"{System.Net.WebUtility.HtmlEncode(Class)}\" ", StringComparison.Ordinal);

        output.TagName = null;
        output.Content.SetHtmlContent(svg);
    }

    private static string Load(string name)
    {
        using var stream = typeof(IconTagHelper).Assembly.GetManifestResourceStream($"Icons.{name}.svg")
            ?? throw new InvalidOperationException($"Unknown icon '{name}'. Add Icons/{name}.svg from heroicons/24/outline.");
        using var reader = new StreamReader(stream);
        return DataSlot().Replace(reader.ReadToEnd().Trim(), string.Empty);
    }

    [GeneratedRegex("stroke-width=\"[^\"]*\"")]
    private static partial Regex StrokeWidthAttribute();

    [GeneratedRegex(" data-slot=\"icon\"")]
    private static partial Regex DataSlot();
}
