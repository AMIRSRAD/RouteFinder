using System.Net;
using System.Net.NetworkInformation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    async void InspectAppRouting(object sender, RoutedEventArgs args)
    {
        if (busy || windowsAppDraft is null) return;
        try
        {
            var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 160, MaxHeight = 400 };
            var content = new StackPanel { Spacing = 8 };
            var refresh = new Button { Content = "Refresh connections" };
            content.Children.Add(refresh);
            content.Children.Add(output);
            async Task Read()
            {
                refresh.IsEnabled = false;
                output.Text = "Reading established TCP connections…";
                try { output.Text = await ReadAppRoutingReport(); }
                catch (Exception error) { output.Text = error.Message; }
                finally { refresh.IsEnabled = true; }
            }
            refresh.Click += async (_, _) => await Read();
            _ = Read();
            await MakeDialog("Live routing inspector", content).ShowAsync();
        }
        catch (Exception error) { Report(error.Message); }
    }

    async Task<string> ReadAppRoutingReport()
    {
        var adapterNames = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses.Select(ip => (ip.Address, adapter.Name)))
            .ToLookup(item => item.Address, item => item.Name);
        bool tunnelUp = session.HasTunnel && !appRouterVpn;
        bool transparent = !preferences.PortableEngine && !connectedForConsole;
        var lines = new List<string>
        {
            tunnelUp ? $"VPN: connected to {route?.Title ?? "the selected route"} ({(transparent ? "transparent mode" : "virtual adapter mode")})"
                : appRouterVpn ? "VPN: running for adapter rules" : "VPN: not connected. Through VPN apps use the route only after you press Connect on Home.",
            $"Adapter rules: {(AppRouterActive ? "on" : "off")}",
            transparent && tunnelUp
                ? "In transparent mode Windows still reports your Wi-Fi or Ethernet address for tunneled connections, so the interface below does not show whether an app uses the VPN. Use Activity > Verify routing to compare public IPs."
                : "The interface below is the Windows source interface. It does not prove which way traffic leaves."
        };
        var connections = (await WindowsNetworkTools.ReadTcpConnections())
            .ToLookup(item => item.Executable, StringComparer.OrdinalIgnoreCase);
        foreach (string path in apps)
        {
            string adapterId = windowsAppDraft?.GetValueOrDefault(path) ?? "";
            string mode;
            if (!checkedApps.Contains(path)) mode = "Off (not routed)";
            else if (IsAdapterApp(path))
            {
                string label = windowsAdapters.FirstOrDefault(item => item.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase))?.Label
                    ?? "saved adapter unavailable";
                mode = $"Direct · {label} · {(directRouting is not null ? "rule active" : "rule not applied yet")}";
            }
            else if (appVpnDraft.TryGetValue(path, out var key))
                mode = $"Through VPN (adapter rules) · {Routes().FirstOrDefault(choice => choice.Key == key)?.Title ?? key}";
            else if (Scope.SelectedIndex != 1)
                mode = $"Through VPN · {route?.Title ?? "no route selected"} via {(route is { } current ? UplinkLabel(UplinkFor(current)) : "Automatic")}" +
                    $" · {(tunnelUp ? "tunnel connected" : "waiting for Connect")}";
            else mode = $"Entire PC · {route?.Title ?? "no route selected"} · {(tunnelUp ? "tunnel connected" : "waiting for Connect")}";
            lines.Add($"\n{Path.GetFileName(path)} · {mode}");
            if (!File.Exists(path)) lines.Add("Executable missing.");
            else if (!connections.Contains(path)) lines.Add("No established TCP connections found.");
            foreach (var connection in connections[path].Take(30))
            {
                var names = IPAddress.TryParse(connection.LocalAddress, out var address) ? adapterNames[address] : [];
                lines.Add($"{connection.LocalAddress} → {connection.RemoteAddress}:{connection.RemotePort} · {string.Join(", ", names.DefaultIfEmpty("Source interface unknown"))}");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }
}
