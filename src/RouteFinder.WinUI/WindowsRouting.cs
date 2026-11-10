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

    // The default adapter is a catch-all Windows rule with no app filter. It applies to every connection this user
    // makes, including RouteFinder's own tunnel traffic and the apps sent through the VPN, so the two cannot run together.
    string? activeFallback;
    DirectAppRouting.Rule[] activeAssignments = [];
    DirectAppRouting.Rule[] activePins = [];

    // In the default transparent mode RouteFinder pins its own tunnel traffic and the Through VPN apps to the
    // tunnel's adapter with app rules, which outrank the catch-all. The virtual adapter mode cannot be pinned that way.

    /// <summary>The adapter the tunnel leaves on while a default adapter is set: the route's choice, else the default adapter.</summary>
    string? PinnedTunnelAdapter(string? fallback) => route is { } current && UplinkFor(current) is { } chosen ? chosen : fallback;

    // App rules that keep RouteFinder (which sends the encrypted tunnel traffic) and the Through VPN apps on the
    // tunnel's adapter, so the catch-all default adapter cannot pull them away.
    DirectAppRouting.Rule[] VpnPins(string? fallback, IEnumerable<string>? vpnPaths = null)
    {
        if (fallback is null || preferences.PortableEngine || PinnedTunnelAdapter(fallback) is not { } adapter) return [];
        var paths = new List<string>();
        if (Environment.ProcessPath is { } self) paths.Add(self);
        paths.AddRange((vpnPaths ?? VpnApps()).Where(ExistsCached));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => new DirectAppRouting.Rule(path, adapter)).ToArray();
    }

    static bool SameRules(DirectAppRouting.Rule[] left, DirectAppRouting.Rule[] right) =>
        left.Length == right.Length && left.All(rule => right.Any(other =>
            other.Executable.Equals(rule.Executable, StringComparison.OrdinalIgnoreCase) &&
            other.AdapterId.Equals(rule.AdapterId, StringComparison.OrdinalIgnoreCase)));

    Dictionary<string, string> appVpnDraft = new(StringComparer.OrdinalIgnoreCase);
    bool windowsRoutingSupported;
    Dictionary<string, string>? windowsAppDraft;
    string? windowsFallbackDraft;
    bool loadingWindowsFallback;
    readonly HashSet<string> seenCheckedWindowsApps = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> suggestedWindowsApps = new(StringComparer.OrdinalIgnoreCase);
    WindowsAdapterChoice[] windowsAdapters = [];
    string adapterListSignature = "";

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

    /// <summary>
    /// What the tunnel is given: the VPN apps' paths plus, for groups set to Through VPN, the executables whose file
    /// was not found, by name. WireSock matches those by process name, the same way EZConnect routes catalog games.
    /// </summary>
    string[] VpnTargets() => VpnApps().Concat(groups.Where(group => group.Enabled && !group.Direct)
            .SelectMany(group => group.Pending).Where(name => RouteFinder.Core.WireSockTunnel.IsExecutableName(name) && !IsGenericName(name)))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    AppRouteMode CurrentMode(string path, IReadOnlyList<AppRouteMode> modes)
    {
        string adapterId = windowsAppDraft?.GetValueOrDefault(path) ?? "";
        if (adapterId.Length > 0 && !appVpnDraft.ContainsKey(path))
            return modes.FirstOrDefault(mode => mode.Kind == AppRouteKind.Adapter &&
                mode.AdapterId.Equals(adapterId, StringComparison.OrdinalIgnoreCase)) ?? modes[^1];
        string uplink = route is { } current ? UplinkFor(current) ?? "" : "";
        return modes.FirstOrDefault(mode => mode.Kind == AppRouteKind.Vpn &&
            mode.AdapterId.Equals(uplink, StringComparison.OrdinalIgnoreCase)) ?? modes[0];
    }

    List<AppRouteMode> RouteModes(string path, string? savedAdapter = null)
    {
        // VPN choices: the tunnel's adapter is shared by every Through VPN app, because one tunnel runs at a time.
        var modes = new List<AppRouteMode> { new(AppRouteKind.Vpn, "", "Through VPN (automatic)") };
        if (route is { } current && !RouteNeedsAutomaticUplink(current))
        {
            modes.AddRange(windowsAdapters.Where(adapter => adapter.HasGateway)
                .Select(adapter => new AppRouteMode(AppRouteKind.Vpn, adapter.Id, $"Through VPN via {adapter.Label}")));
            if (UplinkFor(current) is { } uplink && !modes.Any(mode => mode.AdapterId.Equals(uplink, StringComparison.OrdinalIgnoreCase)))
                modes.Add(new(AppRouteKind.Vpn, uplink, "Through VPN via saved adapter (unavailable)", false));
        }
        modes.AddRange(windowsAdapters.Select(adapter => new AppRouteMode(AppRouteKind.Adapter, adapter.Id,
            "Direct · " + adapter.Label, adapter.Available)));
        string saved = savedAdapter ?? windowsAppDraft?.GetValueOrDefault(path) ?? "";
        if (saved.Length > 0 && !modes.Any(mode => mode.AdapterId.Equals(saved, StringComparison.OrdinalIgnoreCase)))
            modes.Add(new(AppRouteKind.Adapter, saved, "Saved adapter unavailable", false));
        return modes;
    }

    void ChooseAppRoute(string path, AppRouteMode mode)
    {
        if (windowsAppDraft is not { } draft || busy) return;
        // The menu only decides how the app connects. Whether it is used at all is the tick box beside it.
        suggestedWindowsApps.Remove(path);
        bool uplinkChanged = false;
        if (mode.Kind == AppRouteKind.Vpn)
        {
            draft[path] = "";
            string? wanted = mode.AdapterId.Length == 0 ? null : mode.AdapterId;
            if (route is { } current && UplinkFor(current) != wanted)
            {
                SetRouteUplink(current, wanted);
                uplinkChanged = true;
            }
        }
        else
        {
            appVpnDraft.Remove(path);
            draft[path] = mode.AdapterId;
        }
        StoreRouteChoices();
        // A new tunnel adapter applies to every Through VPN app, so their menus are redrawn to match.
        if (uplinkChanged) DispatcherQueue.TryEnqueue(() => { RenderApps(); Refresh(refreshRouteBrowser: false); });
        else Refresh(refreshRouteBrowser: false);
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
                AppVpnRoutes = new(appVpnDraft, StringComparer.OrdinalIgnoreCase),
                DirectFallbackAdapter = windowsFallbackDraft
            };
            ProfileStore.SavePreferences(next);
            preferences = next;
        }
        catch (Exception error) { Report($"Could not save how this app connects. {error.Message}"); }
    }

    bool SyncWindowsAppSuggestions()
    {
        if (windowsAppDraft is not { } draft) return false;
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

    void RefreshWindowsAdapters(object sender, RoutedEventArgs args)
    {
        ReloadWindowsAdapters();
        Refresh(refreshRouteBrowser: false);
    }

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
        // Rebuilding the menus closes any that are open, so skip it when nothing about the adapters changed.
        string listSignature = string.Join("|", windowsAdapters.Select(adapter => $"{adapter.Id}:{adapter.Label}:{adapter.Available}:{adapter.HasGateway}"))
            + "|" + windowsFallbackDraft;
        if (listSignature == adapterListSignature) return;
        adapterListSignature = listSignature;
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
        StoreRouteChoices();
        Refresh(refreshRouteBrowser: false);
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

    async Task StopAppRouter()
    {
        if (directRouting is { } routing)
        {
            await Task.Run(routing.Dispose);
            directRouting = null;
            activeFallback = null;
            activeAssignments = [];
            activePins = [];
        }
        if (appRouterVpn)
        {
            await session.DisconnectAsync();
            appRouterVpn = false;
        }
    }
}
