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
                UncheckedApplications: [uncheckedApp]));
            ProfileStore.SaveMeasurements(new() { ["local:Preferred route"] = new("Fixture", true, 28, 3) });
            ProfileStore.SavePresets([new("Browser test", "local:Existing profile", "Existing profile", false, [olderApp], null, BlockOtherApps: true)]);
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
                !window.checkedApps.SetEquals([selected, olderApp]) || window.route is null || !window.IsFavorite(window.route))
                throw new InvalidOperationException("The normal startup path did not restore the route, tests, favorite and checked apps.");
            var row = (Grid)window.AppRows.Children[0];
            row.Children.OfType<CheckBox>().Single().IsChecked = false;
            var uncheckedPaths = ProfileStore.LoadPreferences().UncheckedApplications ?? [];
            if (!uncheckedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([selected, uncheckedApp]))
                throw new InvalidOperationException("Unchecking an app did not save the traffic selection.");
            window.RouteList.SelectedItem = window.RouteList.Items.OfType<RoutePickerRow>()
                .Single(item => item.Key == "local:Existing profile");
            if (window.route?.Key != "local:Existing profile" || !window.checkedApps.SetEquals([olderApp]))
                throw new InvalidOperationException("Selecting an imported route did not load its saved apps.");
            window.RouteList.SelectedItem = window.RouteList.Items.OfType<RoutePickerRow>()
                .Single(item => item.Key == "local:Preferred route");
            if (!window.checkedApps.SetEquals([selected]))
                throw new InvalidOperationException("Switching imported routes reused the previous route's apps.");
            window.ApplyPreset(window.presets.Single());
            if (window.route?.Key != "local:Existing profile" || !window.checkedApps.SetEquals([olderApp]) || window.Connected)
                throw new InvalidOperationException("Applying the saved preset did not restore its route and app scope without connecting.");
            if (!window.BlocksOtherApps || !window.UseNetworkLock(console: false) || window.UseNetworkLock(console: true))
                throw new InvalidOperationException($"The strict preset selected scope {window.Scope.SelectedIndex} or requested the wrong network lock.");
            if (ProfileStore.LoadPreferences() is not { BlockOtherApps: true, EntirePcMode: false })
                throw new InvalidOperationException("The strict preset scope was not saved.");
            var restored = new MainWindow(preview: false, verifyStartup: true);
            try
            {
                if (!restored.BlocksOtherApps)
                    throw new InvalidOperationException("Startup did not restore Selected apps only.");
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
