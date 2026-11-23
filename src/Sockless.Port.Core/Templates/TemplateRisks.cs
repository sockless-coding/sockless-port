using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Sockless.Port.Templates;

/// <summary>A setting in a compose file that gives a service power over the host.</summary>
public sealed record TemplateRisk(string Service, string Setting, string Why);

/// <summary>
/// Finds what a template's compose file asks for beyond an ordinary container: privileged mode,
/// the engine's socket, the host's network, process or IPC namespaces, extra capabilities, devices,
/// and system folders of the host. Shown before saving, so nobody deploys them unawares.
/// </summary>
public static class TemplateRisks
{
    private static readonly string[] PowerfulCapabilities = ["ALL", "SYS_ADMIN", "SYS_MODULE", "SYS_PTRACE", "SYS_RAWIO", "NET_ADMIN", "DAC_READ_SEARCH", "SYS_BOOT", "SYS_TIME"];

    private static readonly string[] SystemFolders = ["/", "/etc", "/root", "/proc", "/sys", "/dev", "/boot", "/var/lib/docker", "/var/lib/containers", "/run", "/var/run", "/usr", "/lib", "/bin", "/sbin", "/home"];

    /// <returns>The risks, or null when the file isn't YAML Sockless Port can read (Compose's validation says more).</returns>
    public static IReadOnlyList<TemplateRisk>? Find(string compose)
    {
        YamlStream stream = [];
        try
        {
            stream.Load(new StringReader(compose));
        }
        catch (YamlException)
        {
            return null;
        }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return null;
        if (Child(root, "services") is not YamlMappingNode services)
            return [];

        List<TemplateRisk> risks = [];
        foreach (var (key, value) in services.Children)
        {
            if (key is not YamlScalarNode { Value: { } service } || value is not YamlMappingNode spec)
                continue;
            void Add(string setting, string why) => risks.Add(new TemplateRisk(service, setting, why));

            if (Scalar(spec, "privileged") is { } privileged && IsTrue(privileged))
                Add("privileged: true", "Turns off the container's isolation: it can do anything root on the host can.");
            foreach (var mode in new[] { "network_mode", "pid", "ipc", "userns_mode", "uts" })
            {
                if (Scalar(spec, mode) is { } value2 && value2.Equals("host", StringComparison.OrdinalIgnoreCase))
                    Add($"{mode}: host", mode switch
                    {
                        "network_mode" => "Uses the host's network directly: it can listen on any of the host's ports and reach services bound to localhost.",
                        "pid" => "Sees and can signal every process on the host.",
                        "ipc" => "Shares the host's shared memory.",
                        "userns_mode" => "Runs without user namespace remapping.",
                        _ => "Shares the host's hostname.",
                    });
            }
            foreach (var cap in Items(spec, "cap_add").Where(c => PowerfulCapabilities.Contains(c.ToUpperInvariant().Replace("CAP_", "", StringComparison.Ordinal))))
                Add($"cap_add: {cap}", "A capability that reaches past the container, e.g. into the host's network setup or kernel.");
            foreach (var option in Items(spec, "security_opt").Where(o => o.Contains("unconfined", StringComparison.OrdinalIgnoreCase)))
                Add($"security_opt: {option}", "Turns off a security profile that limits what the container can do.");
            foreach (var device in Items(spec, "devices"))
                Add($"devices: {device}", "Gives the container a device of the host.");

            if (Child(spec, "volumes") is YamlSequenceNode volumes)
            {
                foreach (var volume in volumes.Children)
                {
                    var source = volume switch
                    {
                        YamlScalarNode { Value: { } shortSyntax } => shortSyntax.Split(':')[0],
                        YamlMappingNode longSyntax => Scalar(longSyntax, "source"),
                        _ => null,
                    };
                    if (source is null || !source.StartsWith('/'))
                        continue;
                    var path = source.TrimEnd('/');
                    if (path.EndsWith(".sock", StringComparison.Ordinal) && (path.Contains("docker", StringComparison.OrdinalIgnoreCase) || path.Contains("podman", StringComparison.OrdinalIgnoreCase)))
                        Add($"volumes: {source}", "The engine's socket: whoever controls the container controls every container on the host, and the host itself.");
                    else if (SystemFolders.Contains(path.Length == 0 ? "/" : path, StringComparer.Ordinal)
                             || SystemFolders.Any(f => f != "/" && path.StartsWith(f + "/", StringComparison.Ordinal) && f is "/proc" or "/sys" or "/dev" or "/etc" or "/root" or "/boot"))
                        Add($"volumes: {source}", "A system folder of the host.");
                }
            }
        }
        return risks;
    }

    private static YamlNode? Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string? Scalar(YamlMappingNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    private static IEnumerable<string> Items(YamlMappingNode node, string key) => Child(node, key) switch
    {
        YamlSequenceNode sequence => sequence.Children.OfType<YamlScalarNode>().Select(s => s.Value).OfType<string>(),
        YamlScalarNode { Value: { } single } => [single],
        _ => [],
    };

    private static bool IsTrue(string value) => value.ToLowerInvariant() is "true" or "yes" or "on" or "1";
}
