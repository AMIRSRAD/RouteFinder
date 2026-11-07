using System.Net.NetworkInformation;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using RouteFinder.App;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    async void OpenActivity(object sender, RoutedEventArgs args)
    {
        var log = new TextBox
        {
            Text = activity.Count == 0 ? "Connection events will appear here." : string.Join("\n\n", activity),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 170, MaxHeight = 320
        };
        var dialog = MakeDialog("Activity", log, "Diagnostics");
        dialog.SecondaryButtonText = "Save report";
        dialog.CloseButtonText = "Close";
        dialog.IsPrimaryButtonEnabled = !busy && !session.NeedsCleanup;
        dialog.IsSecondaryButtonEnabled = !preview;
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                await ShowDiagnostics();
                break;
            case ContentDialogResult.Secondary:
                await SaveReport();
                break;
        }
    }

    async Task ShowDiagnostics()
    {
        var modes = new ListView { SelectionMode = ListViewSelectionMode.Single, MinWidth = 280 };
        modes.Items.Add("Network and WireSock status");
        modes.Items.Add("App sockets");
        modes.SelectedIndex = 0;
        var picker = MakeDialog("Diagnostics", modes, "Run");
        modes.SelectionChanged += (_, _) => picker.IsPrimaryButtonEnabled = modes.SelectedIndex >= 0;
        if (await picker.ShowAsync() != ContentDialogResult.Primary) return;
        string? result = null;
        await Run("Collecting diagnostics…", async () =>
        {
            result = preview ? "Preview mode. No network probes were sent."
                : modes.SelectedIndex == 1 ? await Diagnostics.ReadSocketSnapshot(CancellationToken.None)
                : await ReadNetworkStatus();
            AddActivity(modes.SelectedIndex == 1 ? "Collected app sockets." : "Collected network diagnostics.");
        });
        if (result is null) return;
        var report = new TextBox
        {
            Text = result, IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 240, MaxHeight = 390
        };
        await MakeDialog("Diagnostic results", report).ShowAsync();
    }

    static async Task<string> ReadNetworkStatus()
    {
        var snapshot = await Task.Run(() =>
        {
            var lines = new List<string> { $"Windows {Environment.OSVersion.Version}" };
            lines.AddRange(WindowsNetworkTools.ReadWireSockStatus());
            lines.AddRange(AdapterSummary(includeNames: true));
            return string.Join("\n", lines);
        });
        string lockStatus;
        try
        {
            lockStatus = await WindowsNetworkTools.QueryNetworkLockAsync() switch
            {
                true => "Driver network lock is active.",
                false => "Driver network lock is inactive.",
                null => "Driver network lock status is unavailable."
            };
        }
        catch (Exception error) { lockStatus = $"Driver network lock status is unavailable. {error.Message}"; }
        return $"{snapshot}\n{lockStatus}\n\n" + (await Diagnostics.MeasurePing("1.1.1.1", count: 8, intervalMilliseconds: 150)).Detail;
    }

    async Task SaveReport()
    {
        try
        {
            var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = "RouteFinder-support" };
            picker.FileTypeChoices.Add("Text report", [".txt"]);
            if (await picker.PickSaveFileAsync() is not { } file) return;
            await File.WriteAllLinesAsync(file.Path, SupportSnapshot());
            AddActivity("Saved a support report.");
        }
        catch (Exception error) { Report(error.Message); }
    }

    IEnumerable<string> SupportSnapshot()
    {
        yield return $"RouteFinder {typeof(MainWindow).Assembly.GetName().Version} · {DateTimeOffset.Now:O}";
        yield return $"Windows {Environment.OSVersion.Version} · x64";
        yield return $"EZConnect account {(session.SignedIn ? "signed in" : "signed out")}";
        string sdkVersion;
        try { sdkVersion = FileVersionInfo.GetVersionInfo(WireSockTunnel.FindSdkPath()).FileVersion ?? "unknown"; }
        catch (Exception error) { sdkVersion = $"unavailable ({error.GetType().Name})"; }
        yield return $"WireSock SDK {sdkVersion}";
        yield return $"Route source: {route?.Source ?? "None"}";
        yield return $"Tunnel {(session.HasTunnel ? "running" : "stopped")} · Cleanup pending {session.NeedsCleanup && !session.HasTunnel}";
        yield return $"Selected apps: {checkedApps.Count}";
        if (session.HasTunnel)
        {
            yield return $"Mode {(connectedForConsole ? "Console" : Scope.SelectedIndex == 1 ? "Entire PC" : "Selected apps")} · Backend {(connectedForConsole || preferences.PortableEngine ? "virtual adapter" : "transparent")}";
            if (session.State is { } stats)
                yield return $"Handshake age {stats.TimeSinceLastHandshakeMilliseconds} ms · RX {stats.RxBytes} bytes · TX {stats.TxBytes} bytes";
        }
        foreach (var line in AdapterSummary(includeNames: false)) yield return line;
    }

    static IEnumerable<string> AdapterSummary(bool includeNames)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            string name = includeNames ? $"{adapter.Name} · " : "";
            yield return $"{name}{adapter.NetworkInterfaceType} · {adapter.OperationalStatus} · IPv6 {(adapter.Supports(NetworkInterfaceComponent.IPv6) ? "available" : "unavailable")}";
        }
    }
}
