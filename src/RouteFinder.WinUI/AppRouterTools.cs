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
        var lines = new List<string> { AppRouterActive ? "App Router active" : "App Router stopped",
            $"VPN status: {(appRouterVpn ? session.TunnelIsActive ? "Tunnel active" : "Tunnel stopped" : "Off")}",
            "Established TCP connections show the Windows source interface. They do not prove VPN egress." };
        var connections = (await WindowsNetworkTools.ReadTcpConnections())
            .ToLookup(item => item.Executable, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, adapterId) in windowsAppDraft ?? [])
        {
            string effectiveId = adapterId.Length == 0 && !appVpnDraft.ContainsKey(path) ? windowsFallbackDraft ?? "" : adapterId;
            string adapter = windowsAdapters.FirstOrDefault(item => item.Id.Equals(effectiveId, StringComparison.OrdinalIgnoreCase))?.Label
                ?? (effectiveId.Length == 0 ? "Automatic" : "Saved adapter unavailable");
            string mode = appVpnDraft.TryGetValue(path, out var key)
                ? $"VPN · {Routes().FirstOrDefault(route => route.Key == key)?.Title ?? key}" : "Direct";
            lines.Add($"\n{Path.GetFileName(path)} · {mode} · {adapter}");
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
