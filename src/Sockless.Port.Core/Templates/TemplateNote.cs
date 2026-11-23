using System.Net;
using System.Text.RegularExpressions;

namespace Sockless.Port.Templates;

/// <summary>A piece of a template's note, safe to render: text is encoded by the view.</summary>
public closed record NotePart;

/// <param name="Strong">Inside <c>&lt;b&gt;</c> or <c>&lt;strong&gt;</c>.</param>
public sealed record NoteText(string Text, bool Strong) : NotePart;

public sealed record NoteCode(string Text) : NotePart;

/// <param name="Url">An http(s) URL; links to anything else stay text.</param>
public sealed record NoteLink(string Text, string Url) : NotePart;

public sealed record NoteBreak : NotePart;

/// <summary>
/// Template notes are HTML written for Portainer, which renders them as they are. Here they're
/// read into text, bold, code, line breaks and http(s) links; every other tag is dropped and its
/// text kept, so a feed can't put markup or script on the page.
/// </summary>
public static partial class TemplateNote
{
    public static IReadOnlyList<NotePart> Parse(string? html)
    {
        List<NotePart> parts = [];
        if (string.IsNullOrWhiteSpace(html))
            return parts;

        var strong = 0;
        var code = 0;
        // Inside script or style: their content isn't text.
        var skip = 0;
        string? link = null;
        var position = 0;
        foreach (Match tag in Tag().Matches(html))
        {
            AddText(html[position..tag.Index]);
            position = tag.Index + tag.Length;
            var closing = tag.Groups["close"].Success;
            switch (tag.Groups["name"].Value.ToLowerInvariant())
            {
                case "b" or "strong":
                    strong = Math.Max(0, strong + (closing ? -1 : 1));
                    break;
                case "code" or "pre" or "kbd":
                    code = Math.Max(0, code + (closing ? -1 : 1));
                    break;
                case "a":
                    link = closing ? null : Href(tag.Groups["attributes"].Value);
                    break;
                case "script" or "style" or "iframe" or "object" or "svg":
                    skip = Math.Max(0, skip + (closing ? -1 : 1));
                    break;
                case "br" or "p" or "div" or "li" or "ul" or "ol" or "h1" or "h2" or "h3" or "h4" when closing || tag.Groups["name"].Value.Equals("br", StringComparison.OrdinalIgnoreCase):
                    if (parts.Count > 0 && parts[^1] is not NoteBreak)
                        parts.Add(new NoteBreak());
                    break;
            }
        }
        AddText(html[position..]);
        while (parts.Count > 0 && parts[^1] is NoteBreak)
            parts.RemoveAt(parts.Count - 1);
        return parts;

        void AddText(string raw)
        {
            if (raw.Length == 0 || skip > 0)
                return;
            var text = Whitespace().Replace(WebUtility.HtmlDecode(raw), " ");
            if (text.Trim().Length == 0 && (parts.Count == 0 || parts[^1] is NoteBreak))
                return;
            if (parts.Count == 0 || parts[^1] is NoteBreak)
                text = text.TrimStart();
            parts.Add(link is not null ? new NoteLink(text, link) : code > 0 ? new NoteCode(text) : new NoteText(text, strong > 0));
        }
    }

    /// <summary>The note as plain text, e.g. for a card.</summary>
    public static string PlainText(string? html) => string.Concat(Parse(html).Select(p => p switch
    {
        NoteText t => t.Text,
        NoteCode c => c.Text,
        NoteLink l => l.Text,
        NoteBreak => " ",
    })).Trim();

    private static string? Href(string attributes)
    {
        var match = HrefAttribute().Match(attributes);
        if (!match.Success)
            return null;
        var value = WebUtility.HtmlDecode(match.Groups["value"].Value.Trim());
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri.AbsoluteUri : null;
    }

    [GeneratedRegex(@"<(?<close>/)?(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attributes>[^>]*)>|<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Tag();

    [GeneratedRegex(@"href\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex HrefAttribute();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
