using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Microsoft.Windows.Storage.Pickers;
using RouteFinder.App;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    async void CheckMaintenanceOnLoad(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= CheckMaintenanceOnLoad;
        try
        {
            if (await WindowsNetworkTools.QueryNetworkLockAsync() == true && !closing && !session.HasTunnel)
                AddActivity("WireSock reports an active driver network lock. If no other client is connected, review it under Advanced tools.");
        }
        catch (Exception error)
        {
            if (!closing) AddActivity($"WireSock lock status unavailable. {error.Message}");
        }
        try
        {
            if (!closing && await UpdateCheck.CheckAsync() is { Available: true } update && !closing)
                AddActivity(update.Message);
        }
        catch (Exception error)
        {
            if (!closing) AddActivity($"Update check failed. {error.Message}");
        }
    }

    async void OpenAdvancedTools(object sender, RoutedEventArgs args)
    {
        var actions = new (string Label, Func<Task> Run)[]
        {
            ("WireSock setup and repair", OpenWireSockRepair),
            ("Driver network lock recovery", ResetDriverLock),
            ("Windows IPv6 adapter bindings", ConfigureIpv6),
            ("Mobile hotspot and console setup", OpenHotspot),
            ("Retry remembered EZConnect sign-in", RetryRememberedSignIn),
            ("Check for RouteFinder updates", CheckForUpdates),
            ("Configure update feed", ConfigureUpdateFeed),
            ("Window and language preferences", OpenAppearance),
            ("Clear all saved route tests", ClearSavedTests)
        };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MinWidth = 320, MaxHeight = 420 };
        foreach (var action in actions) list.Items.Add(action.Label);
        list.SelectedIndex = 0;
        var dialog = MakeDialog("Advanced tools", list, "Open");
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedIndex < 0) return;
        if (preview) { Report("Open RouteFinder outside preview mode to use system and account tools."); return; }
        try { await actions[list.SelectedIndex].Run(); }
        catch (Exception error) { Report(error.Message); }
    }

    async Task ClearSavedTests()
    {
        var dialog = MakeDialog("Clear saved route tests", new TextBlock
        {
            Text = "Clear test results for all routes on this PC?",
            TextWrapping = TextWrapping.Wrap
        }, "Clear results");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        ProfileStore.SaveMeasurements([]);
        measurements.Clear();
        AddActivity("Cleared saved route tests.");
        Refresh();
    }

    async Task ResetDriverLock()
    {
        if (busy || session.HasTunnel || session.NeedsCleanup || !EnsureAdministrator()) return;
        var dialog = MakeDialog("Reset WireSock driver lock", new TextBlock
        {
            Text = "This resets the driver-wide kill switch. Disconnect every other WireSock client first.",
            TextWrapping = TextWrapping.Wrap
        }, "Reset network lock");
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Run("Resetting WireSock network lock…", async () =>
        {
            await Task.Run(() =>
            {
                using var sdk = new WireSockTunnel();
                sdk.ResetDriverNetworkLock();
            });
            Report("WireSock reported that the driver network lock was reset.", InfoBarSeverity.Success);
        });
    }

    async Task ConfigureIpv6()
    {
        if (busy || session.HasTunnel || session.NeedsCleanup || !EnsureAdministrator()) return;
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = "Changing IPv6 bindings on active adapters can briefly interrupt network access.",
            TextWrapping = TextWrapping.Wrap
        });
        var choice = new RadioButtons { Header = "Active Windows adapters", SelectedIndex = -1 };
        choice.Items.Add("Enable IPv6");
        choice.Items.Add("Disable IPv6");
        if (preferences.Ipv6PreferenceConfigured) choice.SelectedIndex = preferences.EnableIpv6Bindings ? 0 : 1;
        body.Children.Add(choice);
        var dialog = MakeDialog("Windows IPv6 adapter bindings", body, "Apply");
        dialog.IsPrimaryButtonEnabled = choice.SelectedIndex >= 0;
        choice.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = choice.SelectedIndex >= 0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        bool enabled = choice.SelectedIndex == 0;
        await Run("Updating Windows IPv6 bindings…", async () =>
        {
            await WindowsNetworkTools.ApplyIpv6Bindings(enabled);
            preferences = preferences with { Ipv6PreferenceConfigured = true, EnableIpv6Bindings = enabled };
            SavePreferences();
            AddActivity($"IPv6 bindings {(enabled ? "enabled" : "disabled")} on active network adapters.");
        });
    }

    async Task OpenWireSockRepair()
    {
        if (busy || session.HasTunnel || session.NeedsCleanup) return;
        var packages = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        using var uninstall = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        foreach (var key in uninstall?.GetSubKeyNames() ?? [])
        {
            if (!Guid.TryParseExact(key, "B", out var product)) continue;
            using var registration = uninstall!.OpenSubKey(key);
            if (registration?.GetValue("WindowsInstaller") is 1 &&
                registration.GetValue("DisplayName") is string name &&
                name is "WireSock Secure Connect SDK" or "WireSock Kernel Drivers")
                packages.TryAdd(name, product);
        }
        var panel = new StackPanel { Spacing = 10, MinWidth = 300 };
        panel.Children.Add(new TextBlock
        {
            Text = packages.Count == 0 ? "Windows has no registered WireSock MSI package. Use the vendor download to install the SDK."
                : "Close other WireSock clients before starting the repair.",
            TextWrapping = TextWrapping.Wrap
        });
        var choice = new ComboBox { Header = "Installed package", ItemsSource = packages.Keys.ToArray(), SelectedIndex = packages.Count > 0 ? 0 : -1 };
        if (packages.Count > 0) panel.Children.Add(choice);
        panel.Children.Add(new HyperlinkButton { Content = "Official WireSock SDK download", NavigateUri = new Uri("https://wiresock.net/wiresock-sdk") });
        panel.Children.Add(new HyperlinkButton { Content = "Windows Installed apps", NavigateUri = new Uri("ms-settings:appsfeatures") });
        var dialog = MakeDialog("WireSock setup and repair", panel, packages.Count > 0 ? "Start repair" : "");
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || choice.SelectedItem is not string selected) return;
        await Run($"Repairing {selected}…", async () =>
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
            {
                UseShellExecute = true, Verb = "runas", Arguments = $"/fomus {packages[selected]:B} /norestart"
            }) ?? throw new InvalidOperationException("Windows Installer did not start.");
            await process.WaitForExitAsync();
            string message = process.ExitCode switch
            {
                0 => $"{selected} repair completed.",
                3010 or 1641 => "Repair completed. Restart Windows before connecting.",
                1602 => "The repair was canceled.",
                1618 => "Another Windows Installer operation is running. Try again when it finishes.",
                _ => $"Windows Installer repair exited with code {process.ExitCode}."
            };
            Report(message, process.ExitCode == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
        });
    }

    async Task OpenHotspot()
    {
        var downstream = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sharing = new List<string>();
        try
        {
            if (!preview)
            {
                var type = Type.GetTypeFromProgID("HNetCfg.HNetShare", throwOnError: true)!;
                dynamic manager = Activator.CreateInstance(type)!;
                foreach (object entry in manager.EnumEveryConnection)
                {
                    dynamic configuration = manager.INetSharingConfigurationForINetConnection(entry);
                    if (!configuration.SharingEnabled) continue;
                    string name = manager.NetConnectionProps(entry).Name;
                    int role = configuration.SharingConnectionType;
                    if (role == 1) downstream.Add(name);
                    sharing.Add($"{name} · {(role == 0 ? "shared upstream" : "shared downstream")}");
                }
                if (downstream.Count > 0)
                {
                    var neighbors = await WindowsNetworkTools.ReadHotspotNeighborsAsync(downstream);
                    sharing.Add(neighbors.Length == 0 ? "No recently seen IPv4 neighbors." : $"Recently seen IPv4 neighbors\n{string.Join("\n", neighbors)}");
                }
            }
        }
        catch (Exception error) { sharing.Add($"Sharing status unavailable. {error.Message}"); }
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = sharing.Count == 0 ? "No active Internet Connection Sharing configuration was reported." : string.Join("\n", sharing),
            TextWrapping = TextWrapping.Wrap
        });
        if (!preview)
            panel.Children.Add(new HyperlinkButton { Content = "Open Windows Mobile hotspot", NavigateUri = new Uri("ms-settings:network-mobilehotspot") });
        bool canConnect = !preview && !busy && !Connected && !session.NeedsCleanup && downstream.Count > 0 &&
            route?.Service is { IsDownload: false };
        if (!canConnect)
            panel.Children.Add(new TextBlock { Text = "Console routing requires a shared downstream adapter and an EZConnect game route.", TextWrapping = TextWrapping.Wrap });
        var dialog = MakeDialog("Mobile hotspot and console", panel, canConnect ? "Connect for console" : "");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && canConnect) await ChangeConnection(console: true);
    }

    async Task ConfigureUpdateFeed()
    {
        var input = new TextBox { Header = "HTTPS release manifest URL", Text = UpdateCheck.ReadFeed(), MinWidth = 300 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(input);
        panel.Children.Add(error);
        var dialog = MakeDialog("Update feed", panel, "Save");
        dialog.PrimaryButtonClick += (_, click) =>
        {
            try { UpdateCheck.SaveFeed(input.Text); }
            catch (Exception failure) { error.Text = failure.Message; click.Cancel = true; }
        };
        await dialog.ShowAsync();
    }

    async Task CheckForUpdates()
    {
        if (UpdateCheck.ReadFeed().Length == 0) { await ConfigureUpdateFeed(); return; }
        UpdateCheck.Result? result = null;
        await Run("Checking for updates…", async () => result = await UpdateCheck.CheckAsync());
        if (result is null) return;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap });
        if (result.Available && result.ReleasePage is { } page)
            content.Children.Add(new HyperlinkButton { Content = "Open release page", NavigateUri = page });
        var package = result.Available ? result.Download : null;
        var dialog = MakeDialog("RouteFinder updates", content, package is null ? "" : "Save update");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || package is null) return;
        var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = "RouteFinder-update" };
        picker.FileTypeChoices.Add("Windows executable", [".exe"]);
        if (await picker.PickSaveFileAsync() is not { } file) return;
        await Run("Downloading update…", async () =>
        {
            await UpdateCheck.DownloadAsync(package, file.Path);
            Report("Update saved and matched the release checksum.", InfoBarSeverity.Success);
        });
    }
}
