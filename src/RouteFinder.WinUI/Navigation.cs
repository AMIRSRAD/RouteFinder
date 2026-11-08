using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RouteFinder.App;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    string currentPage = "home";

    void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string page = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "home";
        ShowPage(page);
    }

    internal void ShowPage(string page)
    {
        if (page == "settings" && currentPage != "settings") LoadSettingsFields();
        if (page == "network" && currentPage != "network") LoadInterfaces();
        if (page == "providers") RefreshProviders();
        currentPage = page;
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        AppsPage.Visibility = page == "apps" ? Visibility.Visible : Visibility.Collapsed;
        RoutesPage.Visibility = page == "routes" ? Visibility.Visible : Visibility.Collapsed;
        ProvidersPage.Visibility = page == "providers" ? Visibility.Visible : Visibility.Collapsed;
        NetworkPage.Visibility = page == "network" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = page == "activity" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        object target = page switch
        {
            "apps" => NavApps,
            "routes" => NavRoutes,
            "providers" => NavProviders,
            "network" => NavNetwork,
            "activity" => NavActivity,
            "settings" => Nav.SettingsItem,
            _ => NavHome
        };
        if (!ReferenceEquals(Nav.SelectedItem, target)) Nav.SelectedItem = target;
        UpdateBusyStrip();
    }

    void GoToRoutes(object sender, RoutedEventArgs args) => ShowPage("routes");
    void GoToApps(object sender, RoutedEventArgs args) => ShowPage("apps");
    void OpenActivity(object sender, RoutedEventArgs args) => ShowPage("activity");

    // The Home page already shows operation progress in the status card.
    void UpdateBusyStrip() =>
        BusyStrip.Visibility = busy && currentPage != "home" ? Visibility.Visible : Visibility.Collapsed;

    void ApplyTitleBarTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 150, 150, 150) : Windows.UI.Color.FromArgb(255, 120, 120, 120);
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(24, 255, 255, 255) : Windows.UI.Color.FromArgb(24, 0, 0, 0);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(40, 0, 0, 0);
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
    }

    enum StatusTone { Neutral, Working, Good, Problem }

    StatusTone CurrentTone()
    {
        if (busy) return StatusTone.Working;
        if (preview) return simulatedConnection ? StatusTone.Good : StatusTone.Neutral;
        if (!session.HasTunnel) return session.NeedsCleanup || reconnectFailed ? StatusTone.Problem : StatusTone.Neutral;
        try
        {
            if (!session.TunnelIsActive) return StatusTone.Problem;
            bool handshake = session.PeerResponseObserved || session.State is { TimeSinceLastHandshakeMilliseconds: >= 0 } or { RxBytes: > 0 };
            if (!handshake) return StatusTone.Working;
            return HealthAlert.Visibility == Visibility.Visible ? StatusTone.Working : StatusTone.Good;
        }
        catch (Exception) { return StatusTone.Working; }
    }

    void ApplyStatusTone()
    {
        var tone = CurrentTone();
        string key = tone switch
        {
            StatusTone.Good => "SystemFillColorSuccessBrush",
            StatusTone.Working => "SystemFillColorCautionBrush",
            StatusTone.Problem => "SystemFillColorCriticalBrush",
            _ => "ControlStrongFillColorDefaultBrush"
        };
        if (!Application.Current.Resources.TryGetValue(key, out var resource) || resource is not SolidColorBrush fill) return;
        StatusDot.Fill = fill;
        StatusLabel.Text = UiLanguage.Text(tone switch
        {
            StatusTone.Good => "CONNECTED",
            StatusTone.Working => busy ? "WORKING" : "CONNECTING",
            StatusTone.Problem => "NEEDS ATTENTION",
            _ => "NOT CONNECTED"
        });
        StatusLabel.Foreground = tone == StatusTone.Neutral
            ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] : fill;
        ConnectRing.BorderBrush = tone == StatusTone.Neutral
            ? (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"] : fill;
        // A diagonal wash from the top-left corner in the state color; hidden when idle.
        var color = fill.Color;
        StatusGlow.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(56, color.R, color.G, color.B), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(0, color.R, color.G, color.B), Offset = 0.7 }
            }
        };
        StatusGlow.Opacity = tone == StatusTone.Neutral ? 0 : 1;
    }
}
