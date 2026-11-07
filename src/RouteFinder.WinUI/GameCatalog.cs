using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.Service;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    CatalogGame? selectedGame;

    IEnumerable<CatalogGame> CatalogEntries(string[] names) =>
        (accountCatalog?.Games ?? []).Where(entry => names.Contains(entry.Name, StringComparer.OrdinalIgnoreCase));

    string[] CatalogExecutables(CatalogGame game) => game.ExecutableNames
        .Concat(CatalogEntries(game.LinkedAppNames).SelectMany(entry => entry.ExecutableNames))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    static bool ExecutableMatches(string path, string name) =>
        string.Equals(Path.GetFileName(path), Path.GetFileName(name), StringComparison.OrdinalIgnoreCase);

    (string[] Ips, string[] Domains, string[] Conflicts) CatalogRouting(ServiceRoute? service, string[] paths)
    {
        if (selectedGame is not { } game || paths.Length == 0) return ([], [], []);
        var expected = CatalogExecutables(game);
        if (!paths.Any(path => expected.Any(name => ExecutableMatches(path, name)))) return ([], [], []);
        var conflicts = CatalogEntries(game.IncompatibleAppNames)
            .Where(entry => entry.ExecutableNames.Any(name => paths.Any(path => ExecutableMatches(path, name))))
            .Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (conflicts.Length > 0) return ([], [], conflicts);
        if (service is not { IsDownload: false } || game.IsApplication ||
            !paths.All(path => expected.Any(name => ExecutableMatches(path, name)))) return ([], [], []);
        var bypass = EzConnectClient.GetGameBypass(game, service.RegionCode);
        return (bypass.Ips, bypass.Domains, []);
    }

    async void OpenCatalog(object sender, RoutedEventArgs args)
    {
        if (accountCatalog is not { Games.Count: > 0 } catalog)
        {
            Report("Sign in to EZConnect to load its game and app catalog.");
            return;
        }
        var search = new TextBox { PlaceholderText = "Search games and apps" };
        AutomationProperties.SetName(search, "Search catalog");
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 300, MinWidth = 310 };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(search);
        content.Children.Add(list);
        var dialog = MakeDialog("EZConnect catalog", content, "Open");
        dialog.SecondaryButtonText = selectedGame is null ? "" : "Clear game selection";
        void Populate()
        {
            list.Items.Clear();
            foreach (var game in catalog.Games.Where(game => game.Name.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                         .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ListViewItem { Content = game.DisplayName, Tag = game };
                list.Items.Add(item);
                if (game == selectedGame) list.SelectedItem = item;
            }
            dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        }
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        search.TextChanged += (_, _) => Populate();
        Populate();
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Secondary) { selectedGame = null; Refresh(); return; }
        if (choice != ContentDialogResult.Primary || (list.SelectedItem as ListViewItem)?.Tag is not CatalogGame selected) return;
        selectedGame = selected;
        Refresh();
        await ShowGameActions(selected);
    }

    async Task ShowGameActions(CatalogGame game)
    {
        var actions = new List<(string Label, Func<Task> Run)>();
        var includeLinked = new CheckBox { Content = "Include linked apps", IsChecked = preferences.AutoAddLinkedApps };
        actions.Add(("Find running apps", async () =>
        {
            preferences = preferences with { AutoAddLinkedApps = includeLinked.IsChecked == true };
            SavePreferences();
            await PickRunningApps(preferences.AutoAddLinkedApps ? CatalogExecutables(game) : game.ExecutableNames);
        }));
        var recommended = Routes().Where(choice => choice.Service is { IsDownload: false } service &&
            service.RegionCode.Equals(game.RecommendedRegion, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!game.IsApplication && recommended.Length > 0)
        {
            actions.Add(($"Use recommended route · {game.RecommendedRegion}", () =>
            {
                route = recommended.OrderBy(item => measurements.GetValueOrDefault(item.Key) switch { { Succeeded: true } => 0, null => 1, _ => 2 })
                    .ThenBy(item => measurements.GetValueOrDefault(item.Key)?.Loss ?? float.PositiveInfinity)
                    .ThenBy(item => measurements.GetValueOrDefault(item.Key)?.Latency ?? int.MaxValue).First();
                Refresh();
                return Task.CompletedTask;
            }));
            actions.Add(($"Test recommended region · {game.RecommendedRegion}",
                async () => await RunRouteTests(await ChooseRoutes("Test recommended routes", recommended, 1))));
        }
        if (game.Servers.Count > 0) actions.Add(("Game servers and destination tests", () => PickGameServer(game)));
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MinWidth = 300 };
        foreach (var action in actions) list.Items.Add(action.Label);
        list.SelectedIndex = 0;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = string.Join(", ", game.ExecutableNames), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(includeLinked);
        panel.Children.Add(list);
        var dialog = MakeDialog(game.Name, panel, "Continue");
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedIndex >= 0)
            await actions[list.SelectedIndex].Run();
    }

    async Task PickGameServer(CatalogGame game)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, ItemsSource = game.Servers, MaxHeight = 300 };
        list.SelectedIndex = 0;
        var dialog = MakeDialog("Published game servers", list, "Test destination");
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is GameServer server)
            await ConfigureDestination(server.Address);
    }
}
