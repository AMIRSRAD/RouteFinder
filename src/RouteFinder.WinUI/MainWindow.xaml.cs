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

    readonly ConnectionSession session = new(new EzConnectClient(), ProfileStore.LoadAllocation, ProfileStore.SaveAllocation);
    readonly ObservableCollection<string> activity = [];
    readonly DispatcherTimer timer = new();
    readonly List<SavedProfile> profiles = [];
    readonly List<ServiceRoute> serviceRoutes = [];
    readonly List<string> apps = [];
    readonly HashSet<string> checkedApps = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Task<BitmapImage?>> appIcons = new(StringComparer.OrdinalIgnoreCase);
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
    string? lastStatusError;
    readonly HashSet<string> unavailableStores = new(StringComparer.Ordinal);
    bool exitRequested;
    bool simulatedConnection;
    bool Connected => preview ? simulatedConnection : session.HasTunnel;

    public MainWindow(bool preview, bool verifyStartup = false)
    {
        this.preview = preview;
        verifying = verifyStartup;
        if (!preview) UiLanguage.Load();
        InitializeComponent();
        ApplyLanguage();
        ApplyActionIcons();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WindowHeader);
        ApplyTitleBarTheme();
        Root.ActualThemeChanged += (_, _) => { ApplyTitleBarTheme(); ApplyStatusTone(); RenderApps(); };
        WatchNetworkChanges();
        StartDiagnostics();
        // A crash could leave an adapter metric lowered; put it back before rules are worked out.
        RecoverDefaultAdapterMetric();
        ApplySecuritySoftwareSetting();
        ClearLeftoverDnsPolicy();
        session.BeforeTunnelStops = StopDnsRelay;
        ActivityList.ItemsSource = activity;
        activity.CollectionChanged += (_, _) =>
            EmptyActivity.Visibility = activity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => { closing = true; timer.Stop(); directRouting?.Dispose(); tray?.Dispose(); trayMenu?.Dispose(); applicationIcon?.Dispose(); session.Dispose(); };
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
            if (!closing && WindowCanRenderTelemetry) UpdateStatus();
            await CheckConnectionLifecycle();
            UpdatePolling();
        };
        InitializeTray();
        ApplyTrayTheme();
        WatchSystemTheme();

        try
        {
            if (preview)
            {
                PreviewLabel.Visibility = Visibility.Visible;
                apps.AddRange([@"C:\Apps\Discord.exe", @"C:\Games\Counter-Strike 2\cs2.exe"]);
                profiles.Add(new SavedProfile { Name = "Amsterdam · Home relay" });
                serviceRoutes.AddRange([
                    new("1", "DE", "Frankfurt", true), new("2", "TR", "Istanbul", false),
                    new("3", "NL", "Rotterdam", true), new("4", "AE", "Dubai", false)
                ]);
            }
            else
            {
                LoadStore("preferences", () => preferences = ProfileStore.LoadPreferences());
                LoadStore("profiles", () => profiles.AddRange(ProfileStore.Load()));
                LoadStore("test results", () =>
                {
                    foreach (var measurement in ProfileStore.LoadMeasurements()) measurements.Add(measurement.Key, measurement.Value);
                });
                LoadStore("applications", () => apps.AddRange(ProfileStore.LoadApplications() ?? []));
                LoadStore("routing presets", () => presets.AddRange(ProfileStore.LoadPresets()));
                MigrateRoutingProfiles();
                if (unavailableStores.Count > 0)
                    Report($"Could not load saved {string.Join(", ", unavailableStores)}. RouteFinder will leave those files untouched. Restore a readable copy and restart.");
                Scope.SelectedIndex = ScopeIndex(preferences.EntirePcMode, preferences.BlockOtherApps);
            }
            checkedApps.UnionWith(apps.Except(preferences.UncheckedApplications ?? [], StringComparer.OrdinalIgnoreCase));
            route = Routes().FirstOrDefault(item => item.Key == preferences.LastConnectedRoute) ?? Routes().FirstOrDefault();
            if (Scope.SelectedIndex != 1 && route?.Profile is { Applications.Count: > 0 } savedProfile)
                ApplyProfileAppSelection(savedProfile.Applications);
        }
        catch (Exception error) { Report(error.Message); }
        InitializeRouteBrowser();
        BuildSettingsPage();
        LoadGroups();
        InitializeAdapterRules();
        ReloadPresetPicker();
        ready = true;
        ApplyWindowSize(center: true);
        if (!preview)
        {
            if (preferences.ShowTutorials) ShowRoutingTip();
        }
        RenderApps();
        Refresh();
    }

    IEnumerable<RouteChoice> Routes() =>
        profiles.Select(profile => new RouteChoice("local:" + profile.Name, profile.Name, "Imported config", "Local WireGuard or Amnezia configuration", Profile: profile))
        .Concat(serviceRoutes.Select(service => new RouteChoice(
            $"{service.RegionCode}:{service.Name}" + (service.OverIpv6 ? ":ipv6" : ""),
            string.IsNullOrWhiteSpace(service.CityName) ? $"Route {service.Name}" : service.CityName,
            "EZConnect", $"{service.RegionCode} · Route {service.Name}" + (service.OverIpv6 ? " · IPv6" : ""), Service: service)));

    void ScopeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ready) return;
        preferences = preferences with { EntirePcMode = Scope.SelectedIndex == 1, BlockOtherApps = BlocksOtherApps };
        SavePreferences();
        Refresh();
    }

    bool BlocksOtherApps => Scope.SelectedIndex == 2;
    static int ScopeIndex(bool entirePc, bool blockOtherApps) => entirePc ? 1 : blockOtherApps ? 2 : 0;
    bool UseNetworkLock(bool console) => preferences.KillSwitch || !console && BlocksOtherApps;

    void LoadStore(string name, Action read)
    {
        try { read(); }
        catch (Exception error)
        {
            unavailableStores.Add(name);
            AddActivity($"Could not read {name}: {error.Message}");
        }
    }

    void WriteStore(string name, Action write)
    {
        if (unavailableStores.Contains(name))
            throw new InvalidOperationException($"Saved {name} could not be loaded. Restore a readable copy and restart before editing.");
        write();
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer? appSelectionSave;

    void SavePreferences()
    {
        appSelectionSave?.Stop();
        if (preview) return;
        try { WriteStore("preferences", () => ProfileStore.SavePreferences(preferences)); }
        catch (Exception error) { Report($"Could not save preferences. {error.Message}"); }
    }

    void RenderApps()
    {
        AppRows.Children.Clear();
        foreach (var group in groups) AppRows.Children.Add(BuildGroupRow(group));
        var grouped = GroupedPaths();
        foreach (string path in apps.Where(path => !grouped.Contains(path)))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            // Tick box: switches the app on or off without removing it from the list.
            var check = new CheckBox
            {
                IsChecked = checkedApps.Contains(path), MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, -8, 0)
            };
            AutomationProperties.SetName(check, name);
            ToolTipService.SetToolTip(check, UiLanguage.Text("Use this app"));
            // Icon tile: the app's own icon when Windows provides one, otherwise its first letter.
            var icon = new Grid { Style = (Style)Root.Resources["IconTileGrid"] };
            icon.Children.Add(new TextBlock
            {
                Text = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            });
            if (!preview) _ = ShowAppIcon(path, icon);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            text.Children.Add(new TextBlock
            {
                Text = name, TextTrimming = TextTrimming.CharacterEllipsis,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
            });
            text.Children.Add(new TextBlock
            {
                Text = Path.GetDirectoryName(path) ?? path, TextTrimming = TextTrimming.CharacterEllipsis,
                Style = (Style)Root.Resources["Caption"]
            });
            ToolTipService.SetToolTip(text, path);

            // One menu decides how this app connects when it is switched on.
            var modes = RouteModes(path);
            var choice = new ComboBox
            {
                ItemsSource = modes, SelectedItem = CurrentMode(path, modes), MinWidth = 200, MaxWidth = 280,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(choice, $"{UiLanguage.Text("How")} {name} {UiLanguage.Text("connects")}");
            bool loading = true;
            choice.SelectionChanged += (_, _) =>
            {
                if (!loading && choice.SelectedItem is AppRouteMode mode) ChooseAppRoute(path, mode);
            };
            loading = false;

            var remove = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(10, 8, 10, 8),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0)
            };
            AutomationProperties.SetName(remove, $"Remove {name}");
            ToolTipService.SetToolTip(remove, UiLanguage.Text("Remove from the list"));
            remove.Click += (_, _) => RemoveApplication(path);

            var row = new Grid { Style = (Style)Root.Resources["AppRow"], Padding = new Thickness(14, 10, 8, 10), ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(icon, 1);
            Grid.SetColumn(text, 2);
            Grid.SetColumn(choice, 3);
            Grid.SetColumn(remove, 4);
            row.Children.Add(check);
            row.Children.Add(icon);
            row.Children.Add(text);
            row.Children.Add(choice);
            row.Children.Add(remove);

            // Switched-off apps stay in the list but fade back and do nothing.
            void Dim() => text.Opacity = icon.Opacity = choice.Opacity = check.IsChecked == true ? 1 : 0.5;
            Dim();
            check.Checked += (_, _) => ToggleApp(path, true, Dim);
            check.Unchecked += (_, _) => ToggleApp(path, false, Dim);
            AppRows.Children.Add(row);
        }
    }

    void ToggleApp(string path, bool on, Action dim)
    {
        if (on) checkedApps.Add(path);
        else checkedApps.Remove(path);
        dim();
        SaveAppSelection(defer: true);
        Refresh(refreshRouteBrowser: false);
    }

    async Task ShowAppIcon(string path, Grid host)
    {
        if (!appIcons.TryGetValue(path, out var loading))
        {
            loading = LoadAppIcon(path);
            appIcons.Add(path, loading);
        }
        if (await loading is not { } bitmap || closing) return;
        host.Children.Clear();
        host.Children.Add(new Image { Source = bitmap });
    }

    static async Task<BitmapImage?> LoadAppIcon(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32, ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null) return null;
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            return bitmap;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    void RemoveApplication(string path, bool rebuild = true)
    {
        var updated = apps.ToList();
        updated.Remove(path);
        try
        {
            if (!preview) WriteStore("applications", () => ProfileStore.SaveApplications(updated));
            apps.Clear();
            apps.AddRange(updated);
            checkedApps.Remove(path);
            appIcons.Remove(path);
            windowsAppDraft?.Remove(path);
            appVpnDraft.Remove(path);
            suggestedWindowsApps.Remove(path);
            seenCheckedWindowsApps.Remove(path);
            foreach (var group in groups) group.Members.Remove(path);
            SaveAppSelection();
            if (!rebuild) return;
            RenderApps();
            Refresh();
        }
        catch (Exception error) { Report(error.Message); }
    }

    void SaveAppSelection(bool defer = false)
    {
        preferences = preferences with { UncheckedApplications = apps.Where(path => !checkedApps.Contains(path)).ToArray() };
        if (!defer) { SavePreferences(); return; }
        if (preview) return;
        if (appSelectionSave is null)
        {
            appSelectionSave = DispatcherQueue.CreateTimer();
            appSelectionSave.Interval = TimeSpan.FromMilliseconds(200);
            appSelectionSave.IsRepeating = false;
            appSelectionSave.Tick += (_, _) => SavePreferences();
        }
        appSelectionSave.Stop();
        appSelectionSave.Start();
    }

    void Refresh(bool refreshRouteBrowser = true)
    {
        // Most settings stay editable while connected; the tunnel reconnects to pick them up.
        bool editable = !busy && !session.NeedsCleanup;
        bool idle = editable && !Connected;
        bool vpnEditable = idle;
        SyncWindowsAppSuggestions();
        UpdateWindowsRoutingActions();
        QueueAdapterSync();
        bool entirePc = Scope.SelectedIndex == 1;
        if (armed is { } watch && (entirePc || watch.RouteKey != route?.Key || !watch.Paths.SetEquals(VpnApps())))
            CancelWatch();
        Scope.IsEnabled = editable;
        RenderAppsIfRouteChanged();
        bool rulesEditable = editable;
        foreach (var row in AppRows.Children.OfType<Grid>())
            foreach (var control in row.Children.OfType<Control>()) control.IsEnabled = rulesEditable && !preview;
        SetGroupRowsEnabled(rulesEditable && !preview);
        int listed = apps.Count + groups.Sum(group => group.Pending.Count);
        AppScroller.Visibility = listed > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyApps.Visibility = listed == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePresetSection();
        AddAppsButton.IsEnabled = editable && !preview;
        AddRouteButton.IsEnabled = idle;
        ImportRouteCommand.IsEnabled = !preview;
        AccountCommand.Text = UiLanguage.Text(session.SignedIn ? "Manage EZConnect account…" : "Sign in to EZConnect…");
        CatalogCommand.Visibility = accountCatalog is { Games.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        SettingsGate.IsEnabled = SettingsSaveButton.IsEnabled = SettingsResetButton.IsEnabled = editable;
        DiagnosticsButton.IsEnabled = !busy;
        SaveReportButton.IsEnabled = !preview;
        ConnectionActionsButton.IsEnabled = !busy;
        int onCount = apps.Count(checkedApps.Contains);
        int vpnCount = VpnTargets().Length;
        AppSummary.Text = UiLanguage.Text(entirePc ? "Entire PC mode is on, so every app uses the VPN route. Direct choices still apply."
            : listed == 0 ? "" : $"{onCount} of {listed} apps on · {vpnCount} through VPN · {onCount - vpnCount} direct");
        if (!entirePc && selectedGame is { } game) AppSummary.Text += $" · Catalog: {game.Name}";
        HomeAppsSummary.Text = entirePc ? UiLanguage.Text("All apps on this PC")
            : apps.Count == 0 ? UiLanguage.Text("No apps added")
            : onCount == 0 ? UiLanguage.Text("No apps switched on")
            : string.Join(", ", apps.Where(checkedApps.Contains).Take(3).Select(Path.GetFileNameWithoutExtension)) +
              (onCount > 3 ? $" +{onCount - 3}" : "");
        HomeRouteTitle.Text = route?.Title ?? UiLanguage.Text("No route selected");
        if (refreshRouteBrowser) RefreshRouteBrowser();
        RefreshConnectionActions();
        SetActionLabel(TestRouteButton, activeTest is null ? "Test route" : "Cancel test", activeTest is null ? "\uE9D9" : "\uE711");
        TestAllButton.IsEnabled = vpnEditable && Routes().Any();
        TestRouteButton.IsEnabled = activeTest is { IsCancellationRequested: false } || vpnEditable && route is not null;
        CompareButton.IsEnabled = vpnEditable && Routes().Skip(1).Any();
        DestinationButton.IsEnabled = vpnEditable && Routes().Any(choice => choice.Service?.IsDownload != true);
        RouteOptionsButton.IsEnabled = idle && route is not null;
        TestShortlistButton.IsEnabled = vpnEditable && RouteList.Items.Count > 0;
        ClearTestsButton.IsEnabled = !preview && editable && measurements.Count > 0;
        Ipv6Button.IsEnabled = !preview && idle;
        RouteToolsButton.IsEnabled = CompareButton.IsEnabled || DestinationButton.IsEnabled || RouteOptionsButton.IsEnabled ||
            TestShortlistButton.IsEnabled || ClearTestsButton.IsEnabled;
        SetActionLabel(ConnectButton, Connected ? "Disconnect" : session.NeedsCleanup ? "Retry cleanup" : "Connect",
            Connected ? "\uE71A" : session.NeedsCleanup ? "\uE72C" : "\uE7E8");
        ConnectButton.IsEnabled = !busy &&
            (Connected || session.NeedsCleanup || route is not null && (entirePc || vpnCount > 0));
        CheckLiveChanges();
        RefreshProviders();
        RefreshVpnBar();
        UpdateDnsButtons();
        if (WindowCanRenderTelemetry) UpdateStatus();
        UpdatePolling();
        UpdateTray();
    }

    void UpdateStatus()
    {
        UpdateStatusText();
        ApplyStatusTone();
    }

    void UpdateStatusText()
    {
        if (busy || exitRequested) return;
        if (preview && simulatedConnection)
        {
            SetHealthAlert("");
            RenderTelemetry(42, 0.3f, 2457600, 190464, "↓ 84 KB/s  ↑ 3 KB/s", "Handshake confirmed · Preview");
            ConnectionTitle.Text = "Connected · preview";
            ConnectionDetail.Text = $"{route?.Title}  ·  42 ms  ·  ↓ 2.4 MB  ↑ 186 KB";
            return;
        }
        if (!session.HasTunnel)
        {
            ResetTrafficHealth();
            SetHealthAlert(session.NeedsCleanup ? "The EZConnect session still needs cleanup."
                : reconnectFailed ? "Reconnect failed. Open Activity for details." : "");
            RenderTelemetry(null, null, 0, 0, null, "");
            previousTraffic = null;
            trafficRate = null;
            ConnectionTitle.Text = UiLanguage.Text(session.NeedsCleanup ? "Cleanup needed" : "Disconnected");
            ConnectionDetail.Text = session.NeedsCleanup ? "Retry cleanup."
                : route is null ? UiLanguage.Text("Add a route to get started.")
                : Scope.SelectedIndex != 1 && VpnTargets().Length == 0 ? "No app set to Through VPN."
                : armed is { } watch ? $"Watching {watch.Paths.Count} app(s) for launch."
                : UiLanguage.Text("Ready to connect.");
            return;
        }
        try
        {
            if (!session.TunnelIsActive)
            {
                // Lookups would otherwise keep going to a relay whose tunnel is gone.
                if (dnsPolicyActive) _ = StopDnsRelay();
                ResetTrafficHealth();
                string stopped = reconnectFailed ? "Reconnect failed and the tunnel is still stopped." : "WireSock reported that the tunnel stopped.";
                if (session.ActiveTunnelError is uint errorCode) stopped += $" Error 0x{errorCode:X8}.";
                SetHealthAlert(stopped);
                RenderTelemetry(null, null, 0, 0, null, "Tunnel stopped");
                ConnectionTitle.Text = "Tunnel stopped";
                ConnectionDetail.Text = "Disconnect before trying another route.";
                return;
            }
            var stats = session.State;
            lastStatusError = null;
            bool handshake = session.PeerResponseObserved || stats is { TimeSinceLastHandshakeMilliseconds: >= 0 } || stats is { RxBytes: > 0 };
            bool timedOut = !handshake && DateTimeOffset.UtcNow - connectedAt >= TimeSpan.FromSeconds(Math.Clamp(preferences.PeerResponseSeconds, 5, 180));
            if (timedOut) SetHealthAlert("No peer response within the configured timeout.");
            else if (stats is { } health) UpdateTrafficHealth(health.RxBytes, health.TxBytes, handshake);
            else SetHealthAlert("");
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
            if (stats is { } metrics)
                RenderTelemetry(handshake && metrics.EstimatedRtt > 0 ? metrics.EstimatedRtt : null,
                    handshake && metrics.RxBytes > 0 && float.IsFinite(metrics.EstimatedLoss) && metrics.EstimatedLoss is >= 0 and <= 100
                        ? metrics.EstimatedLoss : null, metrics.RxBytes, metrics.TxBytes, trafficRate,
                    handshake && !connectedForConsole && Scope.SelectedIndex != 1 && metrics.RxBytes == 0
                        ? "Waiting for traffic · reopen running apps"
                        : handshake ? $"Uptime {DateTimeOffset.UtcNow - connectedAt:hh\\:mm\\:ss} · {connectedEndpoint}" : "Waiting for handshake");
            else RenderTelemetry(null, null, 0, 0, null, "Tunnel statistics unavailable");
            ConnectionDetail.Text = stats is { } sample
                ? $"{route?.Title}  ·  ↓ {FormatBytes(sample.RxBytes)}  ↑ {FormatBytes(sample.TxBytes)}" +
                  (handshake && sample.EstimatedRtt > 0 ? $"  ·  {sample.EstimatedRtt} ms" : "") +
                  (trafficRate is null ? "" : $"  ·  {trafficRate}")
                : route?.Title ?? "";
        }
        catch (Exception error)
        {
            ResetTrafficHealth();
            SetHealthAlert("Tunnel statistics are unavailable. RouteFinder will retry.");
            RenderTelemetry(null, null, 0, 0, null, "Status unavailable");
            ConnectionTitle.Text = "Status unavailable";
            ConnectionDetail.Text = "Retrying WireSock status.";
            if (lastStatusError != error.Message)
            {
                lastStatusError = error.Message;
                AddActivity($"Could not read WireSock status. {error.Message}");
            }
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
        DiagnosticLog.Write("CONNECT", $"Connect button · automatic {automatic} · console {console} · tunnel {session.HasTunnel} · cleanup {session.NeedsCleanup}");
        if (session.HasTunnel || session.NeedsCleanup)
        {
            if (!automatic) reconnectFailed = false;
            await Run("Disconnecting…", async () =>
            {
                await session.DisconnectAsync();
                FlushDnsCache("disconnected");
                AddActivity("Disconnected.");
            });
            return;
        }
        if (route is not { } selected) return;
        if (!EnsureAdministrator()) return;
        // Quick look at running processes only; install-folder scans never delay connecting. Run keeps Connect from being pressed twice.
        if (groups.Any(group => group.Pending.Count > 0)) await Run("Preparing…", () => ResolvePendingApps(disk: false));
        bool entirePc = console || Scope.SelectedIndex == 1;
        string[] paths = entirePc ? [] : VpnTargets();
        // Windows' lookups go through a relay that the tunnel carries as one more app.
        string? relay = UsesDnsRelay(console) ? PrepareDnsRelay() : null;
        if (relay is not null) existsCache.Remove(relay);
        if (!entirePc && paths.Length == 0)
        {
            Report("Select at least one app before connecting.");
            return;
        }
        var (gameIps, gameDomains, conflicts) = CatalogRouting(selected.Service, paths);
        if (conflicts.Length > 0)
        {
            Report($"{selectedGame?.Name} is incompatible with these selected apps: {string.Join(", ", conflicts)}.");
            return;
        }
        // Bare names are matched by WireSock at run time; only real paths must exist.
        var missing = paths.Where(path => !RouteFinder.Core.WireSockTunnel.IsExecutableName(path) && !File.Exists(path)).ToArray();
        if (missing.Length > 0)
        {
            Report($"Could not find {string.Join(", ", missing.Select(Path.GetFileName))}. Uncheck the missing entries and add the current executables.");
            return;
        }
        connectingTunnel = true;
        try
        {
        await Run("Starting tunnel…", async () =>
        {
            // Make sure the tunnel and the VPN apps are pinned above any default adapter before the tunnel starts.
            await SyncAdapterRules();
            string? planFallback = Scope.SelectedIndex == 0 ? activeFallback : null;
            string? uplinkChoice = !console && !RouteNeedsAutomaticUplink(selected) && TunnelNeedsPinning(planFallback)
                ? PinnedTunnelAdapter(planFallback) : RouteNeedsAutomaticUplink(selected) ? UplinkFor(selected) : EffectiveUplink(selected);
            FlushDnsCache("connecting");
            lastSetupDump = "";
            LogSetupIfChanged("Connecting");
            DiagnosticLog.Write("CONNECT", $"Connecting {selected.Title} · entire PC {entirePc} · network lock {UseNetworkLock(console)} · targets {paths.Length}" +
                $"\nuplink decision: chosen {AdapterName(UplinkFor(selected))} · effective {AdapterName(EffectiveUplink(selected))} · active default {AdapterName(activeFallback)}" +
                $" · pinning needed {TunnelNeedsPinning(activeFallback)} · SOCKS5 {RouteNeedsAutomaticUplink(selected)} -> using {AdapterName(uplinkChoice)}" +
                $"\ncatalog bypass IPs {gameIps.Length} · domains {gameDomains.Length}");
            var local = selected.Profile is { } profile ? TunnelProfile.Parse(profile.Configuration, profile.Name) : null;
            var bypass = preferences.BypassDestinations.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var domains = bypass.Where(value => Uri.CheckHostName(value) == UriHostNameType.Dns).ToArray();
            connectedEndpoint = await session.ConnectAsync(selected.Service, local, paths, entirePc, UseNetworkLock(console),
                preferences.PortableEngine, preferences.DnsOverride, preferences.AutoMtu,
                bypass.Except(domains).Concat(gameIps).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                domains.Concat(gameDomains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), consoleMode: console,
                bypassLan: preferences.BypassLan,
                keepaliveSeconds: preferences.KeepaliveSeconds, excludedApplications: preferences.ExcludedApplications,
                tunnelMtu: preferences.TunnelMtu, socksProxyOverride: ConfiguredProxy, blockOtherApps: !console && BlocksOtherApps,
                uplinkAdapterId: uplinkChoice);
            DiagnosticLog.Write("CONNECT", $"Connected · endpoint {connectedEndpoint} · uplink in use {session.UplinkName ?? "Windows default"} · tunnel DNS {session.ActiveDns}");
            if (relay is not null) await StartDnsRelay(session.ActiveDns);
            connectedAt = DateTimeOffset.UtcNow;
            reconnectFailed = false;
            if (!console) RememberConnectedSetup();
            ResetTrafficHealth();
            connectedForConsole = console;
            if (!automatic) recoveryAttempted = false;
            preferences = preferences with { LastConnectedRoute = selected.Key };
            SavePreferences();
            if (!entirePc && selected.Profile is { } saved && !(saved.Applications ?? []).SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
            {
                try { ReplaceProfile(saved, saved.Configuration, paths.ToList()); }
                catch (Exception error) { Report($"Connected, but could not save apps for this route. {error.Message}"); }
            }
            AddActivity($"Started tunnel for {selected.Title}.");
        });
        }
        finally
        {
            connectingTunnel = false;
            QueueAdapterSync();
        }
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
        UpdateBusyStrip();
        Refresh();
        DiagnosticLog.Write("ACTION", $"Start: {message}");
        try { await operation(); }
        catch (Exception error)
        {
            DiagnosticLog.Error("ACTION", $"Failed: {message}", error);
            Report(error.Message);
        }
        finally
        {
            busy = false;
            UpdateBusyStrip();
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
        if (closing) return;
        // Closing the window sends the app to the tray; Exit in the tray menu is what quits.
        if (preferences.CloseToTray && !preview && !verifying)
        {
            args.Cancel = true;
            HideToTray();
            return;
        }
        if (!busy && !session.HasTunnel && !session.NeedsCleanup) return;
        args.Cancel = true;
        await RequestExit();
    }

    async Task RequestExit()
    {
        if (closing) return;
        if (busy)
        {
            exitRequested = true;
            CancelActiveTest();
            return;
        }
        if (session.HasTunnel || session.NeedsCleanup)
        {
            bool disconnected = false;
            await Run("Disconnecting…", async () =>
            {
                await session.DisconnectAsync();
                disconnected = true;
            });
            if (!disconnected) return;
        }
        if (session.HasTunnel || session.NeedsCleanup) return;
        if (appSelectionSave?.IsRunning == true) SavePreferences();
        await Task.Run(RestoreDefaultAdapterMetricOnExit);
        await Task.Run(ResumeFSecure);
        if (directRouting is { } routing)
        {
            try { routing.Dispose(); }
            catch (Exception error) { Report(error.Message); return; }
            directRouting = null;
        }
        closing = true;
        Close();
    }

    void Report(string message, InfoBarSeverity severity = InfoBarSeverity.Warning)
    {
        Notice.ActionButton = null;
        Notice.Severity = severity;
        Notice.Message = message;
        Notice.IsOpen = true;
        AddActivity(message);
    }

    void AddActivity(string message)
    {
        DiagnosticLog.Write("UI", message);
        activity.Insert(0, $"{DateTime.Now:t}  ·  {message}");
        if (activity.Count > 100) activity.RemoveAt(activity.Count - 1);
    }

}
