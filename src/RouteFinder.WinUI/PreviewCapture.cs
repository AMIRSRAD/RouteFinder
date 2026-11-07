using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using RouteFinder.App;
using RouteFinder.Service;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    internal async void RootLoadedForCapture(string path)
    {
        try
        {
            if (!Root.IsLoaded)
            {
                var loaded = new TaskCompletionSource();
                Root.Loaded += (_, _) => loaded.TrySetResult();
                await loaded.Task;
            }
            if (!ConnectButton.IsEnabled || !AccountButton.IsEnabled || !AdvancedButton.IsEnabled ||
                (RouteList.SelectedItem as RoutePickerRow)?.Key != "local:Amsterdam · Home relay" || checkedApps.Count != 2)
                throw new InvalidOperationException("The preview did not render its sample route and app selection.");
            if (!TestRouteButton.IsEnabled || !CompareButton.IsEnabled || !SettingsButton.IsEnabled || !RouteOptionsButton.IsEnabled ||
                !DestinationButton.IsEnabled || !ConnectionActionsButton.IsEnabled || !TestAllButton.IsEnabled)
                throw new InvalidOperationException("The preview did not expose route tests, options, and settings.");
            if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest) is { } display)
            {
                var work = display.WorkArea;
                var position = AppWindow.Position;
                var size = AppWindow.Size;
                if (Math.Abs(position.X - work.X - (work.Width - size.Width) / 2) > 2 ||
                    Math.Abs(position.Y - work.Y - (work.Height - size.Height) / 2) > 2)
                    throw new InvalidOperationException("RouteFinder did not open at the center of the display work area.");
            }
            VerifyProfileImport();
            VerifyLaunchActivation();
            if (applicationIcon is null)
                throw new InvalidOperationException("The RouteFinder window and tray icon could not be loaded.");
            var imported = route!;
            var allRoutes = Routes().ToArray();
            var batch = await CollectRouteTests(allRoutes);
            if (batch.Count != allRoutes.Length || allRoutes.Any(item => !measurements.ContainsKey(item.Key)))
                throw new InvalidOperationException("The full route batch did not test every route.");
            ToggleFavorite(imported);
            Refresh();
            if (!IsFavorite(imported) || (RouteList.SelectedItem as RoutePickerRow)?.Result != "42 ms · 0% est. loss")
                throw new InvalidOperationException("The preview did not display the simulated test result and retain the favorite.");
            RouteRegion.SelectedItem = "TR";
            RouteSearch.Text = "Istanbul";
            if (RouteList.Items.Count != 1 || (RouteList.Items[0] as RoutePickerRow)?.Key != "TR:2")
                throw new InvalidOperationException("Search and region filters did not combine correctly.");
            if (FilteredRouteChoices().Single().Key != "TR:2" ||
                (RouteList.Items[0] as RoutePickerRow)?.TestAge is not { Length: > 0 })
                throw new InvalidOperationException("The shortlist did not honor the current filters or show the saved test time.");
            RouteFilter.SelectedItem = "Imported configs";
            if (RouteList.Items.Count != 0)
                throw new InvalidOperationException("The source filter was not applied alongside the region filter.");
            RouteFilter.SelectedIndex = 0;
            RouteRegion.SelectedIndex = 0;
            RouteSearch.Text = "";
            route = Routes().First(choice => choice.Source == "EZConnect");
            VerifyCatalogRouting(route.Service!);
            var serviceResult = measurements[route.Key];
            if (!serviceResult.Succeeded || serviceResult.Latency != 54)
                throw new InvalidOperationException("The preview did not test the EZConnect route.");
            Refresh();
            ToggleConnection(this, new RoutedEventArgs());
            if (ConnectionTitle.Text != "Connected · preview" || LiveRtt.Text != "42 ms" || Scope.IsEnabled)
                throw new InvalidOperationException("The connected preview did not display the active route and lock traffic scope.");
            RecordRtt(null, 0);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            int[] series = [62, 58, 47, 49, 43, 45, 42];
            for (int i = 0; i < series.Length; i++)
                RecordRtt(series[i], now - (series.Length - i - 1) * System.Diagnostics.Stopwatch.Frequency);
            if (LiveVariation.Text == "—" || LatencyTrend.Points.Count != series.Length)
                throw new InvalidOperationException("The live panel did not display the RTT trend and variation.");
            await Task.Delay(350);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(Root);
            byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
            if (pixels.Length == 0) throw new InvalidOperationException("The live workspace could not be rendered.");
            using var file = File.Create(path);
            using var output = file.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();

            ToggleConnection(this, new RoutedEventArgs());
            if (ConnectionTitle.Text != "Disconnected" || !Scope.IsEnabled)
                throw new InvalidOperationException("Disconnecting did not restore the editable preview.");
            if (!ComparePublicIps("192.0.2.1", "192.0.2.1", "192.0.2.2", "192.0.2.1").Contains("stayed the same") ||
                !ComparePublicIps("192.0.2.1", "192.0.2.1", "192.0.2.2", "192.0.2.2").Contains("changed too"))
                throw new InvalidOperationException("The per-app verification did not distinguish a changed control browser.");
            UpdateTrafficHealth(10, 100, true);
            UpdateTrafficHealth(10, 200, true);
            sendsWithoutReplySince = System.Diagnostics.Stopwatch.GetTimestamp() - 21 * System.Diagnostics.Stopwatch.Frequency;
            UpdateTrafficHealth(10, 200, true);
            if (HealthAlert.Visibility != Visibility.Visible)
                throw new InvalidOperationException("The live panel did not flag outbound traffic with no received bytes.");
            UpdateTrafficHealth(20, 200, true);
            if (HealthAlert.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("The alert did not clear when incoming bytes resumed.");
            await VerifyNarrowLayout();
            bool exitDeferred = false;
            await Run("Checking exit during an operation…", async () =>
            {
                await RequestExit();
                exitDeferred = exitRequested && !closing;
            });
            if (!exitDeferred)
                throw new InvalidOperationException("Exiting during an operation did not wait for that operation to finish.");
        }
        catch (Exception error)
        {
            File.WriteAllText(path + ".error.txt", error.ToString());
            Environment.ExitCode = 1;
        }
        finally { if (!exitRequested && !closing) Close(); }
    }

    static void VerifyLaunchActivation()
    {
        string name = $@"Local\RouteFinder.Check.{Guid.NewGuid():N}";
        using var activated = new AutoResetEvent(false);
        using var first = LaunchInstance.Acquire(name, () => activated.Set());
        using var duplicate = LaunchInstance.Acquire(name, () => { });
        if (first is null || duplicate is not null || !activated.WaitOne(TimeSpan.FromSeconds(3)))
            throw new InvalidOperationException("A second launch did not signal the first instance.");
        if (!LaunchInstance.TryActivate(name) || !activated.WaitOne(TimeSpan.FromSeconds(3)))
            throw new InvalidOperationException("An existing instance could not be activated before elevation.");
    }

    static void VerifyProfileImport()
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        string config = $"[Interface]\nPrivateKey={key}\nAddress=10.0.0.2/32\nAllowedApps=C:\\Games\\fixture.exe\n" +
            $"[Peer]\nPublicKey={key}\nAllowedIPs=0.0.0.0/0\nEndpoint=fixture.invalid:51820";
        var first = PrepareImport(config, "Fixture", []);
        var next = PrepareImport(config, "fixture", [first]);
        if (next.Name != "fixture (2)" || next.Applications.Single() != @"C:\Games\fixture.exe")
            throw new InvalidOperationException("Config import did not retain allowed apps or distinguish duplicate names.");
        var sample = System.Text.Encoding.UTF8.GetBytes(config);
        var encrypted = ProfileStore.Protect(sample, true);
        if (sample.SequenceEqual(encrypted) || !sample.SequenceEqual(ProfileStore.Protect(encrypted, false)))
            throw new InvalidOperationException("Windows-user profile encryption did not round-trip.");
    }

    async Task VerifyNarrowLayout()
    {
        apps.AddRange(Enumerable.Range(1, 25).Select(index => $@"C:\Apps\Example-{index}.exe"));
        RenderApps();
        Refresh();
        AppWindow.Resize(new SizeInt32(620, 690));
        for (int attempt = 0; attempt < 20 &&
            (Grid.GetRow(RouteCard) != 1 || AppScroller.ScrollableHeight <= 0); attempt++) await Task.Delay(50);
        if (Grid.GetRow(RouteCard) != 1 || Grid.GetRow(HeaderActions) != 1)
            throw new InvalidOperationException("The route card and navigation did not stack at a narrow window size.");
        if (AppScroller.ScrollableHeight <= 0)
            throw new InvalidOperationException("A long app list did not scroll inside its card.");
        var bounds = ConnectButton.TransformToVisual(Root).TransformBounds(
            new Windows.Foundation.Rect(0, 0, ConnectButton.ActualWidth, ConnectButton.ActualHeight));
        if (bounds.Top < 0 || bounds.Bottom > Root.ActualHeight || bounds.Left < 0 || bounds.Right > Root.ActualWidth)
            throw new InvalidOperationException("The Connect button was outside the visible window.");
    }

    void VerifyCatalogRouting(ServiceRoute service)
    {
        using var data = System.Text.Json.JsonDocument.Parse("""{"global_disallowed_domains":"example.test"}""");
        var game = new CatalogGame("Example game", ["cs2.exe"], ["192.0.2.10"], data.RootElement)
        {
            LinkedAppNames = ["Launcher"], IncompatibleAppNames = ["Conflicting app"]
        };
        var launcher = new CatalogGame("Launcher", ["launcher.exe"], [], default);
        var incompatible = new CatalogGame("Conflicting app", ["conflict.exe"], [], default);
        accountCatalog = new BootstrapData([service], [game, launcher, incompatible], "", default);
        selectedGame = game;
        var result = CatalogRouting(service, [@"C:\Games\cs2.exe", @"C:\Games\launcher.exe"]);
        if (!result.Ips.Contains("192.0.2.10") || !result.Domains.Contains("example.test"))
            throw new InvalidOperationException("The catalog did not apply bypass rules for the game and its linked app.");
        var conflict = CatalogRouting(service, [@"C:\Games\cs2.exe", @"C:\Games\conflict.exe"]);
        if (!conflict.Conflicts.Contains("Conflicting app") || conflict.Ips.Length > 0)
            throw new InvalidOperationException("The catalog did not detect an incompatible selected app.");
        selectedGame = null;
        accountCatalog = null;
    }
}
