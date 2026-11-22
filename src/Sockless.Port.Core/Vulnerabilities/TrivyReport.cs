using System.Text.Json;

namespace Sockless.Port.Vulnerabilities;

/// <summary>What Trivy's JSON report (<c>--format json</c>, schema 2) says about an image.</summary>
/// <param name="ImageId">The ID Trivy computed from the tarball, when it reported one.</param>
public sealed record TrivyReport(string? ImageId, string? Os, bool OsEndOfLife, IReadOnlyList<VulnerabilityFinding> Findings)
{
    /// <summary>Findings kept from one report. An image with more has bigger problems than this list can show.</summary>
    public const int MaxFindings = 10_000;

    private const int MaxTitle = 300;

    /// <summary>
    /// Reads the report. Lenient on purpose: fields Trivy adds or leaves out are ignored, and a
    /// result without vulnerabilities is simply empty.
    /// </summary>
    /// <exception cref="InvalidDataException">It isn't a Trivy report.</exception>
    public static TrivyReport Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("SchemaVersion", out _))
            throw new InvalidDataException("The scanner's output isn't a Trivy JSON report.");

        string? imageId = null, os = null;
        var endOfLife = false;
        if (root.TryGetProperty("Metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            imageId = String(metadata, "ImageID");
            if (metadata.TryGetProperty("OS", out var osInfo) && osInfo.ValueKind == JsonValueKind.Object)
            {
                os = string.Join(' ', new[] { String(osInfo, "Family"), String(osInfo, "Name") }.Where(p => !string.IsNullOrEmpty(p)));
                if (os.Length == 0)
                    os = null;
                endOfLife = osInfo.TryGetProperty("EOSL", out var eosl) && eosl.ValueKind == JsonValueKind.True;
            }
        }

        var findings = new List<VulnerabilityFinding>();
        if (root.TryGetProperty("Results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("Vulnerabilities", out var vulnerabilities) || vulnerabilities.ValueKind != JsonValueKind.Array)
                    continue;
                // OS packages are reported against the tarball's file name: say so instead.
                var target = String(result, "Class") == "os-pkgs" ? null : String(result, "Target");
                var type = String(result, "Type");
                foreach (var v in vulnerabilities.EnumerateArray())
                {
                    if (findings.Count >= MaxFindings)
                        break;
                    if (String(v, "VulnerabilityID") is not { Length: > 0 } id)
                        continue;
                    findings.Add(new VulnerabilityFinding(
                        id,
                        String(v, "PkgName") ?? String(v, "PkgID") ?? "?",
                        String(v, "InstalledVersion"),
                        String(v, "FixedVersion") is { Length: > 0 } fixedIn ? fixedIn : null,
                        Severities.Parse(String(v, "Severity")),
                        String(v, "Status"),
                        Shorten(String(v, "Title")),
                        target,
                        type,
                        SafeUrl(String(v, "PrimaryURL"))));
                }
            }
        }
        return new TrivyReport(imageId, os, endOfLife, Sort(findings));
    }

    /// <summary>Worst first; among equals, the ones with a fix first, then by ID and package.</summary>
    public static IReadOnlyList<VulnerabilityFinding> Sort(IEnumerable<VulnerabilityFinding> findings) =>
    [
        .. findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Fixable)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ThenBy(f => f.Package, StringComparer.Ordinal),
    ];

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        return text.Length <= MaxTitle ? text : text[..(MaxTitle - 1)].TrimEnd() + "…";
    }

    /// <summary>Only http(s) links are shown: the database is data from outside.</summary>
    internal static string? SafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri.AbsoluteUri : null;
}
