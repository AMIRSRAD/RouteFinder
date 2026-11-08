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

    public sealed record RoutePickerRow(string Key, string Title, string Detail, string AccessibleName, string Result, string TestAge);

    bool updatingRouteBrowser;

    void InitializeRouteBrowser()
    {
        RouteSort.ItemsSource = new[] { "Latency", "Packet loss", "Name" };
        RouteSort.SelectedIndex = Math.Clamp(preferences.RouteSortMetric, 0, 2);
        var searchDelay = DispatcherQueue.CreateTimer();
        searchDelay.Interval = TimeSpan.FromMilliseconds(150);
        searchDelay.IsRepeating = false;
        searchDelay.Tick += (_, _) => RefreshRouteBrowser();
        RouteSearch.TextChanged += (_, _) =>
        {
            searchDelay.Stop();
            searchDelay.Start();
        };
        Closed += (_, _) => searchDelay.Stop();
        RouteFilter.SelectionChanged += (_, _) => RefreshRouteBrowser();
        RouteRegion.SelectionChanged += (_, _) => RefreshRouteBrowser();
        RouteFavorites.Checked += (_, _) => RefreshRouteBrowser();
        RouteFavorites.Unchecked += (_, _) => RefreshRouteBrowser();
        RouteSort.SelectionChanged += (_, _) =>
        {
            preferences = preferences with { RouteSortMetric = RouteSort.SelectedIndex };
            SavePreferences();
            RefreshRouteBrowser();
        };
        RouteList.SelectionChanged += (_, _) =>
        {
            if (updatingRouteBrowser) return;
            if (busy || Connected || session.NeedsCleanup)
            {
                RefreshRouteBrowser();
                return;
            }
            if (RouteList.SelectedItem is not RoutePickerRow row) return;
            SelectRoute(Routes().FirstOrDefault(choice => choice.Key == row.Key));
        };
    }

    void RefreshRouteBrowser()
    {
        if (!ready || updatingRouteBrowser) return;
        updatingRouteBrowser = true;
        try
        {
            var library = Routes().ToArray();
            bool empty = library.Length == 0;
            EmptyRouteLibrary.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            RouteSearch.Visibility = RouteFilters.Visibility = RouteList.Visibility =
                RouteListSummary.Visibility = RouteTestActions.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            string[] sources = ["All sources", .. library.Select(item => item.Profile is not null
                ? "Imported configs" : item.Service is { IsDownload: true } ? "EZConnect downloads" : "EZConnect games")
                .Distinct()];
            string filter = RouteFilter.SelectedItem as string ?? "All sources";
            if (RouteFilter.ItemsSource is not string[] previousSources || !previousSources.SequenceEqual(sources))
            {
                RouteFilter.ItemsSource = sources;
                filter = sources.Contains(filter) ? filter : sources[0];
                RouteFilter.SelectedItem = filter;
            }
            string region = RouteRegion.SelectedItem as string ?? "All regions";
            var regions = new[] { "All regions" }.Concat(library.Select(item => item.Service?.RegionCode)
                .OfType<string>().Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)).ToArray();
            if (RouteRegion.ItemsSource is not string[] previous || !previous.SequenceEqual(regions))
            {
                RouteRegion.ItemsSource = regions;
                region = regions.FirstOrDefault(code => code.Equals(region, StringComparison.OrdinalIgnoreCase)) ?? regions[0];
                RouteRegion.SelectedItem = region;
            }
            string query = RouteSearch.Text.Trim();
            var matches = library.Where(item =>
                $"{item.Title} {item.Source} {item.Detail}".Contains(query, StringComparison.OrdinalIgnoreCase) &&
                (region == "All regions" || string.Equals(item.Service?.RegionCode, region, StringComparison.OrdinalIgnoreCase)) &&
                (RouteFavorites.IsChecked != true || IsFavorite(item))).Where(item => filter switch
                {
                    "EZConnect games" => item.Service is { IsDownload: false },
                    "EZConnect downloads" => item.Service is { IsDownload: true },
                    "Imported configs" => item.Profile is not null,
                    _ => true
                });
            var rows = (RouteSort.SelectedIndex == 2
                ? matches.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                : matches.OrderBy(item => measurements.GetValueOrDefault(item.Key) is { Succeeded: true } ? 0 : 1)
                    .ThenBy(item => SortMetric(item, RouteSort.SelectedIndex))
                    .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase))
                .Select(item =>
                {
                    var result = measurements.GetValueOrDefault(item.Key);
                    return new RoutePickerRow(item.Key, IsFavorite(item) ? $"★ {item.Title}" : item.Title,
                        $"{item.Source} · {item.Detail}", $"{item.Title}, {item.Source}",
                        result is null ? "Untested" : FormatTest(result),
                        result?.TestedAt?.ToLocalTime().ToString("MMM d · HH:mm") ?? "");
                }).ToArray();
            if (RouteList.ItemsSource is RoutePickerRow[] previousRows && previousRows.SequenceEqual(rows))
                rows = previousRows;
            else RouteList.ItemsSource = rows;
            var selectedRow = rows.FirstOrDefault(item => item.Key == route?.Key);
            if (!ReferenceEquals(RouteList.SelectedItem, selectedRow)) RouteList.SelectedItem = selectedRow;
            RouteFavorites.Content = RouteFavorites.IsChecked == true ? "★" : "☆";
            string selection = route is null ? "Select a route" : selectedRow is null
                ? $"Selected route outside filters: {route.Title}" : $"Selected: {route.Title}";
            RouteListSummary.Text = $"{rows.Length} of {library.Length} routes · {selection}";
            ToolTipService.SetToolTip(RouteListSummary, RouteListSummary.Text);
            TestShortlistButton.IsEnabled = rows.Length > 0 && !busy && !Connected && !session.NeedsCleanup;
        }
        finally { updatingRouteBrowser = false; }
    }

    double SortMetric(RouteChoice choice, int sort)
    {
        if (measurements.GetValueOrDefault(choice.Key) is not { Succeeded: true } result)
            return double.PositiveInfinity;
        return sort switch
        {
            1 => result.Loss ?? float.PositiveInfinity,
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

    async void PickBackgroundApp(object sender, RoutedEventArgs args) => await PickRunningApps(background: true);

    async Task PickRunningApps(string[]? expected = null, bool background = false)
    {
        if (busy || Connected || session.NeedsCleanup) return;
        var existing = apps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[]? paths = null;
        await Run("Finding running apps…", async () =>
            paths = await Task.Run(() => FindRunningApps(existing, expected, background)));
        if (exitRequested || closing || paths is null) return;
        var list = CreateRunningAppList(paths, preselect: expected is not null);
        if (paths.Length == 0)
        {
            Report(expected is null ? "No new running apps were found. You can browse for an executable instead."
                : "No matching executables were found. Start the game and any linked apps, or browse for their executable files.");
            return;
        }
        UIElement content = list;
        if (expected is not null)
        {
            var missing = expected.Where(name => !paths.Any(path => ExecutableMatches(path, name))).ToArray();
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(list);
            if (missing.Length > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = $"Could not find {string.Join(", ", missing)}. Start those apps or browse for their files.",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 390
                });
            content = panel;
        }
        var dialog = MakeDialog(expected is not null ? "Select catalog executables" :
            background ? "Add background processes" : "Add running apps", content, "Add selected");
        dialog.IsPrimaryButtonEnabled = list.SelectedItems.Count > 0;
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItems.Count > 0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        AddApplications(list.SelectedItems.OfType<ListViewItem>().Select(item => (string)item.Tag));
    }

    static ListView CreateRunningAppList(IEnumerable<string> paths, bool preselect)
    {
        var list = new ListView { MaxHeight = 310, MinWidth = 290, SelectionMode = ListViewSelectionMode.Multiple };
        foreach (var path in paths.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ListViewItem { Content = Path.GetFileName(path), Tag = path };
            ToolTipService.SetToolTip(item, path);
            list.Items.Add(item);
            if (preselect) list.SelectedItems.Add(item);
        }
        return list;
    }

    static string[] FindRunningApps(HashSet<string> existing, string[]? expected, bool background)
    {
        var names = expected?.Select(ExecutableStem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = names is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : existing.Where(path => names.Contains(ExecutableStem(path)) && File.Exists(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (names is null ? (process.MainWindowHandle == IntPtr.Zero) != background : !names.Contains(process.ProcessName))
                        continue;
                    if (process.MainModule?.FileName is { } path && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        (names is null ? !existing.Contains(path) : names.Contains(ExecutableStem(path))))
                        paths.Add(path);
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return paths.ToArray();
    }

    void AddApplications(IEnumerable<string> selection)
    {
        var chosen = selection.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (chosen.Length == 0) return;
        var updated = apps.Concat(chosen).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            if (!preview) WriteStore("applications", () => ProfileStore.SaveApplications(updated));
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
                WriteStore("profiles", () => ProfileStore.Save(updated));
                profiles.AddRange(updated.Skip(profiles.Count).ToArray());
                RouteSearch.Text = "";
                RouteFilter.SelectedIndex = 0;
                RouteRegion.SelectedIndex = 0;
                RouteFavorites.IsChecked = false;
                SelectRoute(Routes().First(item => item.Profile == first));
                AddActivity($"Imported {imported} configuration(s).");
            }
            if (failures.Count > 0)
            {
                var results = new TextBlock { Text = string.Join("\n\n", failures), TextWrapping = TextWrapping.Wrap };
                await MakeDialog($"Imported {imported} configuration(s)", new ScrollViewer { Content = results, MaxHeight = 340 }).ShowAsync();
            }
        }
        catch (Exception error) { Report(error.Message); }
    }

    void SelectRoute(RouteChoice? selected)
    {
        if (route?.Key == selected?.Key) return;
        appliedPresetUplink = null;
        route = selected;
        if (Scope.SelectedIndex != 1 && selected?.Profile is { Applications.Count: > 0 } profile)
        {
            try { LoadProfileApps(profile); }
            catch (Exception error) { Report($"Could not load the route's saved apps. {error.Message}"); }
        }
        Refresh();
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
