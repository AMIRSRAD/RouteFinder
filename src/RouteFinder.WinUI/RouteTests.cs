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

    static string FormatTest(RouteTestResult result) => result.ProfileUnavailable ? "Route unavailable" :
        !result.Succeeded ? "No peer response" :
        $"{(result.Latency is int latency ? $"{latency} ms" : "Peer responded")}" +
        (result.Loss is float loss ? $" · {loss:0.#}% est. loss" : "") +
        (result.Jitter is float jitter ? $" · {jitter:0.#} ms jitter" : "");

    async void TestSelectedRoute(object sender, RoutedEventArgs args)
    {
        if (activeTest is { } test)
        {
            test.Cancel();
            Refresh();
            return;
        }
        if (route is { } selected) await RunRouteTests([selected]);
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
        var summaries = await CollectRouteTests(choices);
        if (exitRequested || summaries.Count == 0) return;
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
                long? lastServiceAttempt = null;
                try
                {
                    foreach (var choice in choices)
                    {
                        token.ThrowIfCancellationRequested();
                        ConnectionDetail.Text = $"{summaries.Count + 1} of {choices.Length} · {choice.Title}";
                        try
                        {
                            if (!preview && choice.Service is not null && lastServiceAttempt is long previous)
                            {
                                var delay = TimeSpan.FromSeconds(10) - Stopwatch.GetElapsedTime(previous);
                                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                            }
                            var result = await MeasureRoute(choice, token);
                            measurements[choice.Key] = result with { TestedAt = DateTimeOffset.UtcNow };
                            if (!preview) ProfileStore.SaveMeasurements(measurements);
                            var details = FormatTest(result);
                            if (result.TcpReached is bool reached)
                                details += reached ? " · TCP reached port 443; path unverified" : " · TCP probe did not connect";
                            Record($"{choice.Title} · {details}");
                        }
                        catch (Exception error) when (error is not OperationCanceledException && !session.NeedsCleanup)
                        {
                            Record($"{choice.Title} · {error.Message}");
                        }
                        finally
                        {
                            if (choice.Service is not null) lastServiceAttempt = Stopwatch.GetTimestamp();
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Record("Tests canceled.");
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
            return new RouteTestResult("Preview", true, choice.Service is null ? 42 : 54, 3, Loss: 0, Jitter: 2);
        }
        var local = choice.Profile is { } profile ? TunnelProfile.Parse(profile.Configuration, profile.Name) : null;
        try
        {
            return await session.TestAsync(choice.Service, local, cancellation, preferences.PortableEngine,
                preferences.DnsOverride, preferences.AutoMtu, preferences.TunnelMtu, preferences.KeepaliveSeconds,
                preferences.RouteTestSeconds, ConfiguredProxy);
        }
        catch (EzConnectServiceException error) when (error.StatusCode == 429 && !session.NeedsCleanup &&
            (error.RetryAfter ?? TimeSpan.FromSeconds(30)) <= TimeSpan.FromMinutes(5))
        {
            ConnectionDetail.Text = $"{choice.Title} · Waiting for EZConnect cooldown";
            await Task.Delay(error.RetryAfter ?? TimeSpan.FromSeconds(30), cancellation);
            return await session.TestAsync(choice.Service, local, cancellation, preferences.PortableEngine,
                preferences.DnsOverride, preferences.AutoMtu, preferences.TunnelMtu, preferences.KeepaliveSeconds,
                preferences.RouteTestSeconds, ConfiguredProxy);
        }
    }
}
