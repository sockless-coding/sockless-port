using System.Net;
using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Networks;

/// <summary>A network's overview, and the handlers for connecting, disconnecting and removing.</summary>
public sealed class DetailModel(EngineConnections connections) : NetworkPageModel(connections)
{
    /// <summary>The network's tabs: route suffix → (page, label).</summary>
    public static readonly IReadOnlyList<(string Tab, string Page, string Label)> Tabs =
    [
        ("", "/Engine/Networks/Detail", "Overview"),
        ("inspect", "/Engine/Networks/Inspect", "Inspect"),
    ];

    public void OnGet()
    {
    }

    /// <summary>htmx refresh of the overview on engine events.</summary>
    public IActionResult OnGetOverview() => Partial("_NetworkOverview", this);

    /// <param name="aliases">Extra names, separated by spaces or commas.</param>
    /// <param name="address">A fixed IPv4 or IPv6 address, or empty.</param>
    public async Task<IActionResult> OnPostConnectAsync(string? container, string? aliases, string? address)
    {
        var target = Others.FirstOrDefault(c => c.Id == container);
        if (Network is null || target is null)
        {
            Flash.Error(TempData, "Choose a container that isn't attached yet.");
            return Back();
        }

        IPAddress? ip = null;
        if (!string.IsNullOrWhiteSpace(address) && (!IPAddress.TryParse(address.Trim(), out ip) || address.Contains('/', StringComparison.Ordinal)))
        {
            Flash.Error(TempData, $"{address.Trim()} isn't an IP address. Enter one address without a prefix, e.g. 172.30.0.10.");
            return Back();
        }
        var names = (aliases ?? "").Split([' ', ',', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var options = new NetworkConnectOptions
        {
            Aliases = names.Length > 0 ? names : null,
            Ipv4Address = ip?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? ip.ToString() : null,
            Ipv6Address = ip?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? ip.ToString() : null,
        };
        await TryEngineAsync(client => client.ConnectNetworkAsync(Network.Id, target.Id, options, HttpContext.RequestAborted),
            $"Connected {target.DisplayName} to {Network.Name}.{(target.State is "running" ? "" : " It joins when it starts.")}");
        return Back();
    }

    public async Task<IActionResult> OnPostDisconnectAsync(string? container, bool force)
    {
        var target = Attached.FirstOrDefault(c => c.Id == container);
        if (Network is null || target is null)
        {
            Flash.Error(TempData, "That container isn't attached any more.");
            return Back();
        }
        await TryEngineAsync(client => client.DisconnectNetworkAsync(Network.Id, target.Id, force, HttpContext.RequestAborted),
            $"Disconnected {target.DisplayName} from {Network.Name}.");
        return Back();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string? tab)
    {
        var ok = await TryEngineAsync(client => client.RemoveNetworkAsync(NetworkId, HttpContext.RequestAborted), $"Removed the network {DisplayName}.");
        if (ok)
            return RedirectToPage("/Engine/Networks/Index", new { endpointId = EndpointId });
        var page = Tabs.FirstOrDefault(t => t.Tab == tab).Page ?? "/Engine/Networks/Detail";
        return RedirectToPage(page, new { endpointId = EndpointId, networkId = NetworkId });
    }

    private RedirectToPageResult Back() => RedirectToPage("/Engine/Networks/Detail", new { endpointId = EndpointId, networkId = NetworkId });
}
