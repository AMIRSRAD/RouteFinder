using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using RouteFinder.App;
using RouteFinder.Core;
using RouteFinder.Service;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    readonly Dictionary<string, RouteTestResult> measurements = new(StringComparer.Ordinal);
    CancellationTokenSource? activeTest;
    long serviceCooldownStarted;
    TimeSpan serviceCooldown;

    static string FormatTest(RouteTestResult result) => result.ProfileUnavailable ? "Route unavailable" :
        !result.Succeeded ? "No peer response" :
        $"{(result.Latency is int latency ? $"{latency} ms" : "Peer responded")}" +
        (result.Loss is float loss ? $" · {loss:0.#}% est. loss" : "");

    async void TestSelectedRoute(object sender, RoutedEventArgs args)
    {
        if (activeTest is not null)
        {
            CancelActiveTest();
            return;
        }
        if (route is { } selected) await RunRouteTests([selected]);
    }

    void CancelActiveTest()
    {
        if (activeTest is not { IsCancellationRequested: false } test) return;
        test.Cancel();
        ConnectionTitle.Text = "Canceling test…";
        ConnectionDetail.Text = "Finishing any pending EZConnect request and releasing the test tunnel.";
        Refresh();
    }

    async void CompareRoutes(object sender, RoutedEventArgs args)
        => await RunRouteTests(await ChooseRoutes("Compare routes", Routes(), 2));

    async void TestAllRoutes(object sender, RoutedEventArgs args)
    {
        if (busy || Connected || session.NeedsCleanup) return;
        var choices = Routes().ToArray();
        if (choices.Length == 0) return;
        var details = new TextBlock
        {
            Text = $"Test all {choices.Length} available routes one at a time? EZConnect cooldowns can make a large batch take several minutes. You can cancel while it runs.",
            TextWrapping = TextWrapping.Wrap
        };
        var dialog = MakeDialog("Test all routes", details, "Start tests");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await RunRouteTests(choices);
    }

    RouteChoice[] FilteredRouteChoices()
    {
        var library = Routes().DistinctBy(choice => choice.Key, StringComparer.Ordinal)
            .ToDictionary(choice => choice.Key, StringComparer.Ordinal);
        return RouteList.Items.OfType<RoutePickerRow>().Where(row => library.ContainsKey(row.Key))
            .Select(row => library[row.Key]).ToArray();
    }

    async void TestShortlist(object sender, RoutedEventArgs args)
    {
        var choices = FilteredRouteChoices();
        if (busy || Connected || session.NeedsCleanup || choices.Length == 0) return;
        var dialog = MakeDialog("Test filtered routes", new TextBlock
        {
            Text = $"Test the {choices.Length} routes shown in the library? Search, region, source, and favorites filters all apply.",
            TextWrapping = TextWrapping.Wrap
        }, "Start tests");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await RunRouteTests(choices);
    }

    async Task<RouteChoice[]> ChooseRoutes(string title, IEnumerable<RouteChoice> choices, int minimum)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 340, MinWidth = 300 };
        foreach (var choice in choices)
        {
            var item = new ListViewItem { Content = $"{choice.Title} · {choice.Source} · {choice.Detail}", Tag = choice };
            list.Items.Add(item);
            if (choice.Key == route?.Key) list.SelectedItems.Add(item);
        }
        var dialog = MakeDialog(title, list, "Run tests");
        dialog.IsPrimaryButtonEnabled = list.SelectedItems.Count >= minimum;
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItems.Count >= minimum;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return [];
        return list.Items.OfType<ListViewItem>().Where(list.SelectedItems.Contains)
            .Select(item => (RouteChoice)item.Tag).ToArray();
    }

    async Task RunRouteTests(RouteChoice[] choices)
    {
        if (directRouting is not null)
        {
            Report("Stop Windows routing before running VPN route tests.", InfoBarSeverity.Informational);
            return;
        }
        var summaries = await CollectRouteTests(choices);
        if (exitRequested || summaries.Count == 0) return;
        if (choices.Length == 1 && measurements.TryGetValue(choices[0].Key, out var result) &&
            summaries.Count == 1 && summaries[0].StartsWith($"{choices[0].Title} · {FormatTest(result)}", StringComparison.Ordinal))
            return;
        var results = new TextBox { Text = string.Join("\n\n", summaries), TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true, IsReadOnly = true, MinHeight = 140, MaxHeight = 360 };
        var dialog = MakeDialog("Route test results", results, preview ? "" : "Export results");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ExportTestReport("RouteFinder-route-tests", results.Text);
    }

    async Task<List<string>> CollectRouteTests(RouteChoice[] choices)
    {
        if (choices.Length == 0 || busy || Connected || session.NeedsCleanup || (!preview && !EnsureAdministrator())) return [];
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        activeTest = cancellation;
        var summaries = new List<string>();
        try
        {
            await Run("Testing routes…", async () =>
            {
                bool measurementsChanged = false;
                try
                {
                    foreach (var choice in choices)
                    {
                        token.ThrowIfCancellationRequested();
                        ConnectionDetail.Text = $"{summaries.Count + 1} of {choices.Length} · {choice.Title}";
                        try
                        {
                            var result = await MeasureRoute(choice, token);
                            measurements[choice.Key] = result with { TestedAt = DateTimeOffset.UtcNow };
                            measurementsChanged = true;
                            var details = FormatTest(result);
                            if (result.TcpReached is bool reached)
                                details += reached ? " · TCP reached port 443; path unverified" : " · TCP probe did not connect";
                            Record($"{choice.Title} · {details}");
                        }
                        catch (EzConnectServiceException error) when (error.StatusCode == 429)
                        {
                            Record($"Stopped at {choice.Title} · {error.Message} Completed results were kept.");
                            break;
                        }
                        catch (Exception error) when (error is not OperationCanceledException && !session.NeedsCleanup)
                        {
                            Record($"{choice.Title} · {error.Message}");
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Record("Tests canceled.");
                }
                finally
                {
                    if (measurementsChanged && !preview && !unavailableStores.Contains("test results"))
                        await Task.Run(() => ProfileStore.SaveMeasurements(measurements));
                }
            });
        }
        finally
        {
            activeTest = null;
            Refresh();
        }
        return summaries;

        void Record(string message) { summaries.Add(message); AddActivity(message); }
    }

    async Task ExportTestReport(string name, string text)
    {
        try
        {
            var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = name };
            picker.FileTypeChoices.Add("Text report", [".txt"]);
            if (await picker.PickSaveFileAsync() is { } file) await File.WriteAllTextAsync(file.Path, text);
        }
        catch (Exception error) { Report(error.Message); }
    }

    async Task<RouteTestResult> MeasureRoute(RouteChoice choice, CancellationToken cancellation)
    {
        if (preview)
        {
            await Task.Delay(350, cancellation);
            return new RouteTestResult("Preview", true, choice.Service is null ? 42 : 54, 3, Loss: 0);
        }
        var local = choice.Profile is { } profile ? TunnelProfile.Parse(profile.Configuration, profile.Name) : null;
        return await RunPacedProbe(choice, () => session.TestAsync(choice.Service, local, cancellation,
            preferences.PortableEngine, preferences.DnsOverride, preferences.AutoMtu, preferences.TunnelMtu,
            preferences.KeepaliveSeconds, preferences.RouteTestSeconds, ConfiguredProxy, UplinkFor(choice)), cancellation);
    }

    async Task<T> RunPacedProbe<T>(RouteChoice choice, Func<Task<T>> probe, CancellationToken cancellation)
    {
        if (choice.Service is null) return await probe();

        for (int attempt = 0; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            var remaining = serviceCooldown - Stopwatch.GetElapsedTime(serviceCooldownStarted);
            if (remaining > TimeSpan.FromMinutes(5))
                throw new EzConnectServiceException($"EZConnect cooldown has {Math.Ceiling(remaining.TotalMinutes)} minutes remaining.", 429);
            if (remaining > TimeSpan.Zero)
            {
                string progress = ConnectionDetail.Text;
                ConnectionDetail.Text = $"{progress} · EZConnect cooldown: {Math.Ceiling(remaining.TotalSeconds)} s";
                try { await Task.Delay(remaining, cancellation); }
                finally { if (!cancellation.IsCancellationRequested) ConnectionDetail.Text = progress; }
            }

            try { return await probe(); }
            catch (EzConnectServiceException error) when (error.StatusCode == 429)
            {
                serviceCooldownStarted = Stopwatch.GetTimestamp();
                var retry = error.RetryAfter ?? TimeSpan.FromSeconds(30);
                serviceCooldown = retry > TimeSpan.Zero ? retry : TimeSpan.Zero;
                if (attempt > 0 || session.NeedsCleanup || serviceCooldown > TimeSpan.FromMinutes(5)) throw;
            }
        }
    }
}
