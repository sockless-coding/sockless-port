using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Sockless.Port.Containers;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Volumes;

namespace Sockless.Port.Networks;

/// <summary>What the create-network form collects. Options and labels are <c>key=value</c>, one per line.</summary>
public sealed class NetworkCreateSpec
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "bridge";

    /// <summary>CIDR; empty to let the engine pick one from its address pools.</summary>
    public string? Subnet { get; set; }

    public string? Gateway { get; set; }

    /// <summary>A CIDR inside <see cref="Subnet"/> that containers get addresses from.</summary>
    public string? IpRange { get; set; }

    public bool EnableIpv6 { get; set; }

    /// <summary>An IPv6 CIDR; empty to let the engine pick one (where it can).</summary>
    public string? Ipv6Subnet { get; set; }

    /// <summary>The host interface a macvlan or ipvlan network attaches to, e.g. <c>eth0</c>.</summary>
    public string? Parent { get; set; }

    /// <summary>No route out: containers reach each other only.</summary>
    public bool Internal { get; set; }

    /// <summary>Overlay only: standalone containers may join, not only swarm services.</summary>
    public bool Attachable { get; set; }

    /// <summary>Overlay only: encrypt traffic between nodes (IPsec). Costs throughput; Windows nodes can't join.</summary>
    public bool Encrypted { get; set; }

    /// <summary>Overlay only: the swarm's routing mesh. There's one per swarm; remove the old one first.</summary>
    public bool Ingress { get; set; }

    public string? Options { get; set; }
    public string? Labels { get; set; }
}

public static partial class NetworkCreateSpecBuilder
{
    /// <summary>Drivers the form offers, in this order, when the engine has them.</summary>
    private static readonly string[] Creatable = ["bridge", "macvlan", "ipvlan", "overlay"];

    /// <summary>
    /// The drivers a network can be created with: those the engine lists (<c>/info</c> plugins),
    /// overlay only on a swarm manager.
    /// </summary>
    public static IReadOnlyList<string> Drivers(IReadOnlyList<string>? engineDrivers, EngineCapabilities capabilities)
    {
        var available = engineDrivers is { Count: > 0 } ? engineDrivers : ["bridge", "macvlan", "ipvlan"];
        return [.. Creatable.Where(d => available.Contains(d, StringComparer.Ordinal)
                                        && (d != "overlay" || capabilities.HasFlag(EngineCapabilities.SwarmManager)))];
    }

    public static bool UsesParent(string driver) => driver is "macvlan" or "ipvlan";

    public static (NetworkCreateRequest? Request, IReadOnlyList<SpecError> Errors) Build(NetworkCreateSpec spec, IReadOnlyList<string> drivers)
    {
        var errors = new List<SpecError>();
        var name = spec.Name?.Trim() ?? "";
        if (name.Length == 0)
            errors.Add(new(nameof(spec.Name), "Enter a name, e.g. backend."));
        else if (!NetworkName().IsMatch(name))
            errors.Add(new(nameof(spec.Name), "Use letters, digits, '_', '.' or '-', starting with a letter or digit."));

        var driver = spec.Driver?.Trim() ?? "";
        if (!drivers.Contains(driver, StringComparer.Ordinal))
            errors.Add(new(nameof(spec.Driver), drivers.Count == 0 ? "This engine offers no driver to create networks with." : $"Choose {string.Join(", ", drivers)}."));

        var subnet = ParseNetwork(errors, nameof(spec.Subnet), spec.Subnet);
        var gateway = ParseAddress(errors, nameof(spec.Gateway), spec.Gateway);
        var range = ParseNetwork(errors, nameof(spec.IpRange), spec.IpRange);
        var ipv6 = ParseNetwork(errors, nameof(spec.Ipv6Subnet), spec.Ipv6Subnet);

        if (subnet is null && (gateway is not null || range is not null) && !errors.Any(e => e.Field == nameof(spec.Subnet)))
            errors.Add(new(nameof(spec.Subnet), "Set the subnet too: a gateway or range needs one."));
        if (subnet is { } s)
        {
            if (gateway is { } g && !s.Contains(g))
                errors.Add(new(nameof(spec.Gateway), $"{g} isn't in {s}."));
            if (range is { } r && (r.BaseAddress.AddressFamily != s.BaseAddress.AddressFamily || r.PrefixLength < s.PrefixLength || !s.Contains(r.BaseAddress)))
                errors.Add(new(nameof(spec.IpRange), $"The range must be inside {s}, e.g. a smaller block of it."));
        }
        if (ipv6 is { } v6 && v6.BaseAddress.AddressFamily != AddressFamily.InterNetworkV6)
            errors.Add(new(nameof(spec.Ipv6Subnet), "Enter an IPv6 subnet such as fd00:1::/64."));
        if (ipv6 is not null && !spec.EnableIpv6)
            errors.Add(new(nameof(spec.Ipv6Subnet), "Turn on IPv6 to use an IPv6 subnet."));
        if (subnet is { } sub && sub.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6 && ipv6 is not null)
            errors.Add(new(nameof(spec.Subnet), "Put an IPv4 subnet here and the IPv6 one below."));

        var parent = spec.Parent?.Trim();
        if (!string.IsNullOrEmpty(parent) && !UsesParent(driver))
            errors.Add(new(nameof(spec.Parent), "Only macvlan and ipvlan networks attach to a host interface."));
        else if (!string.IsNullOrEmpty(parent) && !InterfaceName().IsMatch(parent))
            errors.Add(new(nameof(spec.Parent), "Enter an interface name such as eth0 or eth0.10."));

        if (spec.Attachable && driver != "overlay")
            errors.Add(new(nameof(spec.Attachable), "Only overlay networks can be made attachable; containers can always join other networks."));
        if (spec.Encrypted && driver != "overlay")
            errors.Add(new(nameof(spec.Encrypted), "Only overlay networks are encrypted: traffic of other networks doesn't leave the host."));
        if (spec.Ingress && driver != "overlay")
            errors.Add(new(nameof(spec.Ingress), "Only an overlay network can be the routing mesh."));
        else if (spec.Ingress && spec.Attachable)
            errors.Add(new(nameof(spec.Ingress), "The routing mesh network can't be attachable: only services use it."));
        else if (spec.Ingress && spec.Internal)
            errors.Add(new(nameof(spec.Ingress), "The routing mesh network can't be internal: it carries published ports in."));

        var options = VolumeCreateSpecBuilder.Parse(errors, nameof(spec.Options), spec.Options);
        var labels = VolumeCreateSpecBuilder.Parse(errors, nameof(spec.Labels), spec.Labels);
        if (errors.Count > 0)
            return (null, errors);

        if (!string.IsNullOrEmpty(parent))
            options!["parent"] = parent;
        if (spec.Encrypted)
            options![Docker.Models.NetworkOptions.Encrypted] = "";

        var configs = new List<NetworkIpamConfig>();
        if (subnet is not null)
            configs.Add(new NetworkIpamConfig { Subnet = subnet.ToString(), Gateway = gateway?.ToString(), IpRange = range?.ToString() });
        if (ipv6 is not null)
            configs.Add(new NetworkIpamConfig { Subnet = ipv6.ToString() });

        return (new NetworkCreateRequest
        {
            Name = name,
            Driver = driver,
            Internal = spec.Internal,
            Attachable = spec.Attachable,
            Ingress = spec.Ingress,
            EnableIpv6 = spec.EnableIpv6,
            Ipam = configs.Count > 0 ? new NetworkIpam { Driver = "default", Config = configs } : null,
            Options = options is { Count: > 0 } ? options : null,
            Labels = labels is { Count: > 0 } ? labels : null,
        }, errors);
    }

    private static IPNetwork? ParseNetwork(List<SpecError> errors, string field, string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (!text.Contains('/', StringComparison.Ordinal))
        {
            errors.Add(new(field, $"Add the prefix length, e.g. {text}/24."));
            return null;
        }
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (!IPAddress.TryParse(text[..slash], out var address) || !int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)
            || prefix > (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
        {
            errors.Add(new(field, "Enter a subnet in CIDR form, e.g. 172.20.0.0/16."));
            return null;
        }
        // Engines refuse host bits after the prefix (10.0.0.1/24); say which address was meant.
        var first = MaskTo(address, prefix);
        if (!first.Equals(address))
        {
            errors.Add(new(field, $"Use the network's first address: {first}/{prefix}."));
            return null;
        }
        return new IPNetwork(first, prefix);
    }

    private static IPAddress? ParseAddress(List<SpecError> errors, string field, string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (IPAddress.TryParse(text, out var address) && !text.Contains('/', StringComparison.Ordinal))
            return address;
        errors.Add(new(field, "Enter one address, e.g. 172.20.0.1."));
        return null;
    }

    private static IPAddress MaskTo(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(bytes);
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex NetworkName();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.@:-]{0,14}$")]
    private static partial Regex InterfaceName();
}
