using System.Net.NetworkInformation;
using RouteFinder.App;

namespace RouteFinder.WinUI;

/// <summary>
/// Changes made while the VPN is connected (apps, route, adapter, scope, connection settings) are applied by
/// reconnecting automatically, instead of locking the controls. Network adapter changes re-apply adapter rules.
/// </summary>
public sealed partial class MainWindow
{
    string? connectedSignature;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? reconnectTimer, adapterChangeTimer;

    // Everything the running tunnel was started with. If any of it changes, the tunnel is out of date.
    string VpnSignature() => string.Join("|",
        route?.Key ?? "", route is { } current ? UplinkFor(current) ?? "" : "", Scope.SelectedIndex, selectedGame?.Name ?? "",
        string.Join(",", VpnTargets().Order(StringComparer.OrdinalIgnoreCase)),
        preferences.KillSwitch, preferences.PortableEngine, preferences.AutoMtu, preferences.BypassLan, preferences.DnsOverride,
        preferences.BypassDestinations, string.Join(",", preferences.ExcludedApplications ?? []), preferences.TunnelMtu,
        preferences.KeepaliveSeconds, ConfiguredProxy?.Endpoint ?? "", ConfiguredProxy?.AllTraffic ?? false);

    void RememberConnectedSetup() => connectedSignature = VpnSignature();

    // Called from Refresh: when the connected setup no longer matches, reconnect shortly after the last change.
    void CheckLiveChanges()
    {
        if (!session.HasTunnel) { connectedSignature = null; reconnectTimer?.Stop(); return; }
        if (preview || verifying || connectedForConsole || appRouterVpn || connectedSignature is null || busy) return;
        if (VpnSignature() == connectedSignature) { reconnectTimer?.Stop(); return; }
        if (reconnectTimer is null)
        {
            reconnectTimer = DispatcherQueue.CreateTimer();
            reconnectTimer.Interval = TimeSpan.FromMilliseconds(1500);
            reconnectTimer.IsRepeating = false;
            reconnectTimer.Tick += async (_, _) =>
            {
                if (busy || closing || !session.HasTunnel || connectedSignature is null || VpnSignature() == connectedSignature) return;
                AddActivity("Applying your changes to the connected VPN.");
                await Reconnect();
            };
        }
        reconnectTimer.Stop();
        reconnectTimer.Start();
    }

    // Wi-Fi or Ethernet coming and going changes which adapters can carry rules, so re-read them and re-apply.
    void WatchNetworkChanges()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;
        Closed += (_, _) =>
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
        };
    }

    // Windows is logging off or shutting down: really exit instead of hiding to the tray, and drop the rules first.
    void OnSessionEnding(object? sender, Microsoft.Win32.SessionEndingEventArgs args)
    {
        closing = true;
        try { directRouting?.Dispose(); }
        catch (Exception) { /* Windows removes dynamic rules when the process ends. */ }
        DispatcherQueue.TryEnqueue(Close);
    }

    void OnNetworkAddressChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (closing || preview || verifying) return;
        if (adapterChangeTimer is null)
        {
            adapterChangeTimer = DispatcherQueue.CreateTimer();
            adapterChangeTimer.Interval = TimeSpan.FromMilliseconds(1500);
            adapterChangeTimer.IsRepeating = false;
            adapterChangeTimer.Tick += (_, _) =>
            {
                if (closing) return;
                ReloadWindowsAdapters();
                Refresh(refreshRouteBrowser: false);
            };
        }
        adapterChangeTimer.Stop();
        adapterChangeTimer.Start();
    });
}
