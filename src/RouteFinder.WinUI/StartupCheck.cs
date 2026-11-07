using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    internal static MainWindow? VerifyStartup(string report)
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
            UiLanguage.Save(1);
            window = new MainWindow(preview: false, verifyStartup: true);
            window.Activate();
            if (window.Root.FlowDirection != Microsoft.UI.Xaml.FlowDirection.RightToLeft ||
                window.WindowHeader.FlowDirection != Microsoft.UI.Xaml.FlowDirection.LeftToRight ||
                window.Scope.Items[0] is not string label || label == "Selected apps")
                throw new InvalidOperationException("The saved Persian preference was not applied to the startup layout.");
            if (window.RouteTitle.Text != "Preferred route" || !window.RouteDetail.Text.Contains("28 ms", StringComparison.Ordinal) ||
                !window.checkedApps.SetEquals([selected, olderApp]) || window.route is null || !window.IsFavorite(window.route))
                throw new InvalidOperationException("The normal startup path did not restore the route, tests, favorite and checked apps.");
            var row = (Grid)window.AppRows.Children[0];
            row.Children.OfType<CheckBox>().Single().IsChecked = false;
            var uncheckedPaths = ProfileStore.LoadPreferences().UncheckedApplications ?? [];
            if (!uncheckedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([selected, uncheckedApp]))
                throw new InvalidOperationException("Unchecking an app did not save the traffic selection.");
            File.WriteAllText(report, "PASS normal WinUI startup restored encrypted profiles, route tests, favorites, app selection, and Persian layout. No account or driver was contacted.");
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
        return window;
    }
}
