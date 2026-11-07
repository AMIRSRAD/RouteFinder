using System.Diagnostics;
using Microsoft.Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;
using RouteFinder.Core;
using RouteFinder.Service;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    ContentDialog MakeDialog(string title, UIElement content, string primary = "") => new()
    {
        Title = UiLanguage.Text(title), Content = content, XamlRoot = Root.XamlRoot,
        RequestedTheme = ElementTheme.Dark, PrimaryButtonText = UiLanguage.Text(primary),
        FlowDirection = Root.FlowDirection,
        CloseButtonText = UiLanguage.Text(primary.Length == 0 ? "Close" : "Cancel"),
        DefaultButton = primary.Length == 0 ? ContentDialogButton.Close : ContentDialogButton.Primary
    };

    async void PickRoute(object sender, RoutedEventArgs args)
    {
        var search = new TextBox { PlaceholderText = "Search by route, region, or source" };
        AutomationProperties.SetName(search, "Search routes");
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 320 };
        AutomationProperties.SetName(list, "Available routes");
        var empty = new TextBlock { Text = "No routes match your search." };
        var panel = new StackPanel { Spacing = 12, MinWidth = 300 };
        panel.Children.Add(search);
        var favoritesOnly = new CheckBox { Content = "Favorites only" };
        panel.Children.Add(favoritesOnly);
        var category = new ComboBox { Header = "Source or route type", ItemsSource = new[] { "All routes", "Game routes", "Download servers", "Imported configs" }, SelectedIndex = 0 };
        var sort = new ComboBox { Header = "Sort by", ItemsSource = new[] { "Latency", "Packet loss", "Jitter" }, SelectedIndex = Math.Clamp(preferences.RouteSortMetric, 0, 2) };
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        filters.Children.Add(category);
        filters.Children.Add(sort);
        panel.Children.Add(filters);
        panel.Children.Add(list);
        panel.Children.Add(empty);
        var dialog = MakeDialog("Choose route", panel, "Use route");
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        void Filter()
        {
            var previous = (list.SelectedItem as ListViewItem)?.Tag as RouteChoice ?? route;
            list.Items.Clear();
            var matches = Routes().Where(item =>
                $"{item.Title} {item.Source} {item.Detail}".Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) &&
                (favoritesOnly.IsChecked != true || IsFavorite(item))).Where(item => category.SelectedIndex switch
                {
                    1 => item.Service is { IsDownload: false },
                    2 => item.Service is { IsDownload: true },
                    3 => item.Profile is not null,
                    _ => true
                });
            foreach (var choice in matches.OrderBy(item => measurements.GetValueOrDefault(item.Key) is { Succeeded: true } ? 0 : 1)
                         .ThenBy(item => SortMetric(item, sort.SelectedIndex))
                         .ThenBy(item => measurements.GetValueOrDefault(item.Key)?.Latency ?? int.MaxValue)
                         .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase))
            {
                var row = new StackPanel { Spacing = 3, Margin = new Thickness(0, 6, 0, 6) };
                row.Children.Add(new TextBlock { Text = IsFavorite(choice) ? $"★ {choice.Title}" : choice.Title, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = $"{choice.Source}  ·  {choice.Detail}", FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap });
                if (measurements.TryGetValue(choice.Key, out var result))
                    row.Children.Add(new TextBlock { Text = FormatTest(result), FontSize = 12 });
                var item = new ListViewItem { Content = row, Tag = choice };
                AutomationProperties.SetName(item, $"{choice.Title}, {choice.Source}");
                list.Items.Add(item);
                if (choice.Key == previous?.Key) list.SelectedItem = item;
            }
            empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (list.SelectedItem is null && list.Items.Count > 0) list.SelectedIndex = 0;
            dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        }
        search.TextChanged += (_, _) => Filter();
        favoritesOnly.Checked += (_, _) => Filter();
        favoritesOnly.Unchecked += (_, _) => Filter();
        category.SelectionChanged += (_, _) => Filter();
        sort.SelectionChanged += (_, _) =>
        {
            preferences = preferences with { RouteSortMetric = sort.SelectedIndex };
            SavePreferences();
            Filter();
        };
        Filter();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            (list.SelectedItem as ListViewItem)?.Tag is not RouteChoice selected) return;
        route = selected;
        Refresh();
    }

    double SortMetric(RouteChoice choice, int sort)
    {
        if (measurements.GetValueOrDefault(choice.Key) is not { Succeeded: true } result)
            return double.PositiveInfinity;
        return sort switch
        {
            1 => result.Loss ?? float.PositiveInfinity,
            2 => result.Jitter ?? float.PositiveInfinity,
            _ => result.Latency ?? int.MaxValue
        };
    }

    FileOpenPicker CreateFilePicker(params string[] extensions)
    {
        var picker = new FileOpenPicker(AppWindow.Id);
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        return picker;
    }

    async void BrowseForApp(object sender, RoutedEventArgs args)
    {
        try
        {
            var files = await CreateFilePicker(".exe").PickMultipleFilesAsync();
            AddApplications(files.Select(file => file.Path));
        }
        catch (Exception error) { Report(error.Message); }
    }

    async void PickRunningApp(object sender, RoutedEventArgs args) => await PickRunningApps();

    async Task PickRunningApps(string[]? expected = null)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if ((expected is not null || process.MainWindowHandle != IntPtr.Zero) && process.MainModule?.FileName is { } path &&
                        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        (expected is null
                            ? !apps.Contains(path, StringComparer.OrdinalIgnoreCase)
                            : expected.Any(name => ExecutableMatches(path, name)))) paths.Add(path);
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        var list = new ListView { MaxHeight = 310, MinWidth = 290, SelectionMode = ListViewSelectionMode.Multiple };
        foreach (var path in paths.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ListViewItem { Content = Path.GetFileNameWithoutExtension(path), Tag = path };
            ToolTipService.SetToolTip(item, path);
            list.Items.Add(item);
        }
        if (list.Items.Count == 0)
        {
            Report("No new running apps were found. You can browse for an executable instead.");
            return;
        }
        var dialog = MakeDialog("Add running apps", list, "Add selected");
        dialog.IsPrimaryButtonEnabled = false;
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItems.Count > 0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        AddApplications(list.SelectedItems.OfType<ListViewItem>().Select(item => (string)item.Tag));
    }

    void AddApplications(IEnumerable<string> selection)
    {
        var chosen = selection.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (chosen.Length == 0) return;
        var updated = apps.Concat(chosen).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            if (!preview) ProfileStore.SaveApplications(updated);
            apps.Clear();
            apps.AddRange(updated);
            checkedApps.UnionWith(chosen);
            SaveAppSelection();
            RenderApps();
            Refresh();
        }
        catch (Exception error) { Report(error.Message); }
    }

    async void ImportConfig(object sender, RoutedEventArgs args)
    {
        try
        {
            var files = await CreateFilePicker(".conf", ".json").PickMultipleFilesAsync();
            if (files.Count == 0) return;
            var updated = profiles.ToList();
            var failures = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    var config = await File.ReadAllTextAsync(file.Path);
                    string baseName = Path.GetFileNameWithoutExtension(file.Path);
                    if (Path.GetExtension(file.Path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        var converted = await ConvertSessionConfig(config, baseName);
                        if (converted is null) break;
                        config = converted;
                    }
                    var saved = PrepareImport(config, baseName, updated);
                    updated.Add(saved);
                }
                catch (Exception error) { failures.Add($"{Path.GetFileName(file.Path)} · {error.Message}"); }
            }
            int imported = updated.Count - profiles.Count;
            if (imported > 0)
            {
                var first = updated[profiles.Count];
                ProfileStore.Save(updated);
                profiles.AddRange(updated.Skip(profiles.Count).ToArray());
                route = Routes().First(item => item.Profile == first);
                AddActivity($"Imported {imported} configuration(s).");
                Refresh();
            }
            if (failures.Count > 0)
            {
                var results = new TextBlock { Text = string.Join("\n\n", failures), TextWrapping = TextWrapping.Wrap };
                await MakeDialog($"Imported {imported} configuration(s)", new ScrollViewer { Content = results, MaxHeight = 340 }).ShowAsync();
            }
        }
        catch (Exception error) { Report(error.Message); }
    }

    static SavedProfile PrepareImport(string config, string baseName, IReadOnlyList<SavedProfile> library)
    {
        var parsed = TunnelProfile.Parse(config, baseName);
        string name = baseName;
        for (int number = 2; library.Any(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); number++)
            name = $"{baseName} ({number})";
        var allowed = parsed.GetInterface("AllowedApps").Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return new SavedProfile
        {
            Name = name, Configuration = config,
            Applications = allowed.All(path => Path.IsPathFullyQualified(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                path.IndexOfAny(['*', '?']) < 0) ? allowed.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : []
        };
    }

    sealed class MissingImportKey : Exception;

    async Task<string?> ConvertSessionConfig(string json, string name)
    {
        try { return EzConnectClient.ImportConfiguration(json, name, () => throw new MissingImportKey()); }
        catch (MissingImportKey)
        {
            var key = new PasswordBox { Header = "Private key for this EZConnect session" };
            var dialog = MakeDialog("Import EZConnect config", key, "Import");
            dialog.IsPrimaryButtonEnabled = false;
            key.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(key.Password);
            try
            {
                return await dialog.ShowAsync() == ContentDialogResult.Primary
                    ? EzConnectClient.ImportConfiguration(json, name, () => key.Password.Trim()) : null;
            }
            finally { key.Password = ""; }
        }
    }

}
