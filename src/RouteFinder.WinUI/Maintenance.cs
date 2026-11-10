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
                AddActivity("WireSock reports an active driver network lock. If no other client is connected, review it under Settings > Maintenance.");
        }
        catch (Exception error)
        {
            if (!closing) AddActivity($"WireSock lock status unavailable. {error.Message}");
        }
        try
        {
            if (!closing && await UpdateCheck.CheckAsync(useGitHubDefault: true) is { Available: true } update && !closing)
            {
                if (Notice.IsOpen || busy) { AddActivity(update.Message); return; }
                Report(update.Message, InfoBarSeverity.Informational);
                var review = new Button { Content = "Review update" };
                review.Click += async (_, _) => { if (!busy && !closing) await ShowUpdate(update); };
                Notice.ActionButton = review;
            }
        }
        catch (Exception error)
        {
            if (!closing) AddActivity($"Update check failed. {error.Message}");
        }
    }

    (string Glyph, string Label, string Description, Func<Task> Run)[] MaintenanceActions() =>
    [
        ("\uE895", "Check for updates", "Download and install the latest RouteFinder release.", CheckForUpdates),
        ("\uE774", "Update feed", "Choose where RouteFinder looks for releases.", ConfigureUpdateFeed),
        ("\uE90F", "WireSock setup and repair", "Repair the installed WireSock SDK or drivers.", OpenWireSockRepair),
        ("\uE72E", "Driver network lock recovery", "Reset a kill switch left on by another WireSock client.", ResetDriverLock)
    ];

    async void ConfigureIpv6Clicked(object sender, RoutedEventArgs args) => await RunMaintenance(ConfigureIpv6);
    async void ClearTestsClicked(object sender, RoutedEventArgs args) => await RunMaintenance(ClearSavedTests);

    async Task RunMaintenance(Func<Task> action)
    {
        if (busy) return;
        if (preview) { Report("Open RouteFinder outside preview mode to use system and account tools."); return; }
        try { await action(); }
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
        WriteStore("test results", () => ProfileStore.SaveMeasurements([]));
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
        var input = new TextBox { Header = "HTTPS release manifest URL (blank disables updates)", Text = UpdateCheck.ReadFeed(true), MinWidth = 300 };
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
        if (UpdateCheck.ReadFeed(true).Length == 0) { await ConfigureUpdateFeed(); return; }
        UpdateCheck.Result? result = null;
        await Run("Checking for updates…", async () => result = await UpdateCheck.CheckAsync(useGitHubDefault: true));
        if (result is null) return;
        await ShowUpdate(result);
    }

    async Task ShowUpdate(UpdateCheck.Result result)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap });
        if (result.Available && result.ReleasePage is { } page)
            content.Children.Add(new HyperlinkButton { Content = "Open release page", NavigateUri = page });
        var package = result.Available ? result.Download : null;
        bool install = package is not null && UpdateCheck.ReadFeed(true) == UpdateCheck.OfficialFeed &&
            UpdateCheck.IsOfficialInstaller(package);
        if (install)
            content.Children.Add(new TextBlock { Text = "RouteFinder will close and open the verified GitHub installer.",
                TextWrapping = TextWrapping.Wrap });
        var dialog = MakeDialog("RouteFinder updates", content, package is null ? "" : install ? "Download and install" : "Save update");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || package is null) return;
        if (install)
        {
            if (busy || session.HasTunnel || session.NeedsCleanup)
            {
                Report("Stop routing and finish pending connection cleanup before installing the update.");
                return;
            }
            string? verified = null;
            await Run("Downloading update…", async () =>
            {
                string folder = Path.Combine(ProfileStore.DirectoryPath, "updates", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "RouteFinderSetup.exe");
                await UpdateCheck.DownloadAsync(package, path);
                verified = path;
            });
            if (verified is null || closing || exitRequested) return;
            try
            {
                using var installerFile = File.Open(verified, FileMode.Open, FileAccess.Read, FileShare.Read);
                string hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(installerFile));
                if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The downloaded installer changed after verification.");
                if (busy || closing || session.HasTunnel || session.NeedsCleanup) return;
                var start = new ProcessStartInfo(verified) { UseShellExecute = false };
                start.ArgumentList.Add($"/UPDATEPID={Environment.ProcessId}");
                using var installer = Process.Start(start) ?? throw new InvalidOperationException("Could not open the update installer.");
                await RequestExit();
            }
            catch (Exception error) { Report(error.Message); }
            return;
        }
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
