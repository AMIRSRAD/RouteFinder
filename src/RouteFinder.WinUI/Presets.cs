using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;
using RouteFinder.Service;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    readonly List<ProfileStore.RoutingPreset> presets = [];

    async void OpenPresets(object sender, RoutedEventArgs args)
    {
        if (busy || Connected || session.NeedsCleanup) return;
        var picker = new ComboBox
        {
            Header = "Saved setup", ItemsSource = presets.Select(preset => preset.Name).ToArray(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var remove = new Button { Content = "Remove preset" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 300 };
        panel.Children.Add(picker);
        panel.Children.Add(details);
        panel.Children.Add(remove);
        var dialog = MakeDialog("Routing presets", panel, "Apply preset");
        dialog.SecondaryButtonText = "Save current setup";
        dialog.IsSecondaryButtonEnabled = route is not null && (Scope.SelectedIndex == 1 || checkedApps.Count > 0);
        dialog.CloseButtonText = "Close";
        ProfileStore.RoutingPreset? Selection() => picker.SelectedIndex >= 0 ? presets[picker.SelectedIndex] : null;
        void Update()
        {
            var selected = Selection();
            bool available = selected is not null && FindPresetRoute(selected) is not null &&
                (selected.CatalogGame is null || FindPresetGame(selected) is not null);
            dialog.IsPrimaryButtonEnabled = available;
            remove.IsEnabled = selected is not null;
            details.Text = selected is null ? "Save the current apps, route, and internet adapter as a setup." :
                $"Apps · {(selected.EntirePc ? "Entire PC" : string.Join(", ", selected.Apps.Select(Path.GetFileNameWithoutExtension)))}\n" +
                $"Route · {selected.RouteName}\n" +
                $"Internet adapter · {(selected.HasAdapterChoice ? UplinkLabel(selected.AdapterId) : "Route default")}" +
                (selected.BlockOtherApps ? "\nOther apps are blocked while connected." : "") +
                (selected.CatalogGame is null ? "" : $"\nCatalog: {selected.CatalogGame}") +
                (available ? "" : "\nThis setup is unavailable. Load its route and catalog before applying it.");
        }
        picker.SelectionChanged += (_, _) => Update();
        picker.SelectedIndex = presets.Count > 0 ? 0 : -1;
        Update();
        ProfileStore.RoutingPreset? removal = null;
        remove.Click += (_, _) => { removal = Selection(); dialog.Hide(); };
        var result = await dialog.ShowAsync();
        try
        {
            if (removal is not null)
            {
                var confirm = MakeDialog("Remove preset",
                    new TextBlock { Text = $"Remove {removal.Name}?", TextWrapping = TextWrapping.Wrap }, "Remove");
                if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                    SavePresets(presets.Where(item => item != removal).ToList());
            }
            else if (result == ContentDialogResult.Secondary) await SaveCurrentPreset();
            else if (result == ContentDialogResult.Primary && Selection() is { } selected) ApplyPreset(selected);
        }
        catch (Exception error) { Report(error.Message); }
    }

    async Task SaveCurrentPreset()
    {
        if (route is not { } selected) return;
        bool entirePc = Scope.SelectedIndex == 1;
        var paths = entirePc ? [] : apps.Where(checkedApps.Contains).ToArray();
        if (!entirePc && paths.Length == 0) return;
        var name = new TextBox { Header = "Preset name", MaxLength = 64 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 8, MinWidth = 300 };
        panel.Children.Add(name);
        panel.Children.Add(error);
        var dialog = MakeDialog("Save routing preset", panel, "Save preset");
        dialog.IsPrimaryButtonEnabled = false;
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        dialog.PrimaryButtonClick += (_, click) =>
        {
            if (!presets.Any(preset => preset.Name.Equals(name.Text.Trim(), StringComparison.OrdinalIgnoreCase))) return;
            error.Text = "A preset already uses that name.";
            click.Cancel = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        SavePresets([.. presets, new(name.Text.Trim(), selected.Key, selected.Title, entirePc, paths,
            entirePc || selected.Service is null ? null : selectedGame?.Name, BlockOtherApps: BlocksOtherApps,
            AdapterId: UplinkFor(selected), HasAdapterChoice: true)]);
        Report("Routing preset saved.", InfoBarSeverity.Success);
    }

    void SavePresets(List<ProfileStore.RoutingPreset> updated)
    {
        if (!preview) WriteStore("routing presets", () => ProfileStore.SavePresets(updated));
        presets.Clear();
        presets.AddRange(updated);
    }

    RouteChoice? FindPresetRoute(ProfileStore.RoutingPreset preset) =>
        Routes().FirstOrDefault(choice => choice.Key.Equals(preset.RouteKey, StringComparison.OrdinalIgnoreCase));

    CatalogGame? FindPresetGame(ProfileStore.RoutingPreset preset) =>
        accountCatalog?.Games.FirstOrDefault(game => game.Name.Equals(preset.CatalogGame, StringComparison.OrdinalIgnoreCase));

    void ApplyPreset(ProfileStore.RoutingPreset preset)
    {
        if (busy || Connected || session.NeedsCleanup || FindPresetRoute(preset) is not { } selected) return;
        var game = FindPresetGame(preset);
        if (preset.CatalogGame is not null && game is null) return;
        var updated = apps.Concat(preset.Apps).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!preview && updated.Count != apps.Count) WriteStore("applications", () => ProfileStore.SaveApplications(updated));
        apps.Clear();
        apps.AddRange(updated);
        checkedApps.Clear();
        checkedApps.UnionWith(preset.Apps);
        route = selected;
        appliedPresetUplink = preset.HasAdapterChoice ? (selected.Key, preset.AdapterId) : null;
        selectedGame = game;
        Scope.SelectedIndex = ScopeIndex(preset.EntirePc, preset.BlockOtherApps);
        SaveAppSelection();
        RenderApps();
        Refresh();
        Report($"Applied {preset.Name}. Review the selection, then connect.", InfoBarSeverity.Success);
    }
}
