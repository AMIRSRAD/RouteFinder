using System.Net;
using System.Net.NetworkInformation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    async void ManageAppRoutingProfiles(object sender, RoutedEventArgs args)
    {
        if (busy || windowsAppDraft is not { } draft) return;
        if (AppRouterActive)
        {
            Report("Stop routing before loading or saving a profile.", InfoBarSeverity.Informational);
            return;
        }
        var saved = preferences.AppRoutingProfiles ?? [];
        var picker = new ComboBox { Header = "Saved profiles", ItemsSource = saved.Select(item => item.Name).ToArray(),
            SelectedIndex = saved.Count > 0 ? 0 : -1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Header = "Name for current rules", MaxLength = 80 };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(picker);
        content.Children.Add(name);
        var dialog = MakeDialog("Routing profiles", content, "Load selected");
        dialog.SecondaryButtonText = "Save current rules";
        dialog.IsPrimaryButtonEnabled = picker.SelectedIndex >= 0;
        dialog.IsSecondaryButtonEnabled = false;
        picker.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = picker.SelectedIndex >= 0;
        name.TextChanged += (_, _) => dialog.IsSecondaryButtonEnabled = name.Text.Trim().Length > 0;
        try
        {
            switch (await dialog.ShowAsync())
            {
                case ContentDialogResult.Primary when picker.SelectedIndex >= 0:
                    var selected = saved[picker.SelectedIndex];
                    windowsAppDraft = new(selected.Adapters, StringComparer.OrdinalIgnoreCase);
                    appVpnDraft = new(selected.VpnRoutes, StringComparer.OrdinalIgnoreCase);
                    windowsFallbackDraft = selected.DefaultAdapter;
                    suggestedWindowsApps.Clear();
                    ReloadWindowsAdapters();
                    Report($"Loaded {selected.Name}. Select Save & apply to start routing.", InfoBarSeverity.Success);
                    break;
                case ContentDialogResult.Secondary:
                    string label = name.Text.Trim();
                    var entries = saved.Where(item => !item.Name.Equals(label, StringComparison.OrdinalIgnoreCase)).ToList();
                    entries.Add(new(label, new(draft, StringComparer.OrdinalIgnoreCase),
                        new(appVpnDraft, StringComparer.OrdinalIgnoreCase), windowsFallbackDraft));
                    var updated = preferences with { AppRoutingProfiles = entries };
                    WriteStore("preferences", () => ProfileStore.SavePreferences(updated));
                    preferences = updated;
                    Report($"Saved routing profile {label}.", InfoBarSeverity.Success);
                    break;
            }
        }
        catch (Exception error) { Report(error.Message); }
    }

    async void ManageInterfaces(object sender, RoutedEventArgs args)
    {
        if (busy) return;
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (adapters.Length == 0) { Report("No Windows interfaces are available."); return; }
            var picker = new ComboBox { Header = "Interface", ItemsSource = adapters.Select(item => item.Name).ToArray(),
                SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var details = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var dns = new TextBox { Header = "DNS servers", PlaceholderText = "IPv4 or IPv6 addresses separated by commas" };
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(picker);
            content.Children.Add(details);
            content.Children.Add(dns);
            content.Children.Add(new TextBlock { Text = "DNS changes are saved by Windows for this interface.", TextWrapping = TextWrapping.Wrap });
            var dialog = MakeDialog("Interfaces and DNS", content, "Apply DNS");
            dialog.SecondaryButtonText = "Use automatic DNS";
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = !AppRouterActive && !Connected;
            void ShowInterface()
            {
                var adapter = adapters[picker.SelectedIndex];
                var ip = adapter.GetIPProperties();
                details.Text = $"{adapter.OperationalStatus} · {adapter.NetworkInterfaceType}\n" +
                    $"Addresses: {string.Join(", ", ip.UnicastAddresses.Select(item => item.Address))}\n" +
                    $"Gateways: {string.Join(", ", ip.GatewayAddresses.Select(item => item.Address))}\n" +
                    $"Current DNS: {string.Join(", ", ip.DnsAddresses)}";
                dns.Text = string.Join(", ", ip.DnsAddresses);
            }
            picker.SelectionChanged += (_, _) => ShowInterface();
            ShowInterface();
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None || busy || AppRouterActive || Connected) return;
            var servers = new List<IPAddress>();
            if (result == ContentDialogResult.Primary)
            {
                foreach (string value in dns.Text.Split([',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!IPAddress.TryParse(value, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                    { Report($"Invalid DNS address: {value}"); return; }
                    if (!servers.Contains(address)) servers.Add(address);
                }
                if (servers.Count == 0) { Report("Enter DNS addresses or choose Use automatic DNS."); return; }
            }
            if (!EnsureAdministrator()) return;
            string id = adapters[picker.SelectedIndex].Id;
            await Run("Updating interface DNS…", async () =>
            {
                await WindowsNetworkTools.SetAdapterDns(id, servers.ToArray());
                Report("Windows interface DNS updated.", InfoBarSeverity.Success);
            });
        }
        catch (Exception error) { Report(error.Message); }
    }

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
