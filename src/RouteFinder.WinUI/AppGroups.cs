using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RouteFinder.App;

namespace RouteFinder.WinUI;

/// <summary>
/// App groups: a game and its helpers shown as one parent item. The group owns the on/off switch and the route
/// choice; its members are ordinary entries in the app list, so routing treats them like any other app.
/// </summary>
public sealed partial class MainWindow
{
    readonly List<ProfileStore.AppGroup> groups = [];
    bool resolving;

    // Groups are restored from saved preferences; members that are no longer in the app list are dropped.
    void LoadGroups()
    {
        groups.Clear();
        foreach (var saved in preferences.AppGroups ?? [])
        {
            var members = saved.Members.Where(path => apps.Contains(path, StringComparer.OrdinalIgnoreCase)).ToList();
            if (members.Count == 0 && saved.Pending.Count == 0) continue;
            groups.Add(saved with { Members = members, Pending = saved.Pending.ToList() });
        }
        foreach (var group in groups)
        {
            // A path belongs to one group only.
            foreach (var other in groups.Where(item => item != group))
                other.Members.RemoveAll(path => group.Members.Contains(path, StringComparer.OrdinalIgnoreCase));
        }
    }

    HashSet<string> GroupedPaths() =>
        groups.SelectMany(group => group.Members).ToHashSet(StringComparer.OrdinalIgnoreCase);

    void SaveGroups()
    {
        var next = preferences with { AppGroups = groups.Count == 0 ? null : groups.ToList() };
        preferences = next;
        if (preview) return;
        try { WriteStore("preferences", () => ProfileStore.SavePreferences(next)); }
        catch (Exception error) { Report($"Could not save app groups. {error.Message}"); }
    }

    // A found executable takes on its group's on/off state and route choice.
    void ApplyGroupState(ProfileStore.AppGroup group, string path)
    {
        if (group.Enabled) checkedApps.Add(path);
        else checkedApps.Remove(path);
        if (windowsAppDraft is { } draft)
        {
            appVpnDraft.Remove(path);
            draft[path] = group.Direct ? group.AdapterId : "";
        }
    }

    /// <summary>Adds a catalog game as one group. Executables that were not found stay on it by name.</summary>
    void AddCatalogGroup(string name, IEnumerable<string> found, IEnumerable<string> expectedNames)
    {
        var paths = found.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var group = groups.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            group = new(name, [], []);
            groups.Add(group);
        }
        var updated = apps.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!preview && updated.Count != apps.Count) WriteStore("applications", () => ProfileStore.SaveApplications(updated));
        apps.Clear();
        apps.AddRange(updated);
        foreach (string path in paths)
        {
            if (!group.Members.Contains(path, StringComparer.OrdinalIgnoreCase)) group.Members.Add(path);
            ApplyGroupState(group, path);
        }
        // Keep one entry per executable name that has no file yet.
        var pending = expectedNames.Concat(group.Pending).Select(ExecutableStem).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(stem => !group.Members.Any(path => ExecutableStem(path).Equals(stem, StringComparison.OrdinalIgnoreCase)))
            .Select(stem => stem + ".exe").ToList();
        group.Pending.Clear();
        group.Pending.AddRange(pending);
        SaveAppSelection();
        SaveGroups();
        StoreRouteChoices();
        RenderApps();
        Refresh();
    }

    // Files added later (browsing, a running app) that match a waiting name join that group.
    HashSet<string> AttachToGroups(IEnumerable<string> paths)
    {
        var attached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            var group = groups.FirstOrDefault(item => item.Pending.Any(name => ExecutableMatches(path, name)));
            if (group is null) continue;
            group.Pending.RemoveAll(name => ExecutableMatches(path, name));
            if (!group.Members.Contains(path, StringComparer.OrdinalIgnoreCase)) group.Members.Add(path);
            ApplyGroupState(group, path);
            attached.Add(path);
        }
        if (attached.Count > 0) SaveGroups();
        return attached;
    }

    DateTime lastDiskSearch = DateTime.MinValue;
    string lastPendingSignature = "";

    /// <summary>
    /// Looks for waiting executables among the running processes and adds the ones it finds. The slower search of
    /// install folders runs only when asked and at most every ten minutes for the same waiting names.
    /// </summary>
    async Task ResolvePendingApps(bool disk = true)
    {
        var names = groups.SelectMany(group => group.Pending).ToArray();
        if (names.Length == 0 || preview || resolving) return;
        string signature = string.Join("|", names.Order(StringComparer.OrdinalIgnoreCase));
        bool searchDisk = disk && (signature != lastPendingSignature || DateTime.UtcNow - lastDiskSearch > TimeSpan.FromMinutes(10));
        resolving = true;
        string[] found;
        var searches = groups.Where(group => group.Pending.Count > 0)
            .Select(group => (Names: group.Pending.ToArray(), Related: group.Members.ToArray(), group.Name)).ToArray();
        try
        {
            found = await Task.Run(() =>
            {
                // Running processes first, then the files on disk near each group's known files and install folder.
                var running = FindRunningApps(new HashSet<string>(StringComparer.OrdinalIgnoreCase), names, false);
                var trusted = searches.SelectMany(search => TrustedRunning(running, search.Names, search.Related, search.Name)).ToArray();
                var installed = !searchDisk ? [] : searches.SelectMany(search => FindInstalledApps(
                    search.Names.Where(name => !trusted.Any(path => ExecutableMatches(path, name))), search.Related, search.Name));
                return trusted.Concat(installed).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(path => !apps.Contains(path, StringComparer.OrdinalIgnoreCase)).ToArray();
            });
            if (searchDisk)
            {
                lastDiskSearch = DateTime.UtcNow;
                lastPendingSignature = signature;
            }
        }
        finally { resolving = false; }
        if (found.Length == 0 || closing) return;
        var updated = apps.Concat(found).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        WriteStore("applications", () => ProfileStore.SaveApplications(updated));
        apps.Clear();
        apps.AddRange(updated);
        AttachToGroups(found);
        SaveAppSelection();
        StoreRouteChoices();
        AddActivity($"Found {found.Length} waiting app(s): {string.Join(", ", found.Select(Path.GetFileName))}.");
        RenderApps();
        Refresh();
    }

    void ToggleGroup(ProfileStore.AppGroup group, bool on)
    {
        group.Enabled = on;
        foreach (string path in group.Members)
        {
            if (on) checkedApps.Add(path);
            else checkedApps.Remove(path);
        }
        SaveAppSelection(defer: true);
        SaveGroups();
        Refresh(refreshRouteBrowser: false);
    }

    void ChooseGroupRoute(ProfileStore.AppGroup group, AppRouteMode mode)
    {
        if (windowsAppDraft is not { } draft || busy) return;
        group.Direct = mode.Kind == AppRouteKind.Adapter;
        group.AdapterId = group.Direct ? mode.AdapterId : "";
        bool uplinkChanged = false;
        foreach (string path in group.Members)
        {
            suggestedWindowsApps.Remove(path);
            appVpnDraft.Remove(path);
            draft[path] = group.Direct ? mode.AdapterId : "";
        }
        if (mode.Kind == AppRouteKind.Vpn && route is { } current)
        {
            string? wanted = mode.AdapterId.Length == 0 ? null : mode.AdapterId;
            if (UplinkFor(current) != wanted)
            {
                SetRouteUplink(current, wanted);
                uplinkChanged = true;
            }
        }
        SaveGroups();
        StoreRouteChoices();
        if (uplinkChanged) DispatcherQueue.TryEnqueue(() => { RenderApps(); Refresh(refreshRouteBrowser: false); });
        else Refresh(refreshRouteBrowser: false);
    }

    void RemoveGroup(ProfileStore.AppGroup group)
    {
        groups.Remove(group);
        foreach (string path in group.Members.ToArray()) RemoveApplication(path, rebuild: false);
        SaveGroups();
        RenderApps();
        Refresh();
    }

    void RemoveGroupMember(ProfileStore.AppGroup group, string path)
    {
        group.Members.Remove(path);
        if (group.Members.Count == 0 && group.Pending.Count == 0) groups.Remove(group);
        SaveGroups();
        RemoveApplication(path);
    }

    // One expandable row for the whole group: switch, name, route menu, and a list of its executables inside.
    UIElement BuildGroupRow(ProfileStore.AppGroup group)
    {
        int missing = group.Pending.Count;
        var tile = new Grid { Style = (Style)Root.Resources["IconTileGrid"] };
        tile.Children.Add(new FontIcon { Glyph = "", Style = (Style)Root.Resources["TileGlyph"] });
        if (!preview && group.Members.FirstOrDefault() is { } first) _ = ShowAppIcon(first, tile);

        var check = new CheckBox
        {
            IsChecked = group.Enabled, MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, -8, 0)
        };
        AutomationProperties.SetName(check, group.Name);
        ToolTipService.SetToolTip(check, UiLanguage.Text("Use this group"));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = group.Name, TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });
        string count = $"{group.Members.Count} {UiLanguage.Text(group.Members.Count == 1 ? "app" : "apps")}";
        text.Children.Add(new TextBlock
        {
            Text = missing == 0 ? count : group.Direct
                ? $"{count} · {missing} {UiLanguage.Text("not found yet")}"
                : $"{count} · {missing} {UiLanguage.Text("by name")}",
            TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Root.Resources["Caption"]
        });

        var modes = RouteModes(group.Members.FirstOrDefault() ?? "", group.Direct ? group.AdapterId : "");
        var current = group.Direct
            ? modes.FirstOrDefault(mode => mode.Kind == AppRouteKind.Adapter && mode.AdapterId.Equals(group.AdapterId, StringComparison.OrdinalIgnoreCase)) ?? modes[^1]
            : CurrentMode(group.Members.FirstOrDefault() ?? "", modes);
        var choice = new ComboBox { ItemsSource = modes, SelectedItem = current, MinWidth = 200, MaxWidth = 280, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(choice, $"{UiLanguage.Text("How")} {group.Name} {UiLanguage.Text("connects")}");
        bool loading = true;
        choice.SelectionChanged += (_, _) =>
        {
            if (!loading && choice.SelectedItem is AppRouteMode mode) ChooseGroupRoute(group, mode);
        };
        loading = false;

        var remove = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(10, 8, 10, 8),
            VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        AutomationProperties.SetName(remove, $"Remove {group.Name}");
        ToolTipService.SetToolTip(remove, UiLanguage.Text("Remove the whole group"));
        remove.Click += (_, _) => RemoveGroup(group);

        var header = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 6, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(tile, 1);
        Grid.SetColumn(text, 2);
        Grid.SetColumn(choice, 3);
        Grid.SetColumn(remove, 4);
        header.Children.Add(check);
        header.Children.Add(tile);
        header.Children.Add(text);
        header.Children.Add(choice);
        header.Children.Add(remove);

        // Members, then the names still waiting for a file.
        var members = new StackPanel { Spacing = 2, Margin = new Thickness(62, 0, 12, 4) };
        foreach (string path in group.Members)
        {
            var line = new Grid { ColumnSpacing = 8, MinHeight = 32 };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock { Text = Path.GetDirectoryName(path) ?? path, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Root.Resources["Caption"] });
            ToolTipService.SetToolTip(info, path);
            var take = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 11 }, Padding = new Thickness(8, 5, 8, 5),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0)
            };
            AutomationProperties.SetName(take, $"Remove {Path.GetFileName(path)} from {group.Name}");
            ToolTipService.SetToolTip(take, UiLanguage.Text("Remove from the group"));
            take.Click += (_, _) => RemoveGroupMember(group, path);
            Grid.SetColumn(take, 1);
            line.Children.Add(info);
            line.Children.Add(take);
            members.Children.Add(line);
        }
        foreach (string name in group.Pending)
        {
            var line = new StackPanel { MinHeight = 32, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65 };
            line.Children.Add(new TextBlock { Text = name });
            line.Children.Add(new TextBlock
            {
                Text = UiLanguage.Text(group.Direct ? "Not found yet" : "Matched by name"),
                Style = (Style)Root.Resources["Caption"], TextWrapping = TextWrapping.Wrap
            });
            members.Children.Add(line);
        }

        var expander = new Expander
        {
            Header = header, Content = members, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(8), Tag = group
        };
        void Dim() => text.Opacity = tile.Opacity = choice.Opacity = check.IsChecked == true ? 1 : 0.5;
        Dim();
        check.Checked += (_, _) => { ToggleGroup(group, true); Dim(); };
        check.Unchecked += (_, _) => { ToggleGroup(group, false); Dim(); };
        return expander;
    }

    // Group headers hold the same controls as an app row, so they follow the same enabled rule.
    void SetGroupRowsEnabled(bool enabled)
    {
        foreach (var expander in AppRows.Children.OfType<Expander>())
        {
            if (expander.Header is Grid header)
                foreach (var control in header.Children.OfType<Control>()) control.IsEnabled = enabled;
            if (expander.Content is StackPanel members)
                foreach (var button in members.Children.OfType<Grid>().SelectMany(line => line.Children.OfType<Button>())) button.IsEnabled = enabled;
        }
    }
}
