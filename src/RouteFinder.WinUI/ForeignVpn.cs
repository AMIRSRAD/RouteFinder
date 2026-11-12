using System.Net.NetworkInformation;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

/// <summary>
/// Another VPN (Hotspot Shield's Hydra, OpenVPN, WireGuard, …) can take over Windows' default route with split routes.
/// Left alone, RouteFinder's own tunnel and its VPN apps would then be carried inside that VPN. When that happens,
/// RouteFinder keeps them on a physical adapter instead.
/// </summary>
public sealed partial class MainWindow
{
    // Name of another VPN's adapter that currently holds the default route, or null.
    string? foreignVpnName;
    // The adapter Windows itself uses for internet traffic right now.
    string? windowsDefaultAdapterId;
    // The physical adapter the tunnel uses when another VPN holds the default route.
    string? autoPhysicalUplink;

    static readonly string[] VpnAdapterWords = ["wintun", "tap-windows", "wireguard", "openvpn", "vpn", "tunnel", "hydra", "nordlynx", "warp"];

    /// <summary>True for virtual adapters that belong to a VPN client, but never for WireSock (RouteFinder's own engine).</summary>
    internal static bool LooksLikeVpn(NetworkInterfaceType type, string description, string name)
    {
        string text = (description + " " + name).ToLowerInvariant();
        if (text.Contains("wiresock")) return false;
        // PPP alone is not enough: PPPoE broadband is PPP too.
        return type is NetworkInterfaceType.Tunnel || (int)type == 53 /* virtual */ ||
            VpnAdapterWords.Any(text.Contains);
    }

    // Physical adapters first by kind (Ethernet, then Wi-Fi), as Windows usually prefers them.
    static int PhysicalRank(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => 0,
        NetworkInterfaceType.Wireless80211 => 1,
        _ => 2
    };

    void DetectForeignVpn(NetworkInterface[] adapters, string? defaultName)
    {
        var holder = adapters.FirstOrDefault(adapter => adapter.Name == defaultName);
        windowsDefaultAdapterId = holder?.Id;
        foreignVpnName = holder is not null && LooksLikeVpn(holder.NetworkInterfaceType, holder.Description, holder.Name) ? holder.Name : null;
        autoPhysicalUplink = adapters
            .Where(adapter => DirectAppRouting.HasInternetGateway(adapter) && !LooksLikeVpn(adapter.NetworkInterfaceType, adapter.Description, adapter.Name) &&
                adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .OrderBy(adapter => PhysicalRank(adapter.NetworkInterfaceType))
            .Select(adapter => adapter.Id).FirstOrDefault();
    }

    /// <summary>
    /// The adapter the tunnel really leaves through: the chosen one while it has a gateway, otherwise a working
    /// physical adapter, so a disconnected Wi-Fi or another VPN never carries the tunnel.
    /// </summary>
    string? EffectiveUplink(RouteChoice choice)
    {
        string? chosen = UplinkFor(choice);
        if (chosen is null) return foreignVpnName is not null ? autoPhysicalUplink : null;
        if (windowsAdapters.Length == 0 || windowsAdapters.Any(adapter => adapter.HasGateway &&
            adapter.Id.Equals(chosen, StringComparison.OrdinalIgnoreCase))) return chosen;
        return autoPhysicalUplink ?? chosen;
    }

    // WireSock (transparent mode) only captures traffic on the adapter it reaches the VPN server through. Apps must be
    // kept on that adapter whenever Windows would send them elsewhere: a default adapter is set, another VPN holds the
    // route, or the route's adapter (say Wi-Fi) is not the one Windows prefers (say Ethernet).
    bool TunnelNeedsPinning(string? fallback) => !preferences.PortableEngine && (fallback is not null || foreignVpnName is not null ||
        PinnedTunnelAdapter(fallback) is { } tunnel && windowsDefaultAdapterId is not null &&
            !tunnel.Equals(windowsDefaultAdapterId, StringComparison.OrdinalIgnoreCase));
}
