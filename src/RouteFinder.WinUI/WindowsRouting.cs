using System.Net.NetworkInformation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    DirectAppRouting? directRouting;
    bool appRouterVpn;
    bool AppRouterActive => directRouting is not null || appRouterVpn;
    Dictionary<string, string> appVpnDraft = new(StringComparer.OrdinalIgnoreCase);
    bool windowsRoutingOpen;
    bool windowsRoutingSupported;
    Dictionary<string, string>? windowsAppDraft;
    string? windowsFallbackDraft;
    bool loadingWindowsFallback;
    readonly HashSet<string> seenCheckedWindowsApps = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> suggestedWindowsApps = new(StringComparer.OrdinalIgnoreCase);
    WindowsAdapterChoice[] windowsAdapters = [];

    sealed record WindowsAdapterChoice(string Id, string Label, bool Available, bool HasGateway = false)
    {
        public override string ToString() => Label;
    }

    void OpenWindowsRouting(object sender, RoutedEventArgs args)
    {
        if (busy || Connected && !appRouterVpn || session.NeedsCleanup && !AppRouterActive || preview) return;
        if (windowsRoutingOpen)
        {
            windowsRoutingOpen = false;
            Refresh();
            return;
        }

        windowsRoutingOpen = true;
        windowsRoutingSupported = DirectAppRouting.IsSupported();
        if (windowsAppDraft is null)
        {
            windowsAppDraft = new(preferences.DirectAppAdapters ?? [], StringComparer.OrdinalIgnoreCase);
            windowsFallbackDraft = preferences.DirectFallbackAdapter;
            appVpnDraft = new(preferences.AppVpnRoutes ?? [], StringComparer.OrdinalIgnoreCase);
        }
        SyncWindowsAppSuggestions();
        ReloadWindowsAdapters();
        Refresh();
        StartWindowsRoutingButton.Focus(FocusState.Programmatic);
    }

    bool SyncWindowsAppSuggestions()
    {
        if (windowsAppDraft is not { } draft || AppRouterActive) return false;
        var selected = apps.Where(checkedApps.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        seenCheckedWindowsApps.IntersectWith(selected);
        bool changed = false;
        foreach (string path in suggestedWindowsApps.Where(path => !selected.Contains(path)).ToArray())
        {
            suggestedWindowsApps.Remove(path);
            if (draft.GetValueOrDefault(path) == "" && !appVpnDraft.ContainsKey(path)) changed |= draft.Remove(path);
        }
        foreach (string path in selected)
        {
            if (!seenCheckedWindowsApps.Add(path) || !draft.TryAdd(path, "")) continue;
            suggestedWindowsApps.Add(path);
            changed = true;
        }
        return changed;
    }

    void RefreshWindowsAdapters(object sender, RoutedEventArgs args) => ReloadWindowsAdapters();

    void ReloadWindowsAdapters()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces();
            var defaultName = DirectAppRouting.DefaultIpv4AdapterName(adapters);
            WindowsDefaultAdapter.Text = defaultName is null ? "Windows IPv4 default unavailable."
                : $"Windows IPv4 default: {defaultName}.";
            windowsAdapters = adapters
                .Where(adapter => adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
                    !adapter.Description.Contains("WireSock", StringComparison.OrdinalIgnoreCase))
                .Select(adapter =>
                {
                    bool available = DirectAppRouting.HasInternetGateway(adapter);
                    string label = available ? adapter.Name : adapter.OperationalStatus == OperationalStatus.Up
                        ? $"{adapter.Name} (no internet gateway)" : $"{adapter.Name} (disconnected)";
                    return new WindowsAdapterChoice(adapter.Id, label, adapter.OperationalStatus == OperationalStatus.Up, available);
                })
                .OrderByDescending(adapter => adapter.Available).ThenBy(adapter => adapter.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (NetworkInformationException error)
        {
            windowsAdapters = [];
            WindowsDefaultAdapter.Text = "Windows adapter information is unavailable.";
            Report(error.Message);
        }
        var choices = new List<WindowsAdapterChoice> { new("", "Follow Windows", true) };
        choices.AddRange(windowsAdapters.Where(adapter => adapter.HasGateway));
        if (windowsFallbackDraft is { } saved && !choices.Any(adapter => adapter.Id.Equals(saved, StringComparison.OrdinalIgnoreCase)))
            choices.Add(windowsAdapters.FirstOrDefault(adapter => adapter.Id.Equals(saved, StringComparison.OrdinalIgnoreCase))
                ?? new WindowsAdapterChoice(saved, "Saved adapter (unavailable)", false));
        loadingWindowsFallback = true;
        try
        {
            WindowsFallbackAdapter.ItemsSource = choices;
            WindowsFallbackAdapter.SelectedItem = choices.First(adapter =>
                adapter.Id.Equals(windowsFallbackDraft ?? "", StringComparison.OrdinalIgnoreCase));
        }
        finally { loadingWindowsFallback = false; }
        RenderWindowsAppRules();
    }

    void WindowsFallbackChanged(object sender, SelectionChangedEventArgs args)
    {
        if (loadingWindowsFallback || WindowsFallbackAdapter.SelectedItem is not WindowsAdapterChoice choice) return;
        windowsFallbackDraft = choice.Id.Length == 0 ? null : choice.Id;
        UpdateWindowsRoutingActions();
    }

    async void BrowseWindowsApps(object sender, RoutedEventArgs args)
    {
        if (busy || AppRouterActive) return;
        try
        {
            var files = await CreateFilePicker(".exe").PickMultipleFilesAsync();
            if (windowsRoutingOpen && !busy && !AppRouterActive) AddWindowsApps(files.Select(file => file.Path));
        }
        catch (Exception error) { Report(error.Message); }
    }

    void AddWindowsApps(IEnumerable<string> paths)
    {
        if (windowsAppDraft is not { } draft || AppRouterActive) return;
        foreach (string path in paths)
        {
            seenCheckedWindowsApps.Add(path);
            suggestedWindowsApps.Remove(path);
            draft.TryAdd(path, "");
        }
        RenderWindowsAppRules();
    }

    void RenderWindowsAppRules()
    {
        WindowsAppRows.Children.Clear();
        if (windowsAppDraft is not { } draft) return;
        if (draft.Count == 0)
            WindowsAppRows.Children.Add(new TextBlock
            {
                Text = "No app rules yet. Add checked apps from the VPN workspace or browse for executable files.",
                TextWrapping = TextWrapping.Wrap
            });
        foreach (var (path, adapterId) in draft.OrderBy(rule => Path.GetFileName(rule.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            var choices = new List<WindowsAdapterChoice> { new("", "Use default / automatic uplink", true) };
            choices.AddRange(windowsAdapters);
            int selected = choices.FindIndex(adapter => adapter.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase));
            if (selected < 0 && adapterId.Length > 0)
            {
                selected = choices.Count;
                choices.Add(new(adapterId, "Saved adapter unavailable", false));
            }

            string app = Path.GetFileNameWithoutExtension(path);
            var name = new TextBlock { Text = app, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(name, path);
            var picker = new ComboBox
            {
                ItemsSource = choices, SelectedIndex = selected, PlaceholderText = "Choose adapter",
                HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = directRouting is null && !busy
            };
            AutomationProperties.SetName(picker, $"Internet adapter for {app}");
            var routeChoices = Routes().Select(choice => new AppRouteOption(choice.Key, choice.Title)).ToList();
            routeChoices.Insert(0, new("", "Direct / no VPN"));
            string routeKey = appVpnDraft.GetValueOrDefault(path) ?? "";
            if (!routeChoices.Any(choice => choice.Key == routeKey)) routeChoices.Add(new(routeKey, "Saved VPN route unavailable"));
            var vpn = new ComboBox { ItemsSource = routeChoices, SelectedItem = routeChoices.First(choice => choice.Key == routeKey),
                HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(vpn, $"VPN route for {app}");
            vpn.SelectionChanged += (_, _) =>
            {
                if (vpn.SelectedItem is not AppRouteOption choice) return;
                if (choice.Key.Length == 0) appVpnDraft.Remove(path);
                else appVpnDraft[path] = choice.Key;
                UpdateWindowsRoutingActions();
            };
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is not WindowsAdapterChoice adapter) return;
                draft[path] = adapter.Id;
                UpdateWindowsRoutingActions();
            };
            var remove = new Button { Content = "Remove", IsEnabled = directRouting is null && !busy };
            AutomationProperties.SetName(remove, $"Remove {app} rule");
            remove.Click += (_, _) =>
            {
                draft.Remove(path);
                appVpnDraft.Remove(path);
                suggestedWindowsApps.Remove(path);
                RenderWindowsAppRules();
            };
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1.4, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetColumn(picker, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(name);
            row.Children.Add(picker);
            Grid.SetRow(vpn, 2);
            Grid.SetColumnSpan(vpn, 3);
            row.Children.Add(vpn);
            row.Children.Add(remove);
            row.SizeChanged += (_, args) =>
            {
                bool compact = args.NewSize.Width < 430;
                Grid.SetRow(picker, compact ? 1 : 0);
                Grid.SetColumn(picker, compact ? 0 : 1);
                Grid.SetColumnSpan(picker, compact ? 3 : 1);
                row.RowSpacing = compact ? 4 : 0;
            };
            WindowsAppRows.Children.Add(row);
        }
        UpdateWindowsRoutingActions();
    }

    void UpdateWindowsRoutingActions()
    {
        if (!windowsRoutingOpen || windowsAppDraft is not { } draft) return;
        bool active = AppRouterActive;
        bool editable = !active && !busy && !Connected && !session.NeedsCleanup;
        var assigned = draft.Where(rule => rule.Value.Length > 0 || appVpnDraft.ContainsKey(rule.Key)).ToArray();
        bool fallbackReady = windowsFallbackDraft is null || windowsAdapters.Any(adapter =>
            adapter.HasGateway && adapter.Id.Equals(windowsFallbackDraft, StringComparison.OrdinalIgnoreCase));
        bool readyToStart = (assigned.Length > 0 || windowsFallbackDraft is not null) && fallbackReady && assigned.All(rule =>
            File.Exists(rule.Key) && (rule.Value.Length == 0 || windowsAdapters.Any(adapter => adapter.Available &&
                adapter.Id.Equals(rule.Value, StringComparison.OrdinalIgnoreCase))));
        bool clearing = assigned.Length == 0 && windowsFallbackDraft is null &&
            (preferences.DirectAppAdapters is { Count: > 0 } || preferences.DirectFallbackAdapter is not null ||
                preferences.AppVpnRoutes is { Count: > 0 });
        WindowsRoutingStatus.Text = active ? $"Active · {assigned.Length} app rules" +
            (windowsFallbackDraft is null ? "" : " · Default adapter on") : "Stopped";
        StartWindowsRoutingButton.Content = active ? "Stop routing" : clearing ? "Clear saved rules" : "Save & apply";
        StartWindowsRoutingButton.IsEnabled = !busy && (active || editable && (clearing || readyToStart && windowsRoutingSupported));
        BrowseWindowsAppsButton.IsEnabled = editable;
        WindowsFallbackAdapter.IsEnabled = editable;
        RefreshWindowsAdaptersButton.IsEnabled = !busy;
        foreach (var row in WindowsAppRows.Children.OfType<Grid>())
            foreach (var control in row.Children.OfType<Control>()) control.IsEnabled = editable;
        WindowsRoutingHint.Text = active ? "Fully quit and reopen assigned apps to replace their existing connections. Stop routing before editing rules or starting a VPN."
            : !windowsRoutingSupported ? "This Windows build does not expose per-app connection policies."
            : !fallbackReady ? "Reconnect the saved default adapter or choose a connected adapter with a gateway."
            : assigned.Length > 0 && !readyToStart ? "Each assigned app needs an existing executable and a connected adapter."
            : "Explicit app rules take priority. The default adapter applies to your other apps while routing is active.";
    }

    bool StoreWindowsAppRules()
    {
        if (windowsAppDraft is not { } draft) return false;
        try
        {
            var next = preferences with { DirectAppAdapters = draft.Where(rule => rule.Value.Length > 0)
                .Concat(draft.Where(rule => rule.Value.Length == 0 && appVpnDraft.ContainsKey(rule.Key)))
                .ToDictionary(rule => rule.Key, rule => rule.Value, StringComparer.OrdinalIgnoreCase),
                AppVpnRoutes = new(appVpnDraft, StringComparer.OrdinalIgnoreCase),
                DirectFallbackAdapter = windowsFallbackDraft };
            WriteStore("preferences", () => ProfileStore.SavePreferences(next));
            preferences = next;
            return true;
        }
        catch (Exception error) { Report(error.Message); return false; }
    }

    async void ToggleWindowsRouting(object sender, RoutedEventArgs args)
    {
        if (busy || Connected && !appRouterVpn || closing) return;
        if (AppRouterActive)
        {
            await Run("Stopping Windows routing…", async () =>
            {
                await StopAppRouter();
                Report("Windows app routing stopped. Reopen apps to use the Windows default.", InfoBarSeverity.Success);
            });
            return;
        }
        if (session.NeedsCleanup) return;
        if (windowsAppDraft is not { } draft) return;
        var assignments = draft.Where(rule => rule.Value.Length > 0 && !appVpnDraft.ContainsKey(rule.Key))
            .Select(rule => new DirectAppRouting.Rule(rule.Key, rule.Value)).ToArray();
        string? fallback = windowsFallbackDraft;
        var vpnRules = draft.Where(rule => appVpnDraft.ContainsKey(rule.Key)).ToArray();
        RouteChoice? vpnRoute = null;
        string? uplink = null;
        if (vpnRules.Length > 0)
        {
            if (fallback is not null)
            {
                Report("Choose Follow Windows for unassigned apps when using VPN rules in the App Router.");
                return;
            }
            var keys = vpnRules.Select(rule => appVpnDraft[rule.Key]).Distinct().ToArray();
            var adapters = vpnRules.Select(rule => rule.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (keys.Length != 1 || adapters.Length != 1)
            {
                Report("VPN apps in this profile must share one VPN route and one uplink adapter.");
                return;
            }
            vpnRoute = Routes().FirstOrDefault(choice => choice.Key == keys[0]);
            if (vpnRoute is null)
            {
                Report("A saved VPN route is unavailable. Select an available route.");
                return;
            }
            uplink = adapters[0].Length == 0 ? null : adapters[0];
        }
        if (!StoreWindowsAppRules()) return;
        if (assignments.Length == 0 && fallback is null && vpnRules.Length == 0)
        {
            Refresh();
            Report("Saved Windows app rules cleared.", InfoBarSeverity.Success);
            return;
        }
        if (!EnsureAdministrator()) return;
        CancelWatch();
        await Run("Starting Windows routing…", async () =>
        {
            if (vpnRoute is not null)
            {
                var local = vpnRoute.Profile is { } profile ? TunnelProfile.Parse(profile.Configuration, profile.Name) : null;
                connectedEndpoint = await session.ConnectAsync(vpnRoute.Service, local, vpnRules.Select(rule => rule.Key).ToArray(),
                    portable: preferences.PortableEngine, dnsOverride: preferences.DnsOverride, autoMtu: preferences.AutoMtu,
                    bypassLan: preferences.BypassLan, keepaliveSeconds: preferences.KeepaliveSeconds,
                    tunnelMtu: preferences.TunnelMtu, socksProxyOverride: ConfiguredProxy, uplinkAdapterId: uplink);
                connectedAt = DateTimeOffset.UtcNow;
                appRouterVpn = true;
            }
            try
            {
                if (assignments.Length > 0 || fallback is not null)
                    directRouting = await Task.Run(() => DirectAppRouting.Start(assignments, fallback));
            }
            catch
            {
                if (appRouterVpn) await StopAppRouter();
                throw;
            }
            Report("Windows routing is active. Fully quit and reopen affected apps to replace existing connections.", InfoBarSeverity.Success);
        });
    }

    sealed record AppRouteOption(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    async Task StopAppRouter()
    {
        if (directRouting is { } routing)
        {
            await Task.Run(routing.Dispose);
            directRouting = null;
        }
        if (appRouterVpn)
        {
            await session.DisconnectAsync();
            appRouterVpn = false;
        }
    }
}
