using System.Net.NetworkInformation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using RouteFinder.App;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    bool IsFavorite(RouteChoice choice) => preferences.Favorites.Contains(choice.Key, StringComparer.OrdinalIgnoreCase);

    async void OpenRouteOptions(object sender, RoutedEventArgs args)
    {
        if (route is not { } selected) return;
        var actions = new List<(string Title, Func<Task> Run)>();
        Add(IsFavorite(selected) ? "Remove from favorites" : "Add to favorites", () => ToggleFavorite(selected));
        if (measurements.ContainsKey(selected.Key)) Add("Clear saved test", () => ClearMeasurement(selected));
        if (!preview && selected.Profile is { } profile)
        {
            actions.Add(("Edit configuration", () => EditConfig(profile)));
            Add("Save selected apps to this profile", () => ReplaceProfile(profile, profile.Configuration, VpnApps().ToList()));
            Add("Load the profile's saved apps", () => LoadProfileApps(profile));
            if (profile.Applications is { Count: > 0 }) actions.Add(("Connect with saved apps", async () =>
            {
                LoadProfileApps(profile);
                await ChangeConnection();
            }));
            actions.Add(("Export configuration", () => ExportConfig(profile)));
            actions.Add(("Remove configuration", () => RemoveConfig(profile)));
        }
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MinWidth = 290 };
        foreach (var action in actions) list.Items.Add(action.Title);
        list.SelectedIndex = 0;
        var dialog = MakeDialog(selected.Title, list, "Continue");
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedIndex < 0) return;
        try { await actions[list.SelectedIndex].Run(); }
        catch (Exception error) { Report(error.Message); }

        void Add(string label, Action action) => actions.Add((label, () => { action(); return Task.CompletedTask; }));
    }

    (string RouteKey, string? AdapterId)? appliedPresetUplink;
    readonly Dictionary<string, string> adapterNames = new(StringComparer.OrdinalIgnoreCase);

    string? UplinkFor(RouteChoice choice) => appliedPresetUplink is { } preset && preset.RouteKey == choice.Key
        ? preset.AdapterId : preferences.RouteUplinks?.GetValueOrDefault(choice.Key);

    string UplinkLabel(string? id)
    {
        if (id is null) return UiLanguage.Text("Automatic");
        if (!adapterNames.TryGetValue(id, out string? name))
        {
            name = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(adapter => adapter.Id == id)?.Name
                ?? UiLanguage.Text("Saved adapter (unavailable)");
            adapterNames[id] = name;
        }
        return name;
    }

    /// <summary>Sets which adapter carries a route's VPN tunnel. Null means Windows chooses.</summary>
    void SetRouteUplink(RouteChoice choice, string? adapterId)
    {
        var updated = new Dictionary<string, string>(preferences.RouteUplinks ?? []);
        if (adapterId is null) updated.Remove(choice.Key);
        else updated[choice.Key] = adapterId;
        var next = preferences with { RouteUplinks = updated };
        if (!preview) WriteStore("preferences", () => ProfileStore.SavePreferences(next));
        preferences = next;
        appliedPresetUplink = null;
        adapterNames.Clear();
    }

    // A SOCKS5 route must let Windows pick the adapter.
    bool RouteNeedsAutomaticUplink(RouteChoice choice) => ConfiguredProxy is not null ||
        choice.Profile?.Configuration.Contains("Socks5Proxy", StringComparison.OrdinalIgnoreCase) == true;

    void ToggleFavorite(RouteChoice choice)
    {
        var favorites = preferences.Favorites.Where(key => !key.Equals(choice.Key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!IsFavorite(choice)) favorites.Add(choice.Key);
        var updated = preferences with { Favorites = favorites };
        if (!preview) WriteStore("preferences", () => ProfileStore.SavePreferences(updated));
        preferences = updated;
        AddActivity($"{choice.Title} · {(IsFavorite(choice) ? "Added to favorites" : "Removed from favorites")}");
    }

    void ClearMeasurement(RouteChoice choice)
    {
        var updated = new Dictionary<string, RouteTestResult>(measurements, StringComparer.Ordinal);
        updated.Remove(choice.Key);
        if (!preview) WriteStore("test results", () => ProfileStore.SaveMeasurements(updated));
        measurements.Remove(choice.Key);
        Refresh();
    }

    async Task EditConfig(SavedProfile profile)
    {
        var warning = new TextBlock { Text = "This configuration may contain a private key.", TextWrapping = TextWrapping.Wrap };
        var config = new TextBox
        {
            Text = profile.Configuration, AcceptsReturn = true, MinHeight = 240, MaxHeight = 370, MinWidth = 320,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(warning);
        content.Children.Add(config);
        var dialog = MakeDialog($"Edit {profile.Name}", content, "Save");
        dialog.PrimaryButtonClick += (_, click) =>
        {
            try
            {
                TunnelProfile.Parse(config.Text, profile.Name);
                ReplaceProfile(profile, config.Text, profile.Applications ?? []);
            }
            catch (Exception error) { warning.Text = error.Message; click.Cancel = true; }
        };
        await dialog.ShowAsync();
    }

    void ReplaceProfile(SavedProfile profile, string config, List<string> paths)
    {
        int index = profiles.IndexOf(profile);
        if (index < 0) return;
        var replacement = new SavedProfile { Name = profile.Name, Configuration = config, Applications = paths };
        var updated = profiles.ToList();
        updated[index] = replacement;
        WriteStore("profiles", () => ProfileStore.Save(updated));
        profiles[index] = replacement;
        route = Routes().First(choice => choice.Profile == replacement);
        Refresh();
        AddActivity($"Updated {profile.Name}.");
    }

    void LoadProfileApps(SavedProfile profile)
    {
        var saved = profile.Applications ?? [];
        var updated = apps.Concat(saved).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!preview && updated.Length != apps.Count)
            WriteStore("applications", () => ProfileStore.SaveApplications(updated.ToList()));
        ApplyProfileAppSelection(saved);
        SaveAppSelection();
        StoreRouteChoices();
        if (Scope.SelectedIndex == 1) Scope.SelectedIndex = 0;
        RenderApps();
        Refresh();
    }

    void ApplyProfileAppSelection(List<string> saved)
    {
        var existing = apps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        apps.AddRange(saved.Where(existing.Add));
        // The route lists these apps, so they go through the VPN. Other VPN apps switch off;
        // apps pinned to an adapter keep their own state.
        var listed = new HashSet<string>(saved, StringComparer.OrdinalIgnoreCase);
        foreach (string path in apps)
        {
            if (listed.Contains(path))
            {
                checkedApps.Add(path);
                if (windowsAppDraft is { } draft && draft.ContainsKey(path)) draft[path] = "";
                appVpnDraft.Remove(path);
            }
            else if (!IsAdapterApp(path)) checkedApps.Remove(path);
        }
    }

    async Task ExportConfig(SavedProfile profile)
    {
        var confirm = MakeDialog("Export configuration", new TextBlock
        {
            Text = "The exported file contains the configuration's private key. Keep it secure.",
            TextWrapping = TextWrapping.Wrap
        }, "Choose location");
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = profile.Name };
        picker.FileTypeChoices.Add("WireGuard configuration", [".conf"]);
        if (await picker.PickSaveFileAsync() is { } file)
        {
            await File.WriteAllTextAsync(file.Path, profile.Configuration);
            AddActivity($"Exported {profile.Name}.");
        }
    }

    async Task RemoveConfig(SavedProfile profile)
    {
        var confirm = MakeDialog("Remove configuration",
            new TextBlock { Text = $"Remove {profile.Name} from RouteFinder?", TextWrapping = TextWrapping.Wrap }, "Remove");
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        WriteStore("profiles", () => ProfileStore.Save(profiles.Where(item => item != profile).ToList()));
        profiles.Remove(profile);
        route = Routes().FirstOrDefault();
        Refresh();
    }
}
