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
    async void DiagnosticsClicked(object sender, RoutedEventArgs args)
    {
        if (!busy) await ShowDiagnostics();
    }

    async void SaveReportClicked(object sender, RoutedEventArgs args)
    {
        if (!preview) await SaveReport();
    }

    void CopyActivity(object sender, RoutedEventArgs args)
    {
        if (activity.Count == 0) return;
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(string.Join(Environment.NewLine, activity));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        Report("Copied the activity log.", InfoBarSeverity.Success);
    }

    async Task ShowDiagnostics()
    {
        var modes = new ListView { SelectionMode = ListViewSelectionMode.Single, MinWidth = 280 };
        modes.Items.Add("Network and WireSock status");
        modes.Items.Add("App sockets");
        if (Connected) modes.Items.Add("Active tunnel rules");
        modes.SelectedIndex = 0;
        var picker = MakeDialog("Diagnostics", modes, "Run");
        modes.SelectionChanged += (_, _) => picker.IsPrimaryButtonEnabled = modes.SelectedIndex >= 0;
        if (await picker.ShowAsync() != ContentDialogResult.Primary) return;
        if (modes.SelectedIndex == 2)
        {
            await ShowActiveTunnelRules();
            return;
        }
        string? result = null;
        await Run("Collecting diagnostics…", async () =>
        {
            result = preview ? "Preview mode. No network probes were sent." : modes.SelectedIndex switch
            {
                1 => await Diagnostics.ReadSocketSnapshot(CancellationToken.None),
                _ => await ReadNetworkStatus()
            };
            AddActivity($"Collected {modes.SelectedItem?.ToString()?.ToLowerInvariant()}.");
        });
        if (result is null) return;
        var report = new TextBox
        {
            Text = result, IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 240, MaxHeight = 390
        };
        await MakeDialog("Diagnostic results", report).ShowAsync();
    }

    async Task ShowActiveTunnelRules()
    {
        var rules = session.ActiveRoutingDetails.Append(session.ActiveTunnelError is uint error
            ? $"WireSock error   0x{error:X8}" : "WireSock error   No code reported").ToArray();
        var rows = new StackPanel { Spacing = 8 };
        foreach (string rule in rules)
            rows.Children.Add(new TextBlock { Text = rule, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var dialog = MakeDialog("Active tunnel rules", new ScrollViewer
        {
            Content = rows, MinWidth = 320, MaxHeight = 390
        }, "Copy rules");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(string.Join(Environment.NewLine, rules));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            Report("Copied the active tunnel rules.", InfoBarSeverity.Success);
        }
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

    void OpenLogsClicked(object sender, RoutedEventArgs args)
    {
        if (preview) return;
        try
        {
            Directory.CreateDirectory(LogFolder);
            DiagnosticLog.Flush(500);
            string target = DiagnosticLog.CurrentFile is { } file && File.Exists(file) ? $"/select,\"{file}\"" : $"\"{LogFolder}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch (Exception error) { Report(error.Message); }
    }

    async Task SaveReport()
    {
        try
        {
            var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = "RouteFinder-support" };
            picker.FileTypeChoices.Add("Text report", [".txt"]);
            if (await picker.PickSaveFileAsync() is not { } file) return;
            lastSetupDump = "";
            LogSetupIfChanged("Support report");
            var log = DiagnosticLog.Tail(4000);
            await File.WriteAllLinesAsync(file.Path, SupportSnapshot()
                .Append("").Append($"--- Detailed log ({DiagnosticLog.CurrentFile ?? "off"}), last {log.Length} lines ---").Concat(log));
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
        yield return $"Apps through VPN: {VpnApps().Count()} of {apps.Count}";
        if (session.HasTunnel)
        {
            yield return $"Mode {(connectedForConsole ? "Console" : Scope.SelectedIndex == 1 ? "Entire PC" : BlocksOtherApps ? "Selected apps only" : "Selected apps")} · Backend {(connectedForConsole || preferences.PortableEngine ? "virtual adapter" : "transparent")}";
            if (session.ActiveTunnelError is uint error)
                yield return $"WireSock error: 0x{error:X8}";
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
