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

    enum AppRouteKind { Vpn, Adapter }

    /// <summary>One entry in an app's "how does this app connect" menu.</summary>
    sealed record AppRouteMode(AppRouteKind Kind, string AdapterId, string Label, bool Available = true)
    {
        public override string ToString() => Label;
    }

    // Loads the saved adapter rules once at startup. They are edited inline on the Apps page.
    void InitializeAdapterRules()
    {
        windowsRoutingSupported = DirectAppRouting.IsSupported();
        windowsAppDraft = new(preferences.DirectAppAdapters ?? [], StringComparer.OrdinalIgnoreCase);
        windowsFallbackDraft = preferences.DirectFallbackAdapter;
        appVpnDraft = new(preferences.AppVpnRoutes ?? [], StringComparer.OrdinalIgnoreCase);
        // Apps that only had an adapter rule (from the old Windows routing list) join the main list, switched on.
        foreach (string path in windowsAppDraft.Keys.Where(path => !apps.Contains(path, StringComparer.OrdinalIgnoreCase)).ToArray())
        {
            apps.Add(path);
            checkedApps.Add(path);
        }
        SyncWindowsAppSuggestions();
        ReloadWindowsAdapters();
    }

    /// <summary>True when the app is pinned to a network adapter instead of going through the VPN.</summary>
    bool IsAdapterApp(string path) => windowsAppDraft is { } draft && draft.GetValueOrDefault(path) is { Length: > 0 } &&
        !appVpnDraft.ContainsKey(path);

    /// <summary>Apps that are switched on and go through the VPN route.</summary>
    IEnumerable<string> VpnApps() => apps.Where(path => checkedApps.Contains(path) && !IsAdapterApp(path));

    AppRouteMode CurrentMode(string path, IReadOnlyList<AppRouteMode> modes)
    {
        string adapterId = windowsAppDraft?.GetValueOrDefault(path) ?? "";
        if (adapterId.Length > 0 && !appVpnDraft.ContainsKey(path))
            return modes.FirstOrDefault(mode => mode.Kind == AppRouteKind.Adapter &&
                mode.AdapterId.Equals(adapterId, StringComparison.OrdinalIgnoreCase)) ?? modes[^1];
        return modes[0];
    }

    List<AppRouteMode> RouteModes(string path)
    {
        var modes = new List<AppRouteMode> { new(AppRouteKind.Vpn, "", "Through VPN") };
        modes.AddRange(windowsAdapters.Select(adapter => new AppRouteMode(AppRouteKind.Adapter, adapter.Id,
            "Direct · " + adapter.Label, adapter.Available)));
        string saved = windowsAppDraft?.GetValueOrDefault(path) ?? "";
        if (saved.Length > 0 && !modes.Any(mode => mode.AdapterId.Equals(saved, StringComparison.OrdinalIgnoreCase)))
            modes.Add(new(AppRouteKind.Adapter, saved, "Saved adapter unavailable", false));
        return modes;
    }

    void ChooseAppRoute(string path, AppRouteMode mode)
    {
        if (windowsAppDraft is not { } draft || busy || AppRouterActive) return;
        // The menu only decides how the app connects. Whether it is used at all is the tick box beside it.
        suggestedWindowsApps.Remove(path);
        if (mode.Kind == AppRouteKind.Vpn) draft[path] = "";
        else
        {
            appVpnDraft.Remove(path);
            draft[path] = mode.AdapterId;
        }
        StoreRouteChoices();
        Refresh(refreshRouteBrowser: false);
    }

    // Remembers how every listed app connects, so nothing has to be set up again next time.
    void StoreRouteChoices()
    {
        if (preview || windowsAppDraft is not { } draft || unavailableStores.Contains("preferences")) return;
        try
        {
            var next = preferences with
            {
                DirectAppAdapters = draft.Where(rule => rule.Value.Length > 0 || appVpnDraft.ContainsKey(rule.Key))
                    .ToDictionary(rule => rule.Key, rule => rule.Value, StringComparer.OrdinalIgnoreCase),
                AppVpnRoutes = new(appVpnDraft, StringComparer.OrdinalIgnoreCase)
            };
            ProfileStore.SavePreferences(next);
            preferences = next;
        }
        catch (Exception error) { Report($"Could not save how this app connects. {error.Message}"); }
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
        RenderApps();
        UpdateWindowsRoutingActions();
    }

    void WindowsFallbackChanged(object sender, SelectionChangedEventArgs args)
    {
        if (loadingWindowsFallback || WindowsFallbackAdapter.SelectedItem is not WindowsAdapterChoice choice) return;
        windowsFallbackDraft = choice.Id.Length == 0 ? null : choice.Id;
        UpdateWindowsRoutingActions();
    }

    void UpdateWindowsRoutingActions()
    {
        if (windowsAppDraft is not { } draft) return;
        bool active = AppRouterActive;
        bool editable = !active && !busy && !Connected && !session.NeedsCleanup;
        // Only apps that are switched on take part.
        var assigned = draft.Where(rule => checkedApps.Contains(rule.Key) && (rule.Value.Length > 0 || appVpnDraft.ContainsKey(rule.Key))).ToArray();
        bool fallbackReady = windowsFallbackDraft is null || windowsAdapters.Any(adapter =>
            adapter.HasGateway && adapter.Id.Equals(windowsFallbackDraft, StringComparison.OrdinalIgnoreCase));
        bool readyToStart = (assigned.Length > 0 || windowsFallbackDraft is not null) && fallbackReady && assigned.All(rule =>
            File.Exists(rule.Key) && (rule.Value.Length == 0 || windowsAdapters.Any(adapter => adapter.Available &&
                adapter.Id.Equals(rule.Value, StringComparison.OrdinalIgnoreCase))));
        bool clearing = assigned.Length == 0 && windowsFallbackDraft is null &&
            (preferences.DirectAppAdapters is { Count: > 0 } || preferences.DirectFallbackAdapter is not null ||
                preferences.AppVpnRoutes is { Count: > 0 });
        int pinned = assigned.Count(rule => rule.Value.Length > 0);
        WindowsRoutingStatus.Text = UiLanguage.Text(active
            ? $"Adapter routing is on · {pinned} app{(pinned == 1 ? "" : "s")}" + (windowsFallbackDraft is null ? "" : " · default adapter on")
            : pinned > 0 ? $"{pinned} app{(pinned == 1 ? "" : "s")} pinned to an adapter"
            : windowsFallbackDraft is not null ? "Default adapter set for other apps" : "Saved adapter rules");
        StartWindowsRoutingButton.Content = UiLanguage.Text(active ? "Stop routing" : clearing ? "Clear saved rules" : "Save & apply");
        StartWindowsRoutingButton.IsEnabled = !busy && (active || editable && (clearing || readyToStart && windowsRoutingSupported));
        AdapterRulesBar.Visibility = active || pinned > 0 || windowsFallbackDraft is not null || clearing
            ? Visibility.Visible : Visibility.Collapsed;
        WindowsFallbackAdapter.IsEnabled = editable;
        RefreshWindowsAdaptersButton.IsEnabled = !busy;
        WindowsRoutingHint.Text = UiLanguage.Text(active ? "Fully quit and reopen pinned apps to replace their existing connections. Stop routing before editing rules or starting a VPN."
            : !windowsRoutingSupported ? "This Windows build does not expose per-app connection policies."
            : !fallbackReady ? "Reconnect the saved default adapter or choose a connected adapter with a gateway."
            : assigned.Length > 0 && !readyToStart ? "Each pinned app needs an existing executable and a connected adapter."
            : "Pinned apps use their adapter directly. Apply this, then reopen the apps.");
    }

    bool StoreWindowsAppRules()
    {
        if (windowsAppDraft is not { } draft) return false;
        if (preview) return true;
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
        var assignments = draft.Where(rule => checkedApps.Contains(rule.Key) && rule.Value.Length > 0 && !appVpnDraft.ContainsKey(rule.Key))
            .Select(rule => new DirectAppRouting.Rule(rule.Key, rule.Value)).ToArray();
        string? fallback = windowsFallbackDraft;
        var vpnRules = draft.Where(rule => checkedApps.Contains(rule.Key) && appVpnDraft.ContainsKey(rule.Key)).ToArray();
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
