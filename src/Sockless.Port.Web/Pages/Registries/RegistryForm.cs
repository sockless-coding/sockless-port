using Sockless.Port.Registries;

namespace Sockless.Port.Web.Pages.Registries;

/// <summary>The registry form's fields (add and edit).</summary>
public sealed class RegistryForm
{
    public string Name { get; set; } = "";

    public string Registry { get; set; } = "";

    public string Username { get; set; } = "";

    /// <summary>Write-only: never sent back to the browser. Empty on edit keeps the stored one.</summary>
    public string? Password { get; set; }

    public RegistryInput ToInput() => new(Name ?? "", Registry ?? "", Username ?? "", Password);

    /// <summary>Common registries, offered as suggestions in the form.</summary>
    public static readonly IReadOnlyList<(string Host, string Label)> Known =
    [
        ("docker.io", "Docker Hub"),
        ("ghcr.io", "GitHub Container Registry"),
        ("quay.io", "Quay"),
        ("registry.gitlab.com", "GitLab"),
    ];
}
