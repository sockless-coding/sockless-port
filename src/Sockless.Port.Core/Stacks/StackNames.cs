using System.Text.RegularExpressions;

namespace Sockless.Port.Stacks;

/// <summary>Compose project names, which are also stack names and folder names.</summary>
public static partial class StackNames
{
    public const int MaxLength = 63;

    /// <summary>Compose's rule: lower-case letters, digits, dashes and underscores, starting with a letter or digit.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,62}$")]
    public static partial Regex Pattern();

    /// <summary>Why <paramref name="name"/> can't be a stack name, or null if it can.</summary>
    public static string? Problem(string? name) => name switch
    {
        null or "" => "Give the stack a name.",
        { Length: > MaxLength } => $"Use at most {MaxLength} characters.",
        _ when !Pattern().IsMatch(name) =>
            "Use lower-case letters, digits, - and _, starting with a letter or digit. Compose uses it as the project name.",
        _ => null,
    };
}
