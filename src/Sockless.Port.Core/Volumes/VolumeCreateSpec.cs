using System.Text.RegularExpressions;
using Sockless.Port.Containers;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Volumes;

/// <summary>What the create-volume form collects. Options and labels are <c>key=value</c>, one per line.</summary>
public sealed class VolumeCreateSpec
{
    /// <summary>Empty for a generated name.</summary>
    public string? Name { get; set; }

    public string Driver { get; set; } = "local";

    /// <summary>Driver options, e.g. <c>type=nfs</c>, <c>o=addr=10.0.0.5,rw</c>, <c>device=:/export</c> for the local driver.</summary>
    public string? Options { get; set; }

    public string? Labels { get; set; }
}

public static partial class VolumeCreateSpecBuilder
{
    public static (VolumeCreateRequest? Request, IReadOnlyList<SpecError> Errors) Build(VolumeCreateSpec spec)
    {
        var errors = new List<SpecError>();
        var name = spec.Name?.Trim();
        if (!string.IsNullOrEmpty(name) && !VolumeName().IsMatch(name))
            errors.Add(new(nameof(spec.Name), "Use at least two letters, digits, '_', '.' or '-', starting with a letter or digit."));

        var driver = spec.Driver?.Trim();
        if (string.IsNullOrEmpty(driver))
            driver = "local";
        else if (driver.Any(char.IsWhiteSpace))
            errors.Add(new(nameof(spec.Driver), "A driver name has no spaces."));

        var options = Parse(errors, nameof(spec.Options), spec.Options);
        var labels = Parse(errors, nameof(spec.Labels), spec.Labels);
        if (errors.Count > 0)
            return (null, errors);

        return (new VolumeCreateRequest
        {
            Name = string.IsNullOrEmpty(name) ? null : name,
            Driver = driver,
            DriverOpts = options is { Count: > 0 } ? options : null,
            Labels = labels is { Count: > 0 } ? labels : null,
        }, errors);
    }

    internal static Dictionary<string, string>? Parse(List<SpecError> errors, string field, string? text)
    {
        try
        {
            return ContainerSpecBuilder.ParseLabels(text);
        }
        catch (ContainerSpecBuilder.SpecFormatException ex)
        {
            errors.Add(new(field, ex.Message));
            return null;
        }
    }

    /// <summary>The engine's rule for volume names (at least two characters).</summary>
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]+$")]
    public static partial Regex VolumeName();
}
