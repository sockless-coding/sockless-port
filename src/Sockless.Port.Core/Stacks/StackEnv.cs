using System.Text;
using System.Text.RegularExpressions;

namespace Sockless.Port.Stacks;

/// <summary>
/// Reads a stack's <c>.env</c> as Compose does, for <c>docker stack deploy</c>: unlike Compose, it
/// interpolates <c>${…}</c> only from its own environment, so the variables are passed that way.
/// </summary>
public static partial class StackEnv
{
    /// <summary>Variables the run itself needs; a <c>.env</c> can't replace them.</summary>
    public static bool IsReserved(string name) =>
        name.StartsWith("DOCKER_", StringComparison.OrdinalIgnoreCase)
        || name.ToUpperInvariant() is "PATH" or "HOME" or "TMPDIR" or "TEMP" or "TMP" or "USERPROFILE" or "APPDATA" or "LOCALAPPDATA"
            or "SYSTEMROOT" or "WINDIR" or "PATHEXT" or "COMSPEC";

    /// <summary>
    /// <c>NAME=value</c> lines, with <c>#</c> comments, an optional <c>export</c>, <c>'literal'</c>
    /// values and <c>"quoted"</c> ones (with <c>\n</c>, <c>\t</c>, <c>\"</c> and <c>\\</c>). Later lines win.
    /// Lines that aren't assignments are skipped, as Compose skips them.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string? text)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line["export ".Length..].TrimStart();

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
                continue;
            var name = line[..equals].Trim();
            if (!Name().IsMatch(name))
                continue;
            variables[name] = Value(line[(equals + 1)..].TrimStart());
        }
        return variables;
    }

    private static string Value(string value)
    {
        if (value.StartsWith('\''))
        {
            var end = value.IndexOf('\'', 1);
            return end < 0 ? value[1..] : value[1..end];
        }
        if (value.StartsWith('"'))
        {
            var builder = new StringBuilder();
            for (var i = 1; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '"')
                    break;
                if (c == '\\' && i + 1 < value.Length)
                {
                    i++;
                    builder.Append(value[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', var other => other });
                    continue;
                }
                builder.Append(c);
            }
            return builder.ToString();
        }
        // Unquoted: a comment starts at " #".
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return (comment < 0 ? value : value[..comment]).Trim();
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]*$")]
    private static partial Regex Name();
}
