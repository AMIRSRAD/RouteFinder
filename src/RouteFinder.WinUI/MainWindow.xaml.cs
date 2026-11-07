using System.Collections.ObjectModel;
using System.Security.Principal;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RouteFinder.App;
using RouteFinder.Core;
using RouteFinder.Service;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow : Window
{
    sealed record RouteChoice(string Key, string Title, string Source, string Detail,
        SavedProfile? Profile = null, ServiceRoute? Service = null);

    readonly ConnectionSession session = new(new EzConnectClient());
    readonly ObservableCollection<string> activity = [];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly List<SavedProfile> profiles = [];
    readonly List<ServiceRoute> serviceRoutes = [];
    readonly List<string> apps = [];
    readonly HashSet<string> checkedApps = new(StringComparer.OrdinalIgnoreCase);
    readonly bool preview;
    ProfileStore.Preferences preferences = new(false, []);
    RouteChoice? route;
    DateTimeOffset connectedAt;
    string? connectedEndpoint;
    (ulong Rx, ulong Tx, long At)? previousTraffic;
    string? trafficRate;
    bool ready;
    bool busy;
    bool closing;
    bool exitRequested;
    bool simulatedConnection;
    bool Connected => preview ? simulatedConnection : session.HasTunnel;

    public MainWindow(bool preview, bool verifyStartup = false)
    {
        this.preview = preview;
        if (!preview) UiLanguage.Load();
        InitializeComponent();
        ApplyLanguage();
        ApplyActionIcons();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WindowHeader);
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.LightGray;
        }
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => { closing = true; timer.Stop(); tray?.Dispose(); trayMenu?.Dispose(); applicationIcon?.Dispose(); session.Dispose(); };
        session.Diagnostic += message => DispatcherQueue.TryEnqueue(() => AddActivity(message));
        session.RefreshTokenChanged += token =>
        {
            if (rememberedEmail is { } email) SaveLogin(email, token);
        };
        if (!preview && !verifyStartup)
        {
            Root.Loaded += RestoreAccountOnLoad;
            Root.Loaded += CheckMaintenanceOnLoad;
        }
        timer.Tick += async (_, _) =>
        {
            UpdateStatus();
            await CheckConnectionLifecycle();
        };
        InitializeTray();

        try
        {
            if (preview)
            {
                PreviewLabel.Visibility = Visibility.Visible;
                apps.AddRange([@"C:\Apps\Discord.exe", @"C:\Games\Counter-Strike 2\cs2.exe"]);
                profiles.Add(new SavedProfile { Name = "Amsterdam · Home relay" });
                serviceRoutes.AddRange([new("1", "DE", "Frankfurt", true), new("2", "TR", "Istanbul", false)]);
            }
            else
            {
                preferences = ProfileStore.LoadPreferences();
                profiles.AddRange(ProfileStore.Load());
                foreach (var measurement in ProfileStore.LoadMeasurements()) measurements.Add(measurement.Key, measurement.Value);
                apps.AddRange(ProfileStore.LoadApplications() ?? []);
                Scope.SelectedIndex = preferences.EntirePcMode ? 1 : 0;
            }
            checkedApps.UnionWith(apps.Except(preferences.UncheckedApplications ?? [], StringComparer.OrdinalIgnoreCase));
            route = Routes().FirstOrDefault(item => item.Key == preferences.LastConnectedRoute) ?? Routes().FirstOrDefault();
        }
        catch (Exception error) { Report(error.Message); }
        ready = true;
        ApplyWindowSize(center: true);
        if (!preview)
        {
            if (preferences.ShowTutorials) ShowRoutingTip();
        }
        RenderApps();
        Refresh();
        timer.Start();
    }

    IEnumerable<RouteChoice> Routes() =>
        profiles.Select(profile => new RouteChoice("local:" + profile.Name, profile.Name, "Imported config", "Local WireGuard or Amnezia configuration", Profile: profile))
        .Concat(serviceRoutes.Select(service => new RouteChoice(
            $"{service.RegionCode}:{service.Name}" + (service.OverIpv6 ? ":ipv6" : ""),
            string.IsNullOrWhiteSpace(service.CityName) ? $"Route {service.Name}" : service.CityName,
            "EZConnect", $"{service.RegionCode} · Route {service.Name}" + (service.OverIpv6 ? " · IPv6" : ""), Service: service)));

    void ResizeCards(object sender, SizeChangedEventArgs args)
    {
        bool stacked = args.NewSize.Width < 700;
        RouteColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        StackedRouteRow.Height = stacked ? GridLength.Auto : new GridLength(0);
        Cards.RowSpacing = stacked ? 10 : 0;
        Grid.SetColumn(RouteCard, stacked ? 0 : 1);
        Grid.SetRow(RouteCard, stacked ? 1 : 0);
        Grid.SetColumn(HeaderActions, stacked ? 0 : 1);
        Grid.SetColumnSpan(HeaderActions, stacked ? 2 : 1);
        Grid.SetRow(HeaderActions, stacked ? 1 : 0);
        HeaderActions.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    void ScopeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ready) return;
        preferences = preferences with { EntirePcMode = Scope.SelectedIndex == 1 };
        SavePreferences();
        Refresh();
    }

    void SavePreferences()
    {
        if (preview) return;
        try { ProfileStore.SavePreferences(preferences); }
        catch (Exception error) { Report($"Could not save preferences. {error.Message}"); }
    }

    void RenderApps()
    {
        AppRows.Children.Clear();
        foreach (string path in apps)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var check = new CheckBox
            {
                Content = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis },
                IsChecked = checkedApps.Contains(path), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, MinWidth = 0
            };
            AutomationProperties.SetName(check, name);
            ToolTipService.SetToolTip(check, path);
            check.Checked += (_, _) => { checkedApps.Add(path); SaveAppSelection(); Refresh(); };
            check.Unchecked += (_, _) => { checkedApps.Remove(path); SaveAppSelection(); Refresh(); };
            var fallback = new FontIcon { Glyph = "\uE7C3", FontSize = 16 };
            var icon = new Grid { Width = 18, Height = 18, Margin = new Thickness(10, 0, 8, 0) };
            icon.Children.Add(fallback);
            if (!preview) _ = LoadAppIcon(path, icon);
            var remove = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }, Padding = new Thickness(9, 6, 9, 6) };
            AutomationProperties.SetName(remove, $"Remove {name}");
            remove.Click += (_, _) => RemoveApplication(path);
            var row = new Grid { CornerRadius = new CornerRadius(8), Background = (Brush)Root.Resources["CardBorder"] };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(check, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(icon);
            row.Children.Add(check);
            row.Children.Add(remove);
            AppRows.Children.Add(row);
        }
    }

    static async Task LoadAppIcon(string path, Grid host)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32, ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null) return;
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            host.Children.Clear();
            host.Children.Add(new Image { Source = bitmap });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // Keep the generic icon for inaccessible executables.
        }
    }

    void RemoveApplication(string path)
    {
        var updated = apps.ToList();
        updated.Remove(path);
        try
        {
            if (!preview) ProfileStore.SaveApplications(updated);
            apps.Clear();
            apps.AddRange(updated);
            checkedApps.Remove(path);
            SaveAppSelection();
            RenderApps();
            Refresh();
        }
        catch (Exception error) { Report(error.Message); }
    }

    void SaveAppSelection()
    {
        preferences = preferences with { UncheckedApplications = apps.Where(path => !checkedApps.Contains(path)).ToArray() };
        SavePreferences();
    }

    void Refresh()
    {
        bool editable = !busy && !Connected && !session.NeedsCleanup;
        bool entirePc = Scope.SelectedIndex == 1;
        if (armed is { } watch && (entirePc || watch.RouteKey != route?.Key || !watch.Paths.SetEquals(checkedApps)))
            CancelWatch();
        Scope.IsEnabled = editable;
        AppRows.IsHitTestVisible = editable && !entirePc;
        foreach (var row in AppRows.Children.OfType<Grid>())
            foreach (var control in row.Children.OfType<Control>()) control.IsEnabled = editable && !entirePc;
        AppScroller.Visibility = apps.Count > 0 && !entirePc ? Visibility.Visible : Visibility.Collapsed;
        AddAppsButton.IsEnabled = editable && !entirePc && !preview;
        ChooseRouteButton.IsEnabled = editable && Routes().Any();
        AddRouteButton.IsEnabled = editable && !preview;
        SetActionLabel(AccountButton, session.SignedIn ? "EZConnect account" : "Sign in to EZConnect");
        AccountButton.IsEnabled = editable;
        AccountCommand.Text = session.SignedIn ? "Manage EZConnect account…" : "Add EZConnect account…";
        AdvancedButton.IsEnabled = editable;
        SettingsButton.IsEnabled = editable;
        ConnectionActionsButton.IsEnabled = !busy;
        AppSummary.Text = entirePc ? "Send traffic from this computer through the selected route." : apps.Count == 0
            ? "Add a running app or browse for its executable." : $"{checkedApps.Count} of {apps.Count} apps selected";
        if (!entirePc && selectedGame is { } game) AppSummary.Text += $" · Catalog: {game.Name}";
        RouteTitle.Text = route?.Title ?? UiLanguage.Text("No route selected");
        ToolTipService.SetToolTip(RouteTitle, route?.Title);
        RouteSource.Text = UiLanguage.Text(route?.Source ?? "");
        SourceBadge.Visibility = route is null ? Visibility.Collapsed : Visibility.Visible;
        RouteDetail.Text = route?.Detail ?? "Import a config or add an EZConnect account to get started.";
        if (route is { } selected && measurements.TryGetValue(selected.Key, out var result))
            RouteDetail.Text += $"\nLast test · {FormatTest(result)}";
        SetActionLabel(TestRouteButton, activeTest is null ? "Test route" : "Cancel test", activeTest is null ? "\uE9D9" : "\uE711");
        TestAllButton.IsEnabled = editable && Routes().Any();
        TestRouteButton.IsEnabled = activeTest is { IsCancellationRequested: false } || editable && route is not null;
        CompareButton.IsEnabled = editable && Routes().Skip(1).Any();
        DestinationButton.IsEnabled = editable && Routes().Any(choice => choice.Service?.IsDownload != true);
        RouteOptionsButton.IsEnabled = editable && route is not null;
        RouteToolsButton.IsEnabled = CompareButton.IsEnabled || DestinationButton.IsEnabled || RouteOptionsButton.IsEnabled;
        SetActionLabel(ConnectButton, Connected ? "Disconnect" : session.NeedsCleanup ? "Retry cleanup" : "Connect",
            Connected ? "\uE71A" : session.NeedsCleanup ? "\uE72C" : "\uE7E8");
        ConnectButton.IsEnabled = !busy && (Connected || session.NeedsCleanup || route is not null && (entirePc || checkedApps.Count > 0));
        UpdateStatus();
        UpdateTray();
    }

    void UpdateStatus()
    {
        if (busy || exitRequested) return;
        if (preview && simulatedConnection)
        {
            ConnectionTitle.Text = "Connected · preview";
            ConnectionDetail.Text = $"{route?.Title}  ·  42 ms  ·  ↓ 2.4 MB  ↑ 186 KB";
            return;
        }
        if (!session.HasTunnel)
        {
            previousTraffic = null;
            trafficRate = null;
            ConnectionTitle.Text = UiLanguage.Text(session.NeedsCleanup ? "Cleanup needed" : "Disconnected");
            ConnectionDetail.Text = session.NeedsCleanup ? "Retry cleanup to release the allocated session."
                : route is null ? "Add a route to get started."
                : Scope.SelectedIndex == 0 && checkedApps.Count == 0 ? "Select at least one app to connect."
                : armed is { } watch ? $"Watching {watch.Paths.Count} app(s) for launch."
                : UiLanguage.Text("Ready to connect.");
            return;
        }
        try
        {
            if (!session.TunnelIsActive)
            {
                ConnectionTitle.Text = "Tunnel stopped";
                ConnectionDetail.Text = "Disconnect before trying another route.";
                return;
            }
            var stats = session.State;
            bool handshake = session.PeerResponseObserved || stats is { TimeSinceLastHandshakeMilliseconds: >= 0 } || stats is { RxBytes: > 0 };
            bool timedOut = !handshake && DateTimeOffset.UtcNow - connectedAt >= TimeSpan.FromSeconds(Math.Clamp(preferences.PeerResponseSeconds, 5, 180));
            ConnectionTitle.Text = UiLanguage.Text(handshake ? "Connected" : timedOut ? "No peer response" : "Waiting for peer");
            if (stats is { } current)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (previousTraffic is { } last && current.RxBytes >= last.Rx && current.TxBytes >= last.Tx)
                {
                    double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(last.At, now).TotalSeconds;
                    if (elapsed >= 0.5)
                    {
                        trafficRate = $"↓ {FormatBytes((ulong)((current.RxBytes - last.Rx) / elapsed))}/s  ↑ {FormatBytes((ulong)((current.TxBytes - last.Tx) / elapsed))}/s";
                        previousTraffic = (current.RxBytes, current.TxBytes, now);
                    }
                }
                else previousTraffic = (current.RxBytes, current.TxBytes, now);
            }
            ConnectionDetail.Text = stats is { } sample
                ? $"{route?.Title}  ·  ↓ {FormatBytes(sample.RxBytes)}  ↑ {FormatBytes(sample.TxBytes)}" +
                  (handshake && sample.EstimatedRtt > 0 ? $"  ·  {sample.EstimatedRtt} ms" : "") +
                  (trafficRate is null ? "" : $"  ·  {trafficRate}")
                : route?.Title ?? "";
        }
        catch (Exception error)
        {
            timer.Stop();
            Report($"Could not read WireSock status. {error.Message}");
        }
    }

    static string FormatBytes(ulong bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} MB" : $"{bytes / 1024.0:0.0} KB";

    async void ToggleConnection(object sender, RoutedEventArgs args) => await ChangeConnection();

    async Task ChangeConnection(bool automatic = false, bool console = false)
    {
        if (busy || exitRequested || closing) return;
        if (!automatic) CancelWatch();
        if (preview)
        {
            simulatedConnection = !simulatedConnection;
            AddActivity(simulatedConnection ? "Simulated connection started." : "Simulated connection stopped.");
            Refresh();
            return;
        }
        if (session.HasTunnel || session.NeedsCleanup)
        {
            await Run("Disconnecting…", async () =>
            {
                await session.DisconnectAsync();
                AddActivity("Disconnected.");
            });
            return;
        }
        if (route is not { } selected) return;
        if (!EnsureAdministrator()) return;
        bool entirePc = console || Scope.SelectedIndex == 1;
        string[] paths = entirePc ? [] : apps.Where(checkedApps.Contains).ToArray();
        var (gameIps, gameDomains, conflicts) = CatalogRouting(selected.Service, paths);
        if (conflicts.Length > 0)
        {
            Report($"{selectedGame?.Name} is incompatible with these selected apps: {string.Join(", ", conflicts)}.");
            return;
        }
        if (paths.Any(path => !File.Exists(path)))
        {
            Report("Uncheck or remove executables that have moved before connecting.");
            return;
        }
        await Run("Starting tunnel…", async () =>
        {
            var local = selected.Profile is { } profile ? TunnelProfile.Parse(profile.Configuration, profile.Name) : null;
            var bypass = preferences.BypassDestinations.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var domains = bypass.Where(value => Uri.CheckHostName(value) == UriHostNameType.Dns).ToArray();
            connectedEndpoint = await session.ConnectAsync(selected.Service, local, paths, entirePc, preferences.KillSwitch,
                preferences.PortableEngine, preferences.DnsOverride, preferences.AutoMtu,
                bypass.Except(domains).Concat(gameIps).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                domains.Concat(gameDomains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), consoleMode: console,
                bypassLan: preferences.BypassLan,
                keepaliveSeconds: preferences.KeepaliveSeconds, excludedApplications: preferences.ExcludedApplications,
                tunnelMtu: preferences.TunnelMtu, socksProxyOverride: ConfiguredProxy);
            connectedAt = DateTimeOffset.UtcNow;
            connectedForConsole = console;
            if (!automatic) recoveryAttempted = false;
            timer.Start();
            preferences = preferences with { LastConnectedRoute = selected.Key };
            SavePreferences();
            AddActivity($"Started tunnel for {selected.Title}.");
        });
    }

    ProxyOverride? ConfiguredProxy => preferences.SocksProxyCredentials ?? (string.IsNullOrWhiteSpace(preferences.SocksProxyOverride)
        ? null : new ProxyOverride(preferences.SocksProxyOverride, "", ""));

    bool EnsureAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        Report("Reopen RouteFinder with Run as administrator to use the WireSock driver.");
        return false;
    }

    async Task Run(string message, Func<Task> operation)
    {
        if (busy) return;
        busy = true;
        Notice.IsOpen = false;
        ConnectionTitle.Text = message;
        ConnectionDetail.Text = "";
        Refresh();
        try { await operation(); }
        catch (Exception error) { Report(error.Message); }
        finally
        {
            busy = false;
            Refresh();
            if (exitRequested)
                DispatcherQueue.TryEnqueue(async () =>
                {
                    exitRequested = false;
                    await RequestExit();
                });
        }
    }

    async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closing || (!busy && !session.HasTunnel && !session.NeedsCleanup)) return;
        args.Cancel = true;
        await RequestExit();
    }

    async Task RequestExit()
    {
        if (closing) return;
        if (busy)
        {
            exitRequested = true;
            activeTest?.Cancel();
            return;
        }
        if (session.HasTunnel || session.NeedsCleanup)
            await Run("Disconnecting…", () => session.DisconnectAsync());
        if (session.HasTunnel || session.NeedsCleanup) return;
        closing = true;
        Close();
    }

    void Report(string message, InfoBarSeverity severity = InfoBarSeverity.Warning)
    {
        Notice.Severity = severity;
        Notice.Message = message;
        Notice.IsOpen = true;
        AddActivity(message);
    }

    void AddActivity(string message)
    {
        activity.Insert(0, $"{DateTime.Now:t}  ·  {message}");
        if (activity.Count > 100) activity.RemoveAt(activity.Count - 1);
    }

}
