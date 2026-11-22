namespace Sockless.Port.Stacks;

/// <summary>A stack's two files: <c>compose.yaml</c> and <c>.env</c>. Line endings are kept as <c>\n</c>.</summary>
public sealed record StackContent(string Compose, string Env)
{
    public static StackContent Empty { get; } = new("", "");

    /// <summary>The same text with <c>\r\n</c> turned into <c>\n</c> and a final newline, as it's stored.</summary>
    public static StackContent Normalize(string? compose, string? env) => new(NormalizeText(compose), NormalizeText(env));

    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return text.EndsWith('\n') ? text : text + "\n";
    }
}
