using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    System.Drawing.Icon? applicationIcon;

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

    async void OpenConnectionOptions(object sender, RoutedEventArgs args)
    {
        var actions = new List<(string Label, Func<Task> Run)>();
        if (Connected)
        {
            actions.Add(("Connection details", ShowConnectionDetails));
            actions.Add(("Reconnect", () => Reconnect()));
            actions.Add(("Probe a destination over the current Windows route", ProbeConnectedDestination));
            if (!preview) actions.Add(("Reset tunnel TCP connections", ResetTunnelTcp));
            if (watchOwnsTunnel)
                actions.Add((holdTunnel ? "Resume automatic idle disconnect" : "Hold tunnel open", () =>
                {
                    holdTunnel = !holdTunnel;
                    idleSince = null;
                    UpdateTray();
                    return Task.CompletedTask;
                }));
        }
        else if (session.NeedsCleanup) actions.Add(("Retry connection cleanup", () => ChangeConnection()));
        else
        {
            actions.Add((armed is null ? "Connect when selected apps launch" : "Stop watching for app launches", () =>
            {
                ToggleWatch();
                return Task.CompletedTask;
            }));
            actions.Add(("Mobile hotspot and console setup", OpenHotspot));
        }
        var options = new ListView { SelectionMode = ListViewSelectionMode.Single, MinWidth = 310 };
        foreach (var action in actions) options.Items.Add(action.Label);
        options.SelectedIndex = 0;
        var dialog = MakeDialog("Connection options", options, "Continue");
        options.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = options.SelectedIndex >= 0;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && options.SelectedIndex >= 0)
            await actions[options.SelectedIndex].Run();
    }

    async Task ShowConnectionDetails()
    {
        var details = new List<string> { ConnectionTitle.Text, ConnectionDetail.Text };
        if (!preview)
        {
            if (connectedEndpoint is not null) details.Add($"Endpoint · {connectedEndpoint}");
            details.Add($"Mode · {(connectedForConsole ? "Console or hotspot" : Scope.SelectedIndex == 1 ? "Entire PC" : "Selected apps")}");
            details.Add($"Uptime · {DateTimeOffset.UtcNow - connectedAt:hh\\:mm\\:ss}");
            details.AddRange(session.ActiveRoutingDetails);
            if (session.ActiveMtu is { } mtu) details.Add($"MTU · {mtu}");
            if (session.ActiveKeepalive is { } keepalive) details.Add($"Keepalive · {keepalive}");
            if (session.ActiveProxyStatus is { } proxy) details.Add($"SOCKS5 · {proxy}");
            if (session.ActiveTunnelError is uint error) details.Add($"WireSock error · 0x{error:X8}");
            if (session.State is { } sample)
            {
                details.Add(sample.TimeSinceLastHandshakeMilliseconds < 0 ? "Handshake · Not reported"
                    : $"Handshake · {sample.TimeSinceLastHandshakeMilliseconds / 1000}s ago");
                if (sample.RxBytes > 0 && float.IsFinite(sample.EstimatedLoss) && sample.EstimatedLoss is >= 0 and <= 100)
                    details.Add($"Estimated loss · {sample.EstimatedLoss:0.#}%");
            }
        }
        var text = new TextBox { Text = string.Join("\n", details), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 320 };
        await MakeDialog("Connection details", text).ShowAsync();
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
        if (route is null || Scope.SelectedIndex != 0 || checkedApps.Count == 0)
        {
            Report("Choose a route and check at least one executable in Selected apps mode.");
            return;
        }
        if (!EnsureAdministrator()) return;
        armed = new AppWatch(route.Key, new HashSet<string>(checkedApps, StringComparer.OrdinalIgnoreCase));
        lastProcessCheck = null;
        recoveryAttempted = false;
        AddActivity($"Watching {checkedApps.Count} apps for {route.Title}.");
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
        Refresh();
    }

    async Task CheckConnectionLifecycle()
    {
        if (preview || busy || closing || exitRequested || checkingProcesses) return;
        checkingProcesses = true;
        try
        {
            if (preferences.RecoverStoppedTunnel && !recoveryAttempted && session.HasTunnel && !session.TunnelIsActive)
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
