using System.Net;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RouteFinder.App;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    sealed class SettingsFields
    {
        public required InfoBar Error;
        public required ComboBox Language, WindowSize;
        public required ToggleSwitch Tray, Tips, Recover, KillSwitch, BypassLan, Portable, AutoMtu, ProxyAllTraffic;
        public required TextBox Idle, PeerTimeout, TestTimeout, Mtu, Keepalive, Bypass, Excluded, ProxyEndpoint, ProxyUser;
        public required PasswordBox ProxyPassword;
        public required DnsEditor Dns;
    }

    SettingsFields? settings;

    void BuildSettingsPage()
    {
        var host = SettingsHost;
        host.Children.Clear();
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true, Title = UiLanguage.Text("Could not save settings") };
        host.Children.Add(error);

        BuildPresetSection(host);

        Section("General");
        var language = new ComboBox { ItemsSource = new[] { "English", "فارسی", "العربية" }, MinWidth = 160 };
        Row("\uF2B7", "Language", "Takes effect after you restart RouteFinder.", language);
        var size = new ComboBox { ItemsSource = WindowSizes.Select(item => $"{item.Width} × {item.Height}").ToArray(), MinWidth = 160 };
        Row("\uE740", "Window size", null, size);
        var tray = Toggle("\uE8A7", "Minimize to tray while connected", "Keep RouteFinder running in the notification area.");
        var tips = Toggle("\uE82F", "Show tips", "Show a short how-to on the Home page at startup.");

        Section("Connection");
        var recover = Toggle("\uE72C", "Reconnect automatically", "Try once to restart the tunnel if it stops.");
        var killSwitch = Toggle("\uE72E", "Kill switch",
            "Block traffic outside the tunnel. With Selected apps, other apps lose network access until you disconnect.");
        var bypassLan = Toggle("\uE968", "Allow local network", "Keep printers, NAS, and other local IPv4 devices outside the tunnel.");
        var idle = Number("\uE916", "Auto-disconnect delay", "Seconds to wait after watched apps close (Watch apps).");

        Section("Advanced network");
        var portable = Toggle("\uE839", "Virtual adapter mode", "Use a WireSock virtual adapter instead of transparent per-app mode.");
        var autoMtu = Toggle("\uE9D9", "Auto MTU", "Probe the path and pick the largest working packet size.");
        var mtu = Number("\uE9D9", "Manual MTU", "Leave blank to use the route's value.");
        var keepalive = Number("\uE916", "Keepalive", "Seconds between keepalive packets. Leave blank to use the route's value.");
        var dns = new DnsEditor();
        Row("", "DNS override", "Pick a provider or enter addresses. Automatic uses the route's own DNS.", dns, stacked: true);
        var bypass = Text("\uE7BA", "Bypass list", "IP addresses, CIDR ranges, or hostnames that skip the tunnel.");
        var excluded = Text("\uE71D", "Entire PC exclusions", "Executable paths that skip the tunnel in Entire PC mode, one per line.", multiline: true);
        var peerTimeout = Number("\uE916", "Peer response timeout", "Seconds to wait for the server to answer.");
        var testTimeout = Number("\uE916", "Route test timeout", "Seconds each route test may take.");

        Section("SOCKS5 proxy");
        var proxyEndpoint = Text("\uE968", "Proxy address", "host:port. Leave blank to connect directly.");
        var proxyUser = Text("\uE77B", "Username", null);
        var proxyPassword = new PasswordBox { Width = 260 };
        Row("\uE8D7", "Password", null, proxyPassword);
        var proxyAllTraffic = Toggle("\uE8AB", "Send all tunnel traffic through the proxy", null);

        Section("Maintenance");
        foreach (var action in MaintenanceActions())
        {
            var run = action.Run;
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(6), MinHeight = 64,
                Content = RowContent(action.Glyph, action.Label, action.Description, new FontIcon { Glyph = "\uE76C", FontSize = 12 })
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, UiLanguage.Text(action.Label));
            button.Click += async (_, _) => await RunMaintenance(run);
            host.Children.Add(button);
        }

        Section("About");
        Row("\uE946", "RouteFinder", $"Version {typeof(MainWindow).Assembly.GetName().Version}",
            new HyperlinkButton { Content = UiLanguage.Text("Release notes"), NavigateUri = new Uri("https://github.com/AMIRSRAD/RouteFinder/releases") });

        settings = new SettingsFields
        {
            Error = error, Language = language, WindowSize = size, Tray = tray, Tips = tips, Recover = recover,
            KillSwitch = killSwitch, BypassLan = bypassLan, Portable = portable, AutoMtu = autoMtu, ProxyAllTraffic = proxyAllTraffic,
            Idle = idle, PeerTimeout = peerTimeout, TestTimeout = testTimeout, Mtu = mtu, Keepalive = keepalive, Dns = dns,
            Bypass = bypass, Excluded = excluded, ProxyEndpoint = proxyEndpoint, ProxyUser = proxyUser, ProxyPassword = proxyPassword
        };
        LoadSettingsFields();

        void Section(string title) => host.Children.Add(new TextBlock
        {
            Text = UiLanguage.Text(title), Style = (Style)Root.Resources["SectionLabel"], Margin = new Thickness(2, 20, 0, 6)
        });

        void Row(string glyph, string title, string? description, FrameworkElement control, bool stacked = false) =>
            host.Children.Add(new Border
            {
                Style = (Style)Root.Resources["Card"], CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 12, 16, 12),
                MinHeight = 64, Child = RowContent(glyph, title, description, control, stacked)
            });

        ToggleSwitch Toggle(string glyph, string title, string? description)
        {
            var control = new ToggleSwitch { MinWidth = 0, OnContent = UiLanguage.Text("On"), OffContent = UiLanguage.Text("Off") };
            Row(glyph, title, description, control);
            return control;
        }

        TextBox Text(string glyph, string title, string? description, bool multiline = false)
        {
            var control = new TextBox { Width = multiline ? double.NaN : 260, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
            if (multiline) { control.MinHeight = 72; control.MaxHeight = 140; }
            Row(glyph, title, description, control, stacked: multiline);
            return control;
        }

        TextBox Number(string glyph, string title, string? description)
        {
            var control = new TextBox { Width = 120, InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } } };
            Row(glyph, title, description, control);
            return control;
        }
    }

    Grid RowContent(string glyph, string title, string? description, FrameworkElement control, bool stacked = false)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var icon = new Border { Style = (Style)Root.Resources["IconTile"], VerticalAlignment = stacked ? VerticalAlignment.Top : VerticalAlignment.Center };
        icon.Child = new FontIcon { Glyph = glyph, Style = (Style)Root.Resources["TileGlyph"] };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = UiLanguage.Text(title), TextWrapping = TextWrapping.Wrap });
        if (description is not null)
            text.Children.Add(new TextBlock
            {
                Text = UiLanguage.Text(description), TextWrapping = TextWrapping.Wrap, Style = (Style)Root.Resources["Caption"]
            });
        Grid.SetColumn(text, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        if (stacked)
        {
            Grid.SetRow(control, 1);
            Grid.SetColumn(control, 1);
            Grid.SetColumnSpan(control, 2);
        }
        else
        {
            Grid.SetColumn(control, 2);
            control.HorizontalAlignment = HorizontalAlignment.Right;
        }
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    void LoadSettingsFields()
    {
        if (settings is not { } f) return;
        f.Error.IsOpen = false;
        f.Language.SelectedIndex = UiLanguage.Index;
        f.WindowSize.SelectedIndex = Math.Clamp(preferences.WindowPreset, 0, WindowSizes.Length - 1);
        f.Tray.IsOn = preferences.MinimizeToTray;
        f.Tips.IsOn = preferences.ShowTutorials;
        f.Recover.IsOn = preferences.RecoverStoppedTunnel;
        f.KillSwitch.IsOn = preferences.KillSwitch;
        f.BypassLan.IsOn = preferences.BypassLan;
        f.Idle.Text = preferences.OnDemandIdleSeconds.ToString();
        f.Portable.IsOn = preferences.PortableEngine;
        f.AutoMtu.IsOn = preferences.AutoMtu;
        f.Mtu.Text = preferences.TunnelMtu?.ToString() ?? "";
        f.Keepalive.Text = preferences.KeepaliveSeconds?.ToString() ?? "";
        f.Dns.Load(SplitValues(preferences.DnsOverride));
        f.Bypass.Text = preferences.BypassDestinations;
        f.Excluded.Text = string.Join(Environment.NewLine, preferences.ExcludedApplications ?? []);
        f.PeerTimeout.Text = preferences.PeerResponseSeconds.ToString();
        f.TestTimeout.Text = preferences.RouteTestSeconds.ToString();
        var proxy = ConfiguredProxy;
        f.ProxyEndpoint.Text = proxy?.Endpoint ?? "";
        f.ProxyUser.Text = proxy?.Username ?? "";
        f.ProxyPassword.Password = proxy?.Password ?? "";
        f.ProxyAllTraffic.IsOn = proxy?.AllTraffic == true;
    }

    void DiscardSettings(object sender, RoutedEventArgs args) => LoadSettingsFields();

    void SaveSettingsClicked(object sender, RoutedEventArgs args)
    {
        if (settings is not { } f || busy) return;
        try
        {
            var dnsAddresses = f.Dns.Servers;
            if (dnsAddresses.Any(value => !IPAddress.TryParse(value, out _)))
                throw new FormatException("Enter valid IP addresses for the DNS override.");
            var destinations = SplitValues(f.Bypass.Text);
            if (destinations.Any(value => !IPAddress.TryParse(value, out _) &&
                !IPNetwork.TryParse(value, out _) && Uri.CheckHostName(value) != UriHostNameType.Dns))
                throw new FormatException("Bypass entries must be IP addresses, CIDR ranges, or hostnames.");
            var paths = f.Excluded.Text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (paths.Any(path => !Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                path.IndexOfAny(['|', '*', '?']) >= 0))
                throw new FormatException("Each app exclusion needs a full executable path.");
            string endpoint = f.ProxyEndpoint.Text.Trim();
            ProxyOverride? updatedProxy = null;
            if (endpoint.Length > 0)
            {
                TunnelProfile.SplitEndpoint(endpoint);
                string username = f.ProxyUser.Text;
                string password = f.ProxyPassword.Password;
                if ((username.Length == 0) != (password.Length == 0) || username.Contains('\0') || password.Contains('\0') ||
                    Encoding.UTF8.GetByteCount(username) > 255 || Encoding.UTF8.GetByteCount(password) > 255)
                    throw new FormatException("Enter both proxy credentials, up to 255 bytes each, or leave both blank.");
                updatedProxy = new ProxyOverride(endpoint, username, password, f.ProxyAllTraffic.IsOn);
            }
            var updated = preferences with
            {
                KillSwitch = f.KillSwitch.IsOn, PortableEngine = f.Portable.IsOn, AutoMtu = f.AutoMtu.IsOn,
                MinimizeToTray = f.Tray.IsOn, RecoverStoppedTunnel = f.Recover.IsOn, ShowTutorials = f.Tips.IsOn,
                WindowPreset = Math.Max(f.WindowSize.SelectedIndex, 0),
                OnDemandIdleSeconds = Number(f.Idle, "Auto-disconnect delay", 5, 3600)!.Value,
                BypassLan = f.BypassLan.IsOn, DnsOverride = string.Join(",", dnsAddresses),
                BypassDestinations = string.Join(",", destinations.Distinct(StringComparer.OrdinalIgnoreCase)),
                ExcludedApplications = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                TunnelMtu = Number(f.Mtu, "MTU", 576, 65535, optional: true),
                KeepaliveSeconds = Number(f.Keepalive, "Keepalive", 0, 65535, optional: true),
                PeerResponseSeconds = Number(f.PeerTimeout, "Peer response timeout", 5, 180)!.Value,
                RouteTestSeconds = Number(f.TestTimeout, "Route test timeout", 5, 180)!.Value,
                SocksProxyOverride = "", SocksProxyCredentials = updatedProxy
            };
            if (!preview) WriteStore("preferences", () => ProfileStore.SavePreferences(updated));
            bool resized = updated.WindowPreset != preferences.WindowPreset;
            preferences = updated;
            bool languageChanged = f.Language.SelectedIndex >= 0 && f.Language.SelectedIndex != UiLanguage.Index;
            if (languageChanged && !preview) UiLanguage.Save(f.Language.SelectedIndex);
            f.Error.IsOpen = false;
            if (resized) ApplyWindowSize();
            AddActivity("Saved settings.");
            Report(UiLanguage.Text(languageChanged ? "Settings saved. Restart RouteFinder to apply the language." : "Settings saved."),
                InfoBarSeverity.Success);
            Refresh();
        }
        catch (Exception error)
        {
            f.Error.Message = error.Message;
            f.Error.IsOpen = true;
        }
    }

    static string[] SplitValues(string text) =>
        text.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    static int? Number(TextBox field, string label, int minimum, int maximum, bool optional = false)
    {
        string value = field.Text.Trim();
        if (optional && value.Length == 0) return null;
        if (!int.TryParse(value, out int number) || number < minimum || number > maximum)
            throw new FormatException($"{label} must be between {minimum} and {maximum}.");
        return number;
    }
}
