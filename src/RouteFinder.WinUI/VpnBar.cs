using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RouteFinder.App;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    string? renderedRouteKey;

    // Tells the user, on the Apps page, where "Through VPN" apps go and whether that is happening yet.
    // The route name appears in each app's menu, so rebuild the rows when the route changes.
    void RenderAppsIfRouteChanged()
    {
        if (renderedRouteKey == route?.Key) return;
        renderedRouteKey = route?.Key;
        RenderApps();
    }

    void RefreshVpnBar()
    {
        bool entirePc = Scope.SelectedIndex == 1;
        int vpnCount = VpnTargets().Length;
        string routeName = route is { } current
            ? current.Title + (EffectiveUplink(current) is { } uplink ? $" via {UplinkLabel(uplink)}" : "") : "";
        bool tunnelUp = Connected && !appRouterVpn;
        string apps = vpnCount == 1 ? "1 app" : $"{vpnCount} apps";
        string title, detail, button;
        string tone;
        if (route is null)
        {
            (title, detail, button, tone) = ("No route selected", "Choose a route to connect.", "Choose route", "neutral");
        }
        else if (tunnelUp)
        {
            (title, detail, button, tone) = (entirePc ? $"All apps are going through {routeName}" : $"{apps} going through {routeName}",
                "Reopen apps that were already running.", "Disconnect", "good");
        }
        else if (session.NeedsCleanup)
        {
            (title, detail, button, tone) = ("The last session needs cleanup", "Retry cleanup to continue.", "Retry cleanup", "problem");
        }
        else if (!entirePc && vpnCount == 0)
        {
            (title, detail, button, tone) = ($"Route: {routeName}", "No app set to Through VPN.", "Connect", "neutral");
        }
        else
        {
            (title, detail, button, tone) = (entirePc ? $"All apps will go through {routeName}" : $"{apps} will go through {routeName}",
                "Not connected", "Connect", "neutral");
        }
        // Another VPN holds Windows' default route: say once that RouteFinder keeps its apps off it.
        if (foreignVpnName is not null && route is not null && tone != "problem" && !preferences.PortableEngine)
            detail = $"Another VPN ({foreignVpnName}) is on. VPN apps skip it.";
        VpnBarTitle.Text = UiLanguage.Text(title);
        VpnBarDetail.Text = UiLanguage.Text(detail);
        VpnBarButton.Content = UiLanguage.Text(button);
        VpnBarButton.Style = tone == "good" ? null : (Style)Application.Current.Resources["AccentButtonStyle"];
        VpnBarButton.IsEnabled = route is null ? !busy : ConnectButton.IsEnabled;
        string key = tone switch
        {
            "good" => "SystemFillColorSuccessBrush",
            "problem" => "SystemFillColorCriticalBrush",
            _ => "ControlStrongFillColorDefaultBrush"
        };
        if (Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush fill) VpnBarDot.Fill = fill;
    }

    async void VpnBarClicked(object sender, RoutedEventArgs args)
    {
        if (route is null) { ShowPage("routes"); return; }
        await ChangeConnection();
    }
}
