using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;
using RouteFinder.Service;

namespace RouteFinder.WinUI;

/// <summary>
/// The one place presets live: Settings. A preset captures the whole setup (route, adapter, apps and how each
/// connects, traffic scope, default adapter, and connection settings) so it can be restored in one step.
/// </summary>
public sealed partial class MainWindow
{
    readonly List<ProfileStore.RoutingPreset> presets = [];
    ComboBox PresetPicker = null!;
    TextBlock PresetDetails = null!;
    Button PresetApplyButton = null!, PresetSaveButton = null!, PresetDeleteButton = null!;
    bool loadingPresets;

    // Builds the Presets card at the top of the Settings page.
    void BuildPresetSection(StackPanel host)
    {
        host.Children.Add(new TextBlock
        {
            Text = UiLanguage.Text("Presets"), Style = (Style)Root.Resources["SectionLabel"], Margin = new Thickness(2, 8, 0, 6)
        });
        PresetPicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = UiLanguage.Text("No saved presets") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PresetPicker, UiLanguage.Text("Saved preset"));
        PresetDetails = new TextBlock { Style = (Style)Root.Resources["Caption"], TextWrapping = TextWrapping.Wrap };
        PresetApplyButton = new Button { Content = UiLanguage.Text("Apply"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        PresetSaveButton = new Button { Content = UiLanguage.Text("Save current setup…") };
        PresetDeleteButton = new Button { Content = UiLanguage.Text("Delete") };
        PresetPicker.SelectionChanged += (_, _) => { if (!loadingPresets) UpdatePresetSection(); };
        PresetApplyButton.Click += (_, _) => { if (SelectedPreset() is { } preset) ApplyPreset(preset); };
        PresetSaveButton.Click += async (_, _) => await SaveCurrentPreset();
        PresetDeleteButton.Click += async (_, _) => await DeleteSelectedPreset();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(PresetApplyButton);
        buttons.Children.Add(PresetSaveButton);
        buttons.Children.Add(PresetDeleteButton);
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = UiLanguage.Text("A preset saves everything in one go: route, internet adapter, your apps and how each connects, traffic scope, default adapter, and the connection settings below."),
            Style = (Style)Root.Resources["Secondary"]
        });
        body.Children.Add(PresetPicker);
        body.Children.Add(PresetDetails);
        body.Children.Add(buttons);
        host.Children.Add(new Border { Style = (Style)Root.Resources["Card"], CornerRadius = new CornerRadius(6), Child = body });
        UpdatePresetSection();
    }

    ProfileStore.RoutingPreset? SelectedPreset() =>
        PresetPicker is { SelectedIndex: >= 0 } && PresetPicker.SelectedIndex < presets.Count ? presets[PresetPicker.SelectedIndex] : null;

    void ReloadPresetPicker(ProfileStore.RoutingPreset? keep = null)
    {
        if (PresetPicker is null) return;
        loadingPresets = true;
        try
        {
            PresetPicker.ItemsSource = presets.Select(preset => preset.Name).ToArray();
            PresetPicker.SelectedIndex = presets.Count == 0 ? -1 : Math.Max(keep is null ? 0 : presets.IndexOf(keep), 0);
        }
        finally { loadingPresets = false; }
        UpdatePresetSection();
    }

    bool PresetAvailable(ProfileStore.RoutingPreset preset) =>
        (preset.RouteKey.Length == 0 || FindPresetRoute(preset) is not null) &&
        (preset.CatalogGame is null || FindPresetGame(preset) is not null);

    void UpdatePresetSection()
    {
        if (PresetPicker is null) return;
        bool editable = !busy && !Connected && !session.NeedsCleanup && !AppRouterActive;
        var selected = SelectedPreset();
        bool available = selected is not null && PresetAvailable(selected);
        PresetApplyButton.IsEnabled = editable && available;
        PresetDeleteButton.IsEnabled = !busy && selected is not null;
        PresetSaveButton.IsEnabled = !busy && route is not null;
        PresetPicker.IsEnabled = !busy;
        if (selected is null)
        {
            PresetDetails.Text = UiLanguage.Text("Nothing saved yet. Set things up, then choose Save current setup.");
            return;
        }
        var lines = new List<string>
        {
            $"{UiLanguage.Text("Route")} · {(selected.RouteKey.Length == 0 ? UiLanguage.Text("Keep the current route") : selected.RouteName)}",
            $"{UiLanguage.Text("Apps")} · " + (selected.EntirePc ? UiLanguage.Text("Entire PC")
                : string.Join(", ", (selected.EnabledApps ?? selected.Apps).Select(Path.GetFileNameWithoutExtension).DefaultIfEmpty("—"))),
            $"{UiLanguage.Text("Internet adapter")} · {(selected.HasAdapterChoice ? UplinkLabel(selected.AdapterId) : UiLanguage.Text("Route default"))}"
        };
        if (selected.IsFull)
        {
            int direct = selected.AppAdapters?.Count(item => item.Value.Length > 0) ?? 0;
            if (direct > 0) lines.Add($"{direct} {UiLanguage.Text("apps pinned to an adapter")}");
            if (selected.Settings is not null) lines.Add(UiLanguage.Text("Includes connection settings"));
        }
        if (selected.BlockOtherApps) lines.Add(UiLanguage.Text("Other apps are blocked while connected."));
        if (selected.CatalogGame is not null) lines.Add($"{UiLanguage.Text("Catalog")}: {selected.CatalogGame}");
        if (!available) lines.Add(UiLanguage.Text("This preset is unavailable. Load its route and catalog before applying it."));
        PresetDetails.Text = string.Join("\n", lines);
    }

    async Task SaveCurrentPreset()
    {
        if (route is not { } selected || busy) return;
        var name = new TextBox { Header = UiLanguage.Text("Preset name"), MaxLength = 64 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 8, MinWidth = 300 };
        panel.Children.Add(name);
        panel.Children.Add(error);
        var dialog = MakeDialog("Save preset", panel, "Save");
        dialog.IsPrimaryButtonEnabled = false;
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        dialog.PrimaryButtonClick += (_, click) =>
        {
            if (!presets.Any(preset => preset.Name.Equals(name.Text.Trim(), StringComparison.OrdinalIgnoreCase))) return;
            error.Text = UiLanguage.Text("A preset already uses that name.");
            click.Cancel = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var created = CapturePreset(name.Text.Trim(), selected);
            SavePresets([.. presets, created]);
            ReloadPresetPicker(created);
            Report("Preset saved.", InfoBarSeverity.Success);
        }
        catch (Exception failure) { Report(failure.Message); }
    }

    ProfileStore.RoutingPreset CapturePreset(string name, RouteChoice selected)
    {
        var draft = windowsAppDraft ?? [];
        var settings = new ProfileStore.PresetSettings(preferences.KillSwitch, preferences.PortableEngine, preferences.AutoMtu,
            preferences.BypassLan, preferences.DnsOverride, preferences.BypassDestinations, preferences.ExcludedApplications ?? [],
            preferences.TunnelMtu, preferences.KeepaliveSeconds, preferences.PeerResponseSeconds, preferences.RouteTestSeconds,
            preferences.OnDemandIdleSeconds, preferences.MinimizeToTray, preferences.RecoverStoppedTunnel);
        return new(name, selected.Key, selected.Title, Scope.SelectedIndex == 1, apps.ToArray(),
            Scope.SelectedIndex == 1 || selected.Service is null ? null : selectedGame?.Name, BlockOtherApps: BlocksOtherApps,
            AdapterId: UplinkFor(selected), HasAdapterChoice: true,
            EnabledApps: apps.Where(checkedApps.Contains).ToArray(),
            AppAdapters: apps.Where(draft.ContainsKey).ToDictionary(path => path, path => draft[path], StringComparer.OrdinalIgnoreCase),
            AppVpnRoutes: new(appVpnDraft, StringComparer.OrdinalIgnoreCase),
            DefaultAdapter: windowsFallbackDraft, HasDefaultAdapter: true, Settings: settings);
    }

    async Task DeleteSelectedPreset()
    {
        if (busy || SelectedPreset() is not { } removal) return;
        var confirm = MakeDialog("Delete preset",
            new TextBlock { Text = $"{UiLanguage.Text("Delete")} {removal.Name}?", TextWrapping = TextWrapping.Wrap }, "Delete");
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            SavePresets(presets.Where(item => item != removal).ToList());
            ReloadPresetPicker();
        }
        catch (Exception error) { Report(error.Message); }
    }

    void SavePresets(List<ProfileStore.RoutingPreset> updated)
    {
        if (!preview) WriteStore("routing presets", () => ProfileStore.SavePresets(updated));
        presets.Clear();
        presets.AddRange(updated);
    }

    // Adapter-rule profiles saved by earlier versions now live with the other presets.
    void MigrateRoutingProfiles()
    {
        if (preview || preferences.AppRoutingProfiles is not { Count: > 0 } legacy || unavailableStores.Count > 0) return;
        try
        {
            var converted = legacy
                .Where(profile => !presets.Any(preset => preset.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(profile => new ProfileStore.RoutingPreset(profile.Name, "", "", false, profile.Adapters.Keys.ToArray(), null,
                    EnabledApps: profile.Adapters.Keys.ToArray(), AppAdapters: new(profile.Adapters, StringComparer.OrdinalIgnoreCase),
                    AppVpnRoutes: new(profile.VpnRoutes, StringComparer.OrdinalIgnoreCase),
                    DefaultAdapter: profile.DefaultAdapter, HasDefaultAdapter: true)).ToList();
            var all = presets.Concat(converted).ToList();
            ProfileStore.SavePresets(all);
            var cleared = preferences with { AppRoutingProfiles = null };
            ProfileStore.SavePreferences(cleared);
            presets.Clear();
            presets.AddRange(all);
            preferences = cleared;
            AddActivity($"Moved {converted.Count} saved routing profile(s) into Settings > Presets.");
        }
        catch (Exception error) { AddActivity($"Could not move saved routing profiles. {error.Message}"); }
    }

    RouteChoice? FindPresetRoute(ProfileStore.RoutingPreset preset) =>
        Routes().FirstOrDefault(choice => choice.Key.Equals(preset.RouteKey, StringComparison.OrdinalIgnoreCase));

    CatalogGame? FindPresetGame(ProfileStore.RoutingPreset preset) =>
        accountCatalog?.Games.FirstOrDefault(game => game.Name.Equals(preset.CatalogGame, StringComparison.OrdinalIgnoreCase));

    void ApplyPreset(ProfileStore.RoutingPreset preset)
    {
        if (busy || Connected || session.NeedsCleanup || AppRouterActive) return;
        var selected = preset.RouteKey.Length == 0 ? route : FindPresetRoute(preset);
        if (preset.RouteKey.Length > 0 && selected is null) return;
        var game = FindPresetGame(preset);
        if (preset.CatalogGame is not null && game is null) return;
        var updated = apps.Concat(preset.Apps).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!preview && updated.Count != apps.Count) WriteStore("applications", () => ProfileStore.SaveApplications(updated));
        apps.Clear();
        apps.AddRange(updated);
        checkedApps.Clear();
        if (preset.IsFull)
        {
            checkedApps.UnionWith(preset.EnabledApps!);
            // Replace how each app connects with the preset's choices; apps it does not mention connect through the VPN.
            windowsAppDraft = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in apps) windowsAppDraft[path] = preset.AppAdapters?.GetValueOrDefault(path) ?? "";
            appVpnDraft = new(preset.AppVpnRoutes ?? [], StringComparer.OrdinalIgnoreCase);
            suggestedWindowsApps.Clear();
            seenCheckedWindowsApps.Clear();
            if (preset.HasDefaultAdapter) windowsFallbackDraft = preset.DefaultAdapter;
            if (preset.Settings is { } snapshot) ApplyPresetSettings(snapshot);
        }
        else
        {
            checkedApps.UnionWith(preset.Apps);
            // Older presets list apps that go through the VPN.
            foreach (string path in preset.Apps)
            {
                if (windowsAppDraft is { } draft && draft.ContainsKey(path)) draft[path] = "";
                appVpnDraft.Remove(path);
            }
        }
        route = selected;
        appliedPresetUplink = preset.HasAdapterChoice && selected is not null ? (selected.Key, preset.AdapterId) : null;
        selectedGame = game;
        Scope.SelectedIndex = ScopeIndex(preset.EntirePc, preset.BlockOtherApps);
        SaveAppSelection();
        if (preset.IsFull) StoreWindowsAppRules();
        ReloadWindowsAdapters();
        LoadSettingsFields();
        Refresh();
        Report($"Applied {preset.Name}. Review the setup, then connect.", InfoBarSeverity.Success);
    }

    void ApplyPresetSettings(ProfileStore.PresetSettings snapshot)
    {
        preferences = preferences with
        {
            KillSwitch = snapshot.KillSwitch, PortableEngine = snapshot.PortableEngine, AutoMtu = snapshot.AutoMtu,
            BypassLan = snapshot.BypassLan, DnsOverride = snapshot.DnsOverride, BypassDestinations = snapshot.BypassDestinations,
            ExcludedApplications = snapshot.ExcludedApplications, TunnelMtu = snapshot.TunnelMtu,
            KeepaliveSeconds = snapshot.KeepaliveSeconds, PeerResponseSeconds = snapshot.PeerResponseSeconds,
            RouteTestSeconds = snapshot.RouteTestSeconds, OnDemandIdleSeconds = snapshot.OnDemandIdleSeconds,
            MinimizeToTray = snapshot.MinimizeToTray, RecoverStoppedTunnel = snapshot.RecoverStoppedTunnel
        };
    }
}
