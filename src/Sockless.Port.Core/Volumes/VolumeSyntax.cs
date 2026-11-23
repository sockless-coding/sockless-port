using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Sockless.Port.Volumes;

/// <summary>A syntax error in an edited file; <see cref="Line"/> is 1-based.</summary>
public sealed record SyntaxProblem(int? Line, string Message);

/// <summary>
/// Checks the syntax of edited files the editor knows by their extension: JSON (strict, as
/// most programs read it) and YAML (every document in the file). Only syntax: whether the
/// program reading the file accepts its content is up to that program.
/// </summary>
public static partial class VolumeSyntax
{
    /// <summary>"JSON" or "YAML" for a file checked on save, else null.</summary>
    public static string? Language(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "JSON",
        ".yml" or ".yaml" => "YAML",
        _ => null,
    };

    /// <summary>The problems in <paramref name="text"/> (empty when it's valid), or null when the file's kind isn't checked.</summary>
    public static IReadOnlyList<SyntaxProblem>? Check(string path, string text) => Language(path) switch
    {
        "JSON" => CheckJson(text),
        "YAML" => CheckYaml(text),
        _ => null,
    };

    private static IReadOnlyList<SyntaxProblem> CheckJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return [];
        }
        catch (JsonException ex)
        {
            // "'}' is an invalid start of a value. LineNumber: 2 | BytePositionInLine: 4." The position goes in the gutter.
            return [new SyntaxProblem(ex.LineNumber is { } line ? (int)line + 1 : null, JsonPosition().Replace(ex.Message, "").Trim())];
        }
    }

    private static IReadOnlyList<SyntaxProblem> CheckYaml(string text)
    {
        try
        {
            // Loading (not only parsing) also refuses duplicate keys. Aliases aren't expanded, so they can't blow up.
            new YamlStream().Load(new StringReader(text));
            return [];
        }
        catch (YamlException ex)
        {
            // "(Line: 3, Col: 5, Idx: 20) - (Line: 3, Col: 6, Idx: 21): While scanning…" The position goes in the gutter.
            var line = ex.Start.Line is var l and > 0 ? (int?)l : null;
            return [new SyntaxProblem(line, YamlPosition().Replace(ex.Message, "").Trim())];
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            // The scanner trips over some malformed input (an unclosed flow sequence) instead of reporting it.
            return [new SyntaxProblem(null, "The YAML is malformed (check for an unclosed [ or {).")];
        }
    }

    [GeneratedRegex(@"\s*(Path: \S*\s*\|\s*)?LineNumber: \d+ \| BytePositionInLine: \d+\.?$")]
    private static partial Regex JsonPosition();

    [GeneratedRegex(@"^\(Line: \d+, Col: \d+, Idx: \d+\) - \(Line: \d+, Col: \d+, Idx: \d+\):\s*")]
    private static partial Regex YamlPosition();
}
