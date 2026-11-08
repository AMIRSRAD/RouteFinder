using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Forms = System.Windows.Forms;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    sealed record AppWatch(string RouteKey, HashSet<string> Paths);
    AppWatch? armed;
    bool watchOwnsTunnel;
    bool holdTunnel;
    bool checkingProcesses;
    bool recoveryAttempted;
    bool connectedForConsole;
    bool hiddenInTray;
    long? lastProcessCheck;
    long? idleSince;
    Forms.NotifyIcon? tray;
    Forms.ContextMenuStrip? trayMenu;
    Forms.ToolStripItem? trayDisconnect;
    Forms.ToolStripItem? trayReconnect;
    readonly Queue<int> peerRttHistory = new();
    long? lastRttSample;
    System.Drawing.Icon? applicationIcon;

    bool WindowCanRenderTelemetry => !hiddenInTray &&
        AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    void UpdatePolling()
    {
        bool visibleTelemetry = !closing && session.HasTunnel && WindowCanRenderTelemetry;
        bool backgroundWork = armed is not null || session.HasSelectedUplink ||
            preferences.RecoverStoppedTunnel && session.HasTunnel && !recoveryAttempted;
        if (preview || busy || closing || exitRequested || !visibleTelemetry && !backgroundWork)
        {
            if (timer.IsEnabled) timer.Stop();
            return;
        }

        var interval = TimeSpan.FromSeconds(visibleTelemetry ? 1 : 3);
        if (timer.IsEnabled && timer.Interval == interval) return;
        if (timer.IsEnabled) timer.Stop();
        timer.Interval = interval;
        timer.Start();
    }

    void InitializeTray()
    {
        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Show RouteFinder", null, (_, _) => DispatcherQueue.TryEnqueue(RestoreWindow));
        trayDisconnect = trayMenu.Items.Add("Disconnect", null, (_, _) => DispatcherQueue.TryEnqueue(async () =>
        {
            if (session.HasTunnel || session.NeedsCleanup) await ChangeConnection();
        }));
        trayReconnect = trayMenu.Items.Add("Reconnect", null, (_, _) => DispatcherQueue.TryEnqueue(async () => await Reconnect()));
        trayMenu.Items.Add("Exit", null, (_, _) => DispatcherQueue.TryEnqueue(async () =>
        {
            RestoreWindow();
            await RequestExit();
        }));
        if (Environment.ProcessPath is { } executable)
            applicationIcon = System.Drawing.Icon.ExtractAssociatedIcon(executable);
        if (applicationIcon is not null)
            AppWindow.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(applicationIcon.Handle));
        tray = new Forms.NotifyIcon
        {
            Icon = applicationIcon ?? System.Drawing.SystemIcons.Application,
            Text = "RouteFinder",
            ContextMenuStrip = trayMenu
        };
        tray.DoubleClick += (_, _) => DispatcherQueue.TryEnqueue(RestoreWindow);
        AppWindow.Changed += (_, change) =>
        {
            if (change.DidSizeChange && preferences.MinimizeToTray && (session.HasTunnel || armed is not null) &&
                !hiddenInTray && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            {
                hiddenInTray = true;
                UpdateTray();
                AppWindow.Hide();
            }
            if (change.DidSizeChange || change.DidVisibilityChange) UpdatePolling();
        };
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            UpdatePolling();
        };
    }

    internal void RestoreWindow()
    {
        if (closing) return;
        hiddenInTray = false;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        UpdateStatus();
        UpdatePolling();
        UpdateTray();
    }

    void UpdateTray()
    {
        if (tray is null) return;
        tray.Visible = hiddenInTray || armed is not null || preferences.MinimizeToTray && session.HasTunnel;
        tray.Text = holdTunnel ? "RouteFinder · Tunnel held open"
            : armed is { } watch ? $"RouteFinder · Watching {watch.Paths.Count} apps" : "RouteFinder";
        trayDisconnect!.Enabled = !busy && (session.HasTunnel || session.NeedsCleanup);
        trayDisconnect.Text = session.HasTunnel ? "Disconnect" : "Retry cleanup";
        trayReconnect!.Enabled = !busy && session.HasTunnel;
    }

    // The actions live in the button's flyout; refresh them each time it opens.
    void OpenConnectionOptions(object sender, RoutedEventArgs args) => RefreshConnectionActions();

    void RefreshConnectionActions()
    {
        bool connected = Connected;
        bool available = !connected && !session.NeedsCleanup;
        ReconnectInline.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        HoldInline.Visibility = connected && watchOwnsTunnel ? Visibility.Visible : Visibility.Collapsed;
        ProbeInline.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        ResetInline.Visibility = connected && !preview ? Visibility.Visible : Visibility.Collapsed;
        WatchInline.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        HotspotInline.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        WatchInline.Content = armed is null ? "Watch apps" : "Stop watching";
        HoldInline.Content = holdTunnel ? "Resume idle disconnect" : "Hold open";
        InlineConnectionActions.IsHitTestVisible = !busy;
        foreach (var action in InlineConnectionActions.Children.OfType<Button>()) action.IsEnabled = !busy;
    }

    void RenderTelemetry(int? rtt, float? loss, ulong received, ulong sent, string? rate, string peer)
    {
        LiveRtt.Text = rtt is int ms ? $"{ms} ms" : "—";
        RecordRtt(rtt, Stopwatch.GetTimestamp());
        LiveLoss.Text = loss is float percent ? $"{percent:0.#}%" : "—";
        LiveUsage.Text = Connected ? $"↓ {FormatBytes(received)}  ↑ {FormatBytes(sent)}" +
            (rate is null ? "" : $" · {rate}") : "Connect to see tunnel traffic.";
        LivePeer.Text = peer;
        LivePeer.Visibility = peer.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(LivePeer, peer);
    }

    void RecordRtt(int? rtt, long now)
    {
        if (rtt is not int ms)
        {
            peerRttHistory.Clear();
            lastRttSample = null;
            LiveVariation.Text = "—";
            LatencyTrend.Visibility = Visibility.Collapsed;
            return;
        }
        if (lastRttSample is long previous && Stopwatch.GetElapsedTime(previous, now).TotalSeconds < 0.9) return;
        lastRttSample = now;
        peerRttHistory.Enqueue(ms);
        if (peerRttHistory.Count > 30) peerRttHistory.Dequeue();
        if (peerRttHistory.Count < 2) return;

        int minimum = int.MaxValue;
        int maximum = int.MinValue;
        int? previousRtt = null;
        double totalVariation = 0;
        foreach (int value in peerRttHistory)
        {
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            if (previousRtt is int prior) totalVariation += Math.Abs((double)value - prior);
            previousRtt = value;
        }
        int segments = peerRttHistory.Count - 1;
        double variation = totalVariation / segments;
        LiveVariation.Text = $"{variation:0.#} ms";
        double range = Math.Max(5.0, maximum - (double)minimum);
        if (minimum == maximum && LiveVariation.Text == "0 ms" && LatencyTrend.Points?.Count == peerRttHistory.Count &&
            LatencyTrend.Points.All(point => point.Y == 18)) return;
        var points = new PointCollection();
        int index = 0;
        foreach (int value in peerRttHistory)
        {
            points.Add(new Point(2 + index * 90.0 / segments, 18 - (value - minimum) * 16 / range));
            index++;
        }
        LatencyTrend.Points = points;
        LatencyTrend.Visibility = Visibility.Visible;
    }

    async void ReconnectFromPanel(object sender, RoutedEventArgs args) => await Reconnect();
    async void HotspotFromPanel(object sender, RoutedEventArgs args) => await OpenHotspot();
    async void ResetFromPanel(object sender, RoutedEventArgs args) => await ResetTunnelTcp();
    async void ProbeFromPanel(object sender, RoutedEventArgs args) => await ProbeConnectedDestination();
    void WatchFromPanel(object sender, RoutedEventArgs args) => ToggleWatch();

    void HoldFromPanel(object sender, RoutedEventArgs args)
    {
        if (busy || !watchOwnsTunnel) return;
        holdTunnel = !holdTunnel;
        idleSince = null;
        Refresh();
    }

    async Task ResetTunnelTcp()
    {
        if (busy || !session.HasTunnel) return;
        var dialog = MakeDialog("Reset tunnel TCP connections", new TextBlock
        {
            Text = "This drops active TCP connections through the tunnel. Apps may need to reconnect.",
            TextWrapping = TextWrapping.Wrap
        }, "Reset TCP connections");
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Run("Resetting tunnel TCP connections…", async () =>
        {
            await Task.Run(session.ResetTunnelTcpConnections);
            Report("WireSock reset TCP connections through the active tunnel.", InfoBarSeverity.Success);
        });
    }

    async Task ProbeConnectedDestination()
    {
        var host = new TextBox { Header = "Destination IP address", MinWidth = 290 };
        var port = new TextBox { Header = "TCP port", Text = "443" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(host);
        panel.Children.Add(port);
        panel.Children.Add(new TextBlock
        {
            Text = "These probes use the current Windows route. The reply path through the tunnel cannot be verified.",
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = MakeDialog("Probe destination", panel, "TCP connect");
        dialog.SecondaryButtonText = "ICMP ping";
        void Validate()
        {
            bool valid = System.Net.IPAddress.TryParse(host.Text.Trim(), out var address) && IsProbeAddress(address);
            dialog.IsSecondaryButtonEnabled = valid;
            dialog.IsPrimaryButtonEnabled = valid && int.TryParse(port.Text, out var value) && value is >= 1 and <= 65535;
        }
        host.TextChanged += (_, _) => Validate();
        port.TextChanged += (_, _) => Validate();
        Validate();
        var action = await dialog.ShowAsync();
        if (action == ContentDialogResult.None || !Connected) return;
        string? result = null;
        await Run("Probing the Windows route…", async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string target = host.Text.Trim();
            result = preview ? "Simulated result" : action == ContentDialogResult.Primary
                ? await RouteFinder.App.Diagnostics.TcpProbe(target, int.Parse(port.Text), deadline.Token)
                : await RouteFinder.App.Diagnostics.Probe(target, cancellation: deadline.Token);
        });
        if (result is not null)
            await MakeDialog("Windows probe · Tunnel path unverified", new TextBlock { Text = result, TextWrapping = TextWrapping.Wrap }).ShowAsync();
    }

    void ToggleWatch()
    {
        if (armed is not null) { CancelWatch(); Refresh(); return; }
        if (busy || Connected || session.NeedsCleanup || preview) return;
        if (route is null || Scope.SelectedIndex == 1 || !VpnApps().Any())
        {
            Report("Choose a route and check at least one executable in Selected apps mode.");
            return;
        }
        if (!EnsureAdministrator()) return;
        armed = new AppWatch(route.Key, new HashSet<string>(VpnApps(), StringComparer.OrdinalIgnoreCase));
        lastProcessCheck = null;
        recoveryAttempted = false;
        AddActivity($"Watching {VpnApps().Count()} apps for {route.Title}.");
        Refresh();
    }

    void CancelWatch()
    {
        if (armed is null) return;
        armed = null;
        watchOwnsTunnel = holdTunnel = false;
        idleSince = null;
        AddActivity("Stopped watching for app launches.");
    }

    async Task Reconnect(bool automatic = false)
    {
        if (busy || !Connected || route is null) return;
        if (preview) { AddActivity("Simulated reconnect."); return; }
        bool console = connectedForConsole;
        bool wasWatched = watchOwnsTunnel;
        await Run("Releasing the current tunnel…", () => session.DisconnectAsync());
        if (!session.HasTunnel && !session.NeedsCleanup)
        {
            await ChangeConnection(automatic: true, console: console);
            watchOwnsTunnel = wasWatched && session.HasTunnel;
            if (wasWatched && !watchOwnsTunnel) CancelWatch();
        }
        if (!automatic) recoveryAttempted = false;
        reconnectFailed = !session.HasTunnel;
        if (reconnectFailed) AddActivity("The reconnect did not restore a tunnel.");
        Refresh();
    }

    async Task CheckConnectionLifecycle()
    {
        if (preview || busy || closing || exitRequested || checkingProcesses) return;
        checkingProcesses = true;
        try
        {
            if (!session.UplinkIsCurrent)
            {
                CancelWatch();
                await Run("Disconnecting…", async () =>
                {
                    if (appRouterVpn) await StopAppRouter();
                    else await session.DisconnectAsync();
                    Report("Disconnected because Windows stopped routing the VPN endpoint through the selected internet adapter.",
                        InfoBarSeverity.Warning);
                });
                return;
            }
            if (!appRouterVpn && preferences.RecoverStoppedTunnel && !recoveryAttempted && session.HasTunnel && !session.TunnelIsActive)
            {
                recoveryAttempted = true;
                await Reconnect(automatic: true);
                return;
            }
            if (armed is not { } watch ||
                lastProcessCheck is long previous && Stopwatch.GetElapsedTime(previous) < TimeSpan.FromSeconds(3)) return;
            lastProcessCheck = Stopwatch.GetTimestamp();
            var (running, uncertain) = await Task.Run(() => FindWatchedProcess(watch.Paths));
            if (armed != watch || busy || closing) return;
            if (running || uncertain) idleSince = null;
            if (running && !watchOwnsTunnel && !session.HasTunnel && !session.NeedsCleanup)
            {
                await ChangeConnection(automatic: true);
                watchOwnsTunnel = session.HasTunnel;
                if (!watchOwnsTunnel) CancelWatch();
                Refresh();
                return;
            }
            if (running || uncertain || !watchOwnsTunnel || holdTunnel) return;
            idleSince ??= Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(idleSince.Value) < TimeSpan.FromSeconds(Math.Clamp(preferences.OnDemandIdleSeconds, 5, 3600))) return;
            await Run("Releasing idle tunnel…", async () =>
            {
                try { await session.DisconnectAsync(); AddActivity("Watched apps exited. Waiting for the next launch."); }
                finally
                {
                    idleSince = null;
                    watchOwnsTunnel = session.HasTunnel;
                    if (session.NeedsCleanup && !session.HasTunnel) CancelWatch();
                }
            });
        }
        catch (Exception error)
        {
            CancelWatch();
            Report(error.Message);
            Refresh();
        }
        finally { checkingProcesses = false; }
    }

    static (bool Running, bool Uncertain) FindWatchedProcess(IEnumerable<string> paths)
    {
        bool running = false;
        bool uncertain = false;
        foreach (string path in paths)
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
            {
                using (process)
                {
                    try { running |= string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase); }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or NotSupportedException) { uncertain = true; }
                    catch (InvalidOperationException) { }
                }
            }
            if (running) break;
        }
        return (running, uncertain);
    }
}
