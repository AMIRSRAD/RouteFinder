using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    internal static async Task VerifyStartup(string report)
    {
        var directory = Directory.CreateTempSubdirectory("RouteFinder-startup-");
        ProfileStore.VerificationDirectory = directory.FullName;
        MainWindow? window = null;
        try
        {
            const string selected = @"C:\Games\fixture.exe";
            const string uncheckedApp = @"C:\Games\unchecked.exe";
            const string olderApp = @"C:\Games\existing.exe";
            ProfileStore.Save([new SavedProfile { Name = "Existing profile", Configuration = "Fixture", Applications = [olderApp] },
                new SavedProfile { Name = "Preferred route", Configuration = "Fixture", Applications = [selected] }]);
            ProfileStore.SaveApplications([selected, uncheckedApp, olderApp]);
            ProfileStore.SavePreferences(new(false, ["local:Preferred route"], LastConnectedRoute: "local:Preferred route",
                UncheckedApplications: [uncheckedApp],
                RouteUplinks: new() { ["local:Preferred route"] = "fixture-uplink" },
                DirectAppAdapters: new() { [selected] = "fixture-direct-adapter" },
                DirectFallbackAdapter: "fixture-fallback" ));
            ProfileStore.SaveMeasurements(new() { ["local:Preferred route"] = new("Fixture", true, 28, 3) });
            ProfileStore.SavePresets([new("Browser test", "local:Existing profile", "Existing profile", false, [olderApp], null,
                BlockOtherApps: true, AdapterId: "preset-uplink", HasAdapterChoice: true)]);
            UiLanguage.Save(1);
            window = new MainWindow(preview: false, verifyStartup: true);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Root.Loaded += (_, _) => loaded.TrySetResult();
            window.Activate();
            await loaded.Task;
            if (window.timer.IsEnabled)
                throw new InvalidOperationException("The idle app should not poll for tunnel statistics.");
            if (window.Root.FlowDirection != Microsoft.UI.Xaml.FlowDirection.RightToLeft ||
                window.WindowHeader.FlowDirection != Microsoft.UI.Xaml.FlowDirection.LeftToRight ||
                window.Scope.Items[0] is not string label || label == "Selected apps")
                throw new InvalidOperationException("The saved Persian preference was not applied to the startup layout.");
            if ((window.RouteList.SelectedItem as RoutePickerRow) is not { Title: "★ Preferred route", Result: "28 ms" } ||
                !window.checkedApps.SetEquals([selected]) || window.route is null || !window.IsFavorite(window.route))
                throw new InvalidOperationException("The normal startup path did not restore the route, tests, favorite and checked apps.");
            if (window.RouteFilter.ItemsSource is not string[] sources || !sources.SequenceEqual(["All sources", "Imported configs"]))
                throw new InvalidOperationException("The source filter must reflect the routes in the library.");
            if (window.UplinkFor(window.route) != "fixture-uplink" ||
                window.UplinkFor(window.Routes().Single(item => item.Key == "local:Existing profile")) is not null)
                throw new InvalidOperationException("The saved internet adapter must apply only to its own route.");
            if (window.preferences.DirectAppAdapters?.GetValueOrDefault(selected) != "fixture-direct-adapter" ||
                window.directRouting is not null)
                throw new InvalidOperationException("Windows app assignments should load without starting routing.");
            if (window.WindowsFallbackAdapter.SelectedItem is not WindowsAdapterChoice { Id: "fixture-fallback", HasGateway: false })
                throw new InvalidOperationException("The saved fallback should remain visible when its adapter is unavailable.");
            if (window.AppRows.Children.Count != 3 ||
                window.AdapterRulesBar.Visibility != Microsoft.UI.Xaml.Visibility.Visible ||
                window.windowsAppDraft?.GetValueOrDefault(selected) != "fixture-direct-adapter" ||
                window.windowsAppDraft?.ContainsKey(olderApp) != false)
                throw new InvalidOperationException("The Apps list must show saved adapter rules and reject missing adapters.");
            if (((Grid)window.AppRows.Children[0]).Children.OfType<ComboBox>().Single().SelectedItem is not
                AppRouteMode { Kind: AppRouteKind.Adapter, Available: false })
                throw new InvalidOperationException("An app pinned to an unavailable adapter must say so in its route menu.");
            var originalSize = window.AppWindow.Size;
            var compact = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCompact(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs args)
            {
                if (window.Nav.DisplayMode == NavigationViewDisplayMode.Minimal) compact.TrySetResult();
            }
            window.Workspace.SizeChanged += OnCompact;
            try
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(420, 460));
                await compact.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (window.Nav.DisplayMode != NavigationViewDisplayMode.Minimal)
                    throw new InvalidOperationException("The navigation pane must collapse at narrow window widths.");
            }
            finally
            {
                window.Workspace.SizeChanged -= OnCompact;
                window.AppWindow.Resize(originalSize);
            }
            if (window.route.Key != "local:Preferred route")
                throw new InvalidOperationException("Resizing the window must not change the selected route.");
            const string addedApp = @"C:\Games\added.exe";
            window.AddApplications([addedApp]);
            if (window.windowsAppDraft?.GetValueOrDefault(addedApp) != "")
                throw new InvalidOperationException("Adding an app must update the Windows routing suggestions immediately.");
            window.checkedApps.Remove(addedApp);
            window.Refresh();
            if (window.windowsAppDraft.ContainsKey(addedApp))
                throw new InvalidOperationException("Unchecking an app must remove its suggestion.");
            window.checkedApps.Add(addedApp);
            window.Refresh();
            if (!window.windowsAppDraft.ContainsKey(addedApp))
                throw new InvalidOperationException("Rechecking an app must restore its suggestion.");
            window.RemoveApplication(addedApp);
            if (window.windowsAppDraft.ContainsKey(addedApp) ||
                window.windowsAppDraft.GetValueOrDefault(selected) != "fixture-direct-adapter")
                throw new InvalidOperationException("Removing an app must remove its suggestion and preserve other assignments.");
            var row = (Grid)window.AppRows.Children[0];
            row.Children.OfType<CheckBox>().Single().IsChecked = false;
            await Task.Delay(300);
            var uncheckedPaths = ProfileStore.LoadPreferences().UncheckedApplications ?? [];
            if (!uncheckedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([selected, uncheckedApp, olderApp]))
                throw new InvalidOperationException("Unchecking an app did not save the traffic selection.");
            window.RouteList.SelectedItem = window.RouteList.Items.OfType<RoutePickerRow>()
                .Single(item => item.Key == "local:Existing profile");
            if (window.route?.Key != "local:Existing profile" || !window.checkedApps.SetEquals([olderApp]))
                throw new InvalidOperationException("Selecting an imported route did not load its saved apps.");
            window.RouteList.SelectedItem = window.RouteList.Items.OfType<RoutePickerRow>()
                .Single(item => item.Key == "local:Preferred route");
            if (!window.checkedApps.SetEquals([selected]))
                throw new InvalidOperationException("Switching imported routes reused the previous route's apps.");
            // Switching routes moves the route's apps to Through VPN, and that is saved, not just shown.
            if (ProfileStore.LoadPreferences().DirectAppAdapters?.ContainsKey(selected) == true)
                throw new InvalidOperationException("Switching routes must save that the route's apps go through the VPN.");
            // A change to the VPN apps changes what a connected tunnel was started with, so it would reconnect.
            string setupBefore = window.VpnSignature();
            window.checkedApps.Remove(selected);
            if (window.VpnSignature() == setupBefore)
                throw new InvalidOperationException("Switching an app off while connected must be picked up by the tunnel.");
            window.checkedApps.Add(selected);
            if (window.VpnSignature() != setupBefore)
                throw new InvalidOperationException("The connected setup signature must be stable.");
            var inspection = await window.ReadAppRoutingReport();
            if (!inspection.Contains("fixture.exe · Through VPN · Preferred route") || inspection.Contains("fixture.exe · Direct"))
                throw new InvalidOperationException("The routing inspector must report a Through VPN app as using the VPN route.");
            if (!window.VpnBarTitle.Text.StartsWith("1 app will go through Preferred route"))
                throw new InvalidOperationException($"The Apps page must say where Through VPN apps go (got \"{window.VpnBarTitle.Text}\").");
            var vpnMenu = ((Grid)window.AppRows.Children[0]).Children.OfType<ComboBox>().Single();
            if (vpnMenu.SelectedItem is not AppRouteMode { Kind: AppRouteKind.Vpn, AdapterId: "fixture-uplink", Available: false } ||
                ((IEnumerable<AppRouteMode>)vpnMenu.ItemsSource).First() is not { Kind: AppRouteKind.Vpn, AdapterId: "" })
                throw new InvalidOperationException("A Through VPN app must show the route's tunnel adapter and offer Automatic.");
            window.ApplyPreset(window.presets.Single());
            if (window.route?.Key != "local:Existing profile" || !window.checkedApps.SetEquals([olderApp]) || window.Connected)
                throw new InvalidOperationException("Applying the saved preset did not restore its route and app scope without connecting.");
            if (window.UplinkFor(window.route!) != "preset-uplink" ||
                window.preferences.RouteUplinks?.ContainsKey("local:Existing profile") == true)
                throw new InvalidOperationException("The preset must restore its adapter without rewriting the route default.");
            if (!window.BlocksOtherApps || !window.UseNetworkLock(console: false) || window.UseNetworkLock(console: true))
                throw new InvalidOperationException($"The strict preset selected scope {window.Scope.SelectedIndex} or requested the wrong network lock.");
            if (ProfileStore.LoadPreferences() is not { BlockOtherApps: true, EntirePcMode: false })
                throw new InvalidOperationException("The strict preset scope was not saved.");
            var restored = new MainWindow(preview: false, verifyStartup: true);
            try
            {
                if (!restored.BlocksOtherApps)
                    throw new InvalidOperationException("Startup did not restore Selected apps only.");
                restored.profiles.Clear();
                restored.route = null;
                restored.Refresh();
                if (restored.EmptyRouteLibrary.Visibility != Microsoft.UI.Xaml.Visibility.Visible ||
                    restored.RouteList.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed ||
                    !restored.AddRouteButton.IsEnabled || !restored.ImportRouteCommand.IsEnabled ||
                    !restored.AccountCommand.IsEnabled || restored.ConnectButton.IsEnabled ||
                    restored.CatalogCommand.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed)
                    throw new InvalidOperationException("An empty route library must offer import and provider sign-in without a catalog.");
            }
            finally { restored.Close(); }
            window.Scope.SelectedIndex = 0;
            if (window.UseNetworkLock(console: false) || ProfileStore.LoadPreferences().BlockOtherApps)
                throw new InvalidOperationException("Returning to Selected apps did not clear the strict-mode override.");
            window.armed = new AppWatch(window.route!.Key, new(window.checkedApps, StringComparer.OrdinalIgnoreCase));
            window.Refresh();
            if (!window.timer.IsEnabled || window.timer.Interval != TimeSpan.FromSeconds(3))
                throw new InvalidOperationException("App-launch watching should poll every three seconds.");
            window.CancelWatch();
            window.Refresh();
            if (window.timer.IsEnabled)
                throw new InvalidOperationException("Stopping app-launch watching should stop idle polling.");
            window.windowsAppDraft![selected] = "fixture-uplink";
            window.appVpnDraft[selected] = "local:Preferred route";
            var routerProfile = new ProfileStore.AppRoutingProfile("Gaming", new(window.windowsAppDraft),
                new(window.appVpnDraft), window.windowsFallbackDraft);
            window.preferences = window.preferences with { AppRoutingProfiles = [routerProfile] };
            if (!window.StoreWindowsAppRules()) throw new InvalidOperationException("Could not persist the unified router draft.");
            var savedRouter = ProfileStore.LoadPreferences();
            var savedProfile = savedRouter.AppRoutingProfiles?.Single();
            if (savedRouter.AppVpnRoutes?.GetValueOrDefault(selected) != "local:Preferred route" ||
                savedProfile?.Adapters.GetValueOrDefault(selected) != "fixture-uplink" ||
                savedProfile?.DefaultAdapter != "fixture-fallback")
                throw new InvalidOperationException("The routing profile lost its VPN route, uplink, or default adapter.");
            // Beside a default adapter, RouteFinder and the VPN apps are pinned to the tunnel's adapter with app rules.
            string realApp = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            var pins = window.VpnPins("fixture-fallback", [selected, realApp]);
            string? tunnelAdapter = window.PinnedTunnelAdapter("fixture-fallback");
            if (tunnelAdapter is null || pins.Length != 2 || pins.Any(pin => pin.AdapterId != tunnelAdapter) ||
                !pins.Any(pin => pin.Executable.Equals(realApp, StringComparison.OrdinalIgnoreCase)) ||
                !pins.Any(pin => pin.Executable.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The tunnel and VPN apps must be pinned to the route's adapter above the default adapter.");
            // A default adapter cannot be pinned around in virtual adapter mode, so it stays off there.
            window.windowsFallbackDraft = "fixture-fallback";
            window.preferences = window.preferences with { PortableEngine = true };
            if (window.DesiredFallback() is not null || window.VpnPins("fixture-fallback", [selected]).Length != 0)
                throw new InvalidOperationException("Virtual adapter mode must not apply a default adapter.");
            window.preferences = window.preferences with { PortableEngine = false };
            window.windowsFallbackDraft = null;
            // Another VPN holding the default route: the tunnel and VPN apps are kept on a physical adapter even
            // without a default adapter, and its adapter is told apart from real ones.
            if (!MainWindow.LooksLikeVpn((System.Net.NetworkInformation.NetworkInterfaceType)53, "Wintun Userspace Tunnel", "FSVpnSDK-HydraTunnel") ||
                !MainWindow.LooksLikeVpn(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "TAP-Windows Adapter V9", "Local Area Connection") ||
                MainWindow.LooksLikeVpn(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "Intel(R) Ethernet Connection", "Ethernet") ||
                MainWindow.LooksLikeVpn(System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211, "Intel(R) Wi-Fi 6 AX201", "Wi-Fi") ||
                MainWindow.LooksLikeVpn(System.Net.NetworkInformation.NetworkInterfaceType.Ppp, "WAN Miniport (PPPOE)", "Broadband") ||
                MainWindow.LooksLikeVpn((System.Net.NetworkInformation.NetworkInterfaceType)53, "WireSock Virtual Adapter", "WireSock"))
                throw new InvalidOperationException("Another VPN's adapter was not told apart from physical adapters.");
            (window.foreignVpnName, window.autoPhysicalUplink) = (null, null);
            string? realDefault = window.windowsDefaultAdapterId;
            // The route's adapter is the one Windows uses anyway: nothing needs pinning.
            window.windowsDefaultAdapterId = window.PinnedTunnelAdapter(null);
            if (window.VpnPins(null, [realApp]).Length != 0)
                throw new InvalidOperationException("When the tunnel's adapter is Windows' own choice nothing should be pinned.");
            // The route leaves through an adapter Windows does not prefer (Wi-Fi beside Ethernet): WireSock only captures
            // there, so the VPN apps must be pinned to it.
            window.windowsDefaultAdapterId = "some-other-adapter";
            if (window.PinnedTunnelAdapter(null) is { } routeAdapter &&
                window.VpnPins(null, [realApp]) is var otherPins && (otherPins.Length != 2 || otherPins.Any(pin => pin.AdapterId != routeAdapter)))
                throw new InvalidOperationException("VPN apps must be pinned to the route's adapter when Windows prefers another one.");
            window.windowsDefaultAdapterId = realDefault;
            (window.foreignVpnName, window.autoPhysicalUplink) = ("FSVpnSDK-HydraTunnel", "fixture-physical");
            var foreignPins = window.VpnPins(null, [realApp]);
            if (foreignPins.Length != 2 || window.PinnedTunnelAdapter(null) is not { } kept || foreignPins.Any(pin => pin.AdapterId != kept))
                throw new InvalidOperationException("Beside another VPN, RouteFinder and the VPN apps must be kept on a physical adapter.");
            (window.foreignVpnName, window.autoPhysicalUplink) = (null, null);
            // A default adapter that is offline hands over to a working physical adapter, so apps without a rule stay direct.
            if (window.windowsAdapters.FirstOrDefault(adapter => adapter.HasGateway) is { } working)
            {
                (window.windowsFallbackDraft, window.autoPhysicalUplink) = ("offline-adapter", working.Id);
                if (window.DesiredFallback() != working.Id)
                    throw new InvalidOperationException("An offline default adapter must hand over to a working one.");
                // The default adapter is set through the adapter metric, never a rule on every app (that blanked
                // Riot Client's window). Only another VPN's split routes still need the catch-all rule.
                int scope = window.Scope.SelectedIndex;
                window.Scope.SelectedIndex = 0;
                window.windowsFallbackDraft = working.Id;
                if (window.DesiredPlan().CatchAll is not null)
                    throw new InvalidOperationException("A default adapter must not put a rule on every app.");
                window.foreignVpnName = "FSVpnSDK-HydraTunnel";
                if (window.DesiredPlan().CatchAll != working.Id)
                    throw new InvalidOperationException("Beside another VPN the default adapter still needs its catch-all rule.");
                window.foreignVpnName = null;
                window.Scope.SelectedIndex = scope;
                (window.windowsFallbackDraft, window.autoPhysicalUplink) = (null, null);
            }
            // The detailed log writes the setup, adapters, routes, and every process's connections.
            RouteFinder.Core.DiagnosticLog.Start(Path.Combine(Path.GetTempPath(), "RouteFinder-log-check-" + Environment.ProcessId));
            window.LogSetupIfChanged("Startup check");
            window.LogAdapters(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces(), null);
            window.Snapshot("Tunnel stopped", new(window.apps, StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase) { "svchost.exe" }, null);
            RouteFinder.Core.DiagnosticLog.Flush(3000);
            string logText = string.Join(Environment.NewLine, RouteFinder.Core.DiagnosticLog.Tail(100000));
            File.WriteAllText(report + ".log", logText);
            foreach (string category in new[] { "SETUP", "ADAPTERS", "PATH", "ROUTES", "APPS" })
                if (!logText.Contains(" " + category))
                    throw new InvalidOperationException($"The detailed log has no {category} entry.");
            if (!logText.Contains(" pid ") || !logText.Contains(" · TCP "))
                throw new InvalidOperationException("The detailed log did not read process connections.");
            // The Start with Windows task is valid XML that runs RouteFinder elevated in the tray at logon, with awkward characters escaped.
            var taskXml = System.Xml.Linq.XDocument.Parse(StartupTask.BuildXml(@"C:\Program Files\Route&Finder\app\RouteFinder.exe", @"PC\user & co"));
            System.Xml.Linq.XNamespace taskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            if (taskXml.Descendants(taskNs + "Command").Single().Value != @"C:\Program Files\Route&Finder\app\RouteFinder.exe" ||
                taskXml.Descendants(taskNs + "Arguments").Single().Value != "--startup" ||
                taskXml.Descendants(taskNs + "RunLevel").Single().Value != "HighestAvailable" ||
                taskXml.Descendants(taskNs + "ExecutionTimeLimit").Single().Value != "PT0S" ||
                taskXml.Descendants(taskNs + "LogonTrigger").Count() != 1)
                throw new InvalidOperationException("The Start with Windows task definition is wrong.");
            // Entire PC and Selected apps only take over every app, so adapter rules pause themselves.
            int scopeBefore = window.Scope.SelectedIndex;
            window.Scope.SelectedIndex = 1;
            if (!window.AdapterRulesPaused || window.DesiredPlan().Rules.Any(rule => !rule.Executable.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Direct app rules must pause while Entire PC is on.");
            // Entire PC on a route whose adapter Windows does not prefer: while connecting, every app goes to that adapter.
            window.connectingTunnel = true;
            var (_, entireCatchAll) = window.DesiredPlan();
            window.connectingTunnel = false;
            if (window.TunnelNeedsPinning(null) && entireCatchAll != window.PinnedTunnelAdapter(null))
                throw new InvalidOperationException("Entire PC must send every app to the tunnel's adapter.");
            if (window.DesiredPlan().CatchAll is not null)
                throw new InvalidOperationException("Entire PC must not force apps onto an adapter while disconnected.");
            window.Scope.SelectedIndex = scopeBefore;
            window.Refresh();
            // The default adapter is saved the moment it changes, so it cannot silently fail to stick.
            var fallbackChoices = (IEnumerable<WindowsAdapterChoice>)window.WindowsFallbackAdapter.ItemsSource;
            window.WindowsFallbackAdapter.SelectedItem = fallbackChoices.First(item => item.Id.Length == 0);
            if (ProfileStore.LoadPreferences().DirectFallbackAdapter is not null)
                throw new InvalidOperationException("Choosing Follow Windows must clear the saved default adapter.");
            window.WindowsFallbackAdapter.SelectedItem = fallbackChoices.First(item => item.Id == "fixture-fallback");
            if (ProfileStore.LoadPreferences().DirectFallbackAdapter != "fixture-fallback")
                throw new InvalidOperationException("The default adapter must be saved as soon as it is chosen.");
            // Closing the window sends the app to the tray by default, and the option is saved with the other settings.
            if (!window.preferences.CloseToTray || window.settings is null || !window.settings.CloseTray.IsOn)
                throw new InvalidOperationException("Close to tray must be on by default and shown in Settings.");
            // A catalog game becomes one group, even when none of its executables exist yet.
            int standalone = window.AppRows.Children.Count;
            window.AddCatalogGroup("Example game", [], ["Game.exe", "Launcher.exe", "Helper.exe"]);
            var gameGroup = window.groups.SingleOrDefault();
            if (gameGroup is null || gameGroup.Pending.Count != 3 || gameGroup.Members.Count != 0 ||
                window.AppRows.Children.Count != standalone + 1 || window.AppRows.Children[0] is not Expander)
                throw new InvalidOperationException("A catalog game with no files found must be added as one group with its executables waiting.");
            window.AddApplications([@"C:\Games\Game.exe", @"C:\Games\Helper.exe"]);
            if (gameGroup.Members.Count != 2 || gameGroup.Pending.Count != 1 || gameGroup.Pending[0] != "Launcher.exe" ||
                window.AppRows.Children.Count != standalone + 1 || !window.apps.Contains(@"C:\Games\Game.exe"))
                throw new InvalidOperationException("Files added later must join the group that was waiting for them instead of becoming separate rows.");
            window.ToggleGroup(gameGroup, false);
            if (window.checkedApps.Contains(@"C:\Games\Game.exe") || window.VpnApps().Contains(@"C:\Games\Helper.exe"))
                throw new InvalidOperationException("Switching a group off must switch off every member.");
            window.ChooseGroupRoute(gameGroup, new AppRouteMode(AppRouteKind.Adapter, "fixture-direct-adapter", "Direct"));
            if (window.windowsAppDraft!.GetValueOrDefault(@"C:\Games\Game.exe") != "fixture-direct-adapter" || !gameGroup.Direct)
                throw new InvalidOperationException("A group's route choice must apply to every member.");
            var saved = ProfileStore.LoadPreferences().AppGroups?.Single();
            if (saved is null || saved.Members.Count != 2 || saved.Pending.Count != 1 || saved.Enabled || !saved.Direct)
                throw new InvalidOperationException("App groups were not saved with their state.");
            window.RemoveGroup(gameGroup);
            if (window.groups.Count != 0 || window.apps.Contains(@"C:\Games\Game.exe") || window.AppRows.Children.Count != standalone)
                throw new InvalidOperationException("Removing a group must remove its members too.");
            // Generic names (Update.exe) only count inside the group's own folders, and are never handed to WireSock by name.
            var ownRoot = Directory.CreateDirectory(Path.Combine(directory.FullName, "a", "b", "c", "Foo", "bin")).FullName;
            string ownApp = Path.Combine(ownRoot, "Foo.exe");
            string[] related = [ownApp];
            string ownUpdate = Path.Combine(ownRoot, "Update.exe");
            string otherUpdate = Path.Combine(directory.FullName, "elsewhere", "Slack", "Update.exe");
            string distinctive = Path.Combine(directory.FullName, "elsewhere", "Shipping-Win64.exe");
            var trusted = MainWindow.TrustedRunning([ownUpdate, otherUpdate, distinctive], ["Update.exe", "Shipping-Win64.exe"], related, "Foo");
            if (!MainWindow.IsGenericName("Update.exe") || MainWindow.IsGenericName("Shipping-Win64.exe") ||
                !trusted.Contains(ownUpdate) || trusted.Contains(otherUpdate) || !trusted.Contains(distinctive))
                throw new InvalidOperationException("Running programs must match waiting names only inside the group's folders when the name is generic.");
            window.AddCatalogGroup("Squirrel app", [], ["Update.exe", "Real.exe"]);
            if (window.VpnTargets().Contains("Update.exe") || !window.VpnTargets().Contains("Real.exe"))
                throw new InvalidOperationException("A generic name must never be passed to the tunnel by name.");
            window.RemoveGroup(window.groups.Single(group => group.Name == "Squirrel app"));
            File.WriteAllText(Path.Combine(directory.FullName, "preferences.dat"), "unreadable fixture");
            var recovery = new MainWindow(preview: false, verifyStartup: true);
            try
            {
                if (!recovery.unavailableStores.Contains("preferences") || recovery.profiles.Count != 2 || recovery.apps.Count != 3)
                    throw new InvalidOperationException("An unreadable preferences file prevented the other stores from loading.");
                bool wrote = false;
                try { recovery.WriteStore("preferences", () => wrote = true); }
                catch (InvalidOperationException) when (!wrote) { }
                if (wrote)
                    throw new InvalidOperationException("The app allowed a write to an unreadable store.");
            }
            finally { recovery.Close(); }
            File.WriteAllText(report, "PASS WinUI restored saved data and Persian layout, loaded saved apps when switching routes, verified the Selected apps only preset and network-lock request, stopped idle polling when app-launch watching was off, and protected an unreadable preferences file. No account or driver was contacted.");
        }
        catch (Exception error)
        {
            File.WriteAllText(report, error.ToString());
            Environment.ExitCode = 1;
        }
        finally
        {
            window?.Close();
            ProfileStore.VerificationDirectory = null;
            foreach (var file in directory.EnumerateFiles()) file.Delete();
            directory.Delete();
        }
    }
}
