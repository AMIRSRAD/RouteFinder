using System.Net;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    async void OpenSettings(object sender, RoutedEventArgs args)
    {
        var fields = new StackPanel { Spacing = 12, MinWidth = 315 };
        var errorMessage = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var minimize = Toggle("Minimize to tray while connected", preferences.MinimizeToTray);
        var recover = Toggle("Reconnect once if the tunnel stops", preferences.RecoverStoppedTunnel);
        var idle = Input("App-launch connection idle timeout in seconds", preferences.OnDemandIdleSeconds.ToString());
        var killSwitch = Toggle("Kill switch", preferences.KillSwitch);
        fields.Children.Add(new TextBlock
        {
            Text = "Blocks traffic outside the tunnel. With selected-app routing, other apps lose network access until you disconnect.",
            TextWrapping = TextWrapping.Wrap, Opacity = .8
        });
        var portable = Toggle("WireSock virtual adapter", preferences.PortableEngine);
        var autoMtu = Toggle("Auto MTU", preferences.AutoMtu);
        var bypassLan = Toggle("Bypass local IPv4 networks", preferences.BypassLan);
        var dns = Input("DNS override · IPs separated by commas", preferences.DnsOverride);
        var bypass = Input("Bypass · IPs, CIDR ranges, or hostnames", preferences.BypassDestinations);
        var excluded = Input("Entire PC exclusions · one executable path per line",
            string.Join(Environment.NewLine, preferences.ExcludedApplications ?? []));
        excluded.AcceptsReturn = true;
        excluded.MaxHeight = 110;
        var mtu = Input("Manual MTU · blank uses the profile", preferences.TunnelMtu?.ToString());
        var keepalive = Input("Keepalive seconds · blank uses the profile", preferences.KeepaliveSeconds?.ToString());
        var responseTime = Input("Peer response timeout in seconds", preferences.PeerResponseSeconds.ToString());
        var testTime = Input("Route test timeout in seconds", preferences.RouteTestSeconds.ToString());
        var proxy = ConfiguredProxy;
        var proxyEndpoint = Input("SOCKS5 proxy · host:port", proxy?.Endpoint);
        var proxyUser = Input("SOCKS5 username", proxy?.Username);
        var proxyPassword = new PasswordBox { Header = "SOCKS5 password", Password = proxy?.Password ?? "" };
        fields.Children.Add(proxyPassword);
        var proxyAllTraffic = Toggle("Use SOCKS5 for all tunnel traffic", proxy?.AllTraffic == true);

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(errorMessage);
        content.Children.Add(new ScrollViewer { Content = fields, MaxHeight = 430 });
        var dialog = MakeDialog("Connection settings", content, "Save");
        dialog.PrimaryButtonClick += (senderDialog, click) =>
        {
            try
            {
                var dnsAddresses = SplitValues(dns.Text);
                if (dnsAddresses.Any(value => !IPAddress.TryParse(value, out _)))
                    throw new FormatException("Enter valid IP addresses for the DNS override.");
                var destinations = SplitValues(bypass.Text);
                if (destinations.Any(value => !IPAddress.TryParse(value, out _) &&
                    !IPNetwork.TryParse(value, out _) && Uri.CheckHostName(value) != UriHostNameType.Dns))
                    throw new FormatException("Bypass entries must be IP addresses, CIDR ranges, or hostnames.");
                var paths = excluded.Text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (paths.Any(path => !Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    path.IndexOfAny(['|', '*', '?']) >= 0))
                    throw new FormatException("Each app exclusion needs a full executable path.");
                string endpoint = proxyEndpoint.Text.Trim();
                ProxyOverride? updatedProxy = null;
                if (endpoint.Length > 0)
                {
                    TunnelProfile.SplitEndpoint(endpoint);
                    string username = proxyUser.Text;
                    string password = proxyPassword.Password;
                    if ((username.Length == 0) != (password.Length == 0) || username.Contains('\0') || password.Contains('\0') ||
                        Encoding.UTF8.GetByteCount(username) > 255 || Encoding.UTF8.GetByteCount(password) > 255)
                        throw new FormatException("Enter both proxy credentials, up to 255 bytes each, or leave both blank.");
                    updatedProxy = new ProxyOverride(endpoint, username, password, proxyAllTraffic.IsOn);
                }
                var updated = preferences with
                {
                    KillSwitch = killSwitch.IsOn, PortableEngine = portable.IsOn, AutoMtu = autoMtu.IsOn,
                    MinimizeToTray = minimize.IsOn, RecoverStoppedTunnel = recover.IsOn,
                    OnDemandIdleSeconds = Number(idle, "Idle timeout", 5, 3600)!.Value,
                    BypassLan = bypassLan.IsOn, DnsOverride = string.Join(",", dnsAddresses),
                    BypassDestinations = string.Join(",", destinations.Distinct(StringComparer.OrdinalIgnoreCase)),
                    ExcludedApplications = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    TunnelMtu = Number(mtu, "MTU", 576, 65535, optional: true),
                    KeepaliveSeconds = Number(keepalive, "Keepalive", 0, 65535, optional: true),
                    PeerResponseSeconds = Number(responseTime, "Peer response timeout", 5, 180)!.Value,
                    RouteTestSeconds = Number(testTime, "Route test timeout", 5, 180)!.Value,
                    SocksProxyOverride = "", SocksProxyCredentials = updatedProxy
                };
                if (!preview) ProfileStore.SavePreferences(updated);
                preferences = updated;
                AddActivity("Saved connection settings.");
                Refresh();
            }
            catch (Exception error)
            {
                errorMessage.Text = error.Message;
                errorMessage.Visibility = Visibility.Visible;
                click.Cancel = true;
            }
        };
        await dialog.ShowAsync();
        proxyPassword.Password = "";

        ToggleSwitch Toggle(string header, bool value)
        {
            var control = new ToggleSwitch { Header = header, IsOn = value };
            fields.Children.Add(control);
            return control;
        }

        TextBox Input(string header, string? value)
        {
            var control = new TextBox { Header = header, Text = value ?? "" };
            fields.Children.Add(control);
            return control;
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
