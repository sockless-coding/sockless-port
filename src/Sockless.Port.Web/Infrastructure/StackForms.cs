using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>What the stack forms post.</summary>
public sealed class StackInput
{
    public string? Name { get; set; }

    public string? Compose { get; set; }

    public string? Env { get; set; }

    public StackContent Content => StackContent.Normalize(Compose, Env);
}

/// <summary>A validation result for <c>_StackValidation</c>.</summary>
/// <param name="Unavailable">Why it couldn't be checked: Compose is missing.</param>
public sealed record StackValidationView(ComposeCheck? Check, string? Unavailable)
{
    /// <summary>Errors with a line, for the editor's gutter (<c>[{ line, message }]</c>).</summary>
    public string Diagnostics => JsonSerializer.Serialize(
        (Check?.Errors ?? []).Where(e => e.Line is not null).Select(e => new Diagnostic(e.Line!.Value, e.Message)),
        DiagnosticJson.Default.IEnumerableDiagnostic);
}

public sealed record Diagnostic(int Line, string Message);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(IEnumerable<Diagnostic>))]
internal sealed partial class DiagnosticJson : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>A review of changes before a deploy.</summary>
/// <param name="FirstDeploy">Nothing was deployed from here yet, so everything is new.</param>
public sealed record StackReview(IReadOnlyList<DiffHunk> Compose, IReadOnlyList<DiffHunk> Env, bool FirstDeploy)
{
    public bool Unchanged => Compose.Count == 0 && Env.Count == 0;

    public static StackReview Between(StackContent? deployed, StackContent next) => new(
        TextDiff.Hunks(TextDiff.Lines(deployed?.Compose, next.Compose)),
        TextDiff.Hunks(TextDiff.Lines(deployed?.Env, next.Env)),
        deployed is null);

    /// <summary>Identifies reviewed content, so a deploy can tell whether it changed after the review.</summary>
    public static string Hash(StackContent content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{content.Compose}\0{content.Env}")));
}

public static class StackForms
{
    /// <summary>Validates the files the way they'd be deployed on <paramref name="connection"/>.</summary>
    public static async Task<StackValidationView> ValidateAsync(StackValidator validator, StackStore store, EngineConnection connection, string name, StackContent content,
        StackKind kind, CancellationToken cancellationToken)
    {
        try
        {
            var rootless = connection.Engine?.Has(EngineCapabilities.Rootless) == true;
            var check = await validator.CheckAsync(name, content, store.Files.Directory(connection.Id, name), kind, rootless, cancellationToken);
            return new StackValidationView(check, null);
        }
        catch (ComposeUnavailableException ex)
        {
            return new StackValidationView(null, ex.Message);
        }
    }

    /// <summary>A placeholder project name for validating before a name is chosen.</summary>
    public static string NameForCheck(string? name) => StackNames.Problem(name) is null ? name! : "stack";
}
