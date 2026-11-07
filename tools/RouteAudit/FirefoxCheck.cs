using System.Diagnostics;
using System.Text.Json;
using RouteFinder.App;
using RouteFinder.Service;

static class FirefoxCheck
{
    public static async Task<int> RunAsync(string pipe, string output, string firefox)
    {
        using var connection = new ConnectionSession(new EzConnectClient());
        var runs = new List<Dictionary<string, object?>>();
        string? failure = null;
        bool passed = false;
        try
        {
            var (email, password) = await CredentialPipe.ReceiveAsync(pipe, CancellationToken.None);
            BootstrapData catalog;
            try { catalog = await connection.SignInAsync(email, password); }
            finally { password = ""; }
            foreach (var routeKey in new[] { "AM:1", "DE:1", "IR:1" })
            {
                var route = catalog.Routes.Single(item => $"{item.RegionCode}:{item.Name}" == routeKey);
                var row = new Dictionary<string, object?> { ["route"] = routeKey, ["label"] = route.CityName };
                runs.Add(row);
                try
                {
                    row["endpoint"] = await connection.ConnectAsync(route, null, [firefox]);
                    var screenshot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $"firefox-check-{route.RegionCode}.png");
                    var pageCaptured = await OpenFreshFirefoxAsync(firefox, screenshot);
                    var state = connection.State;
                    row["pageCaptured"] = pageCaptured;
                    row["tunnel"] = state;
                    passed = pageCaptured && state is { RxBytes: > 0, TimeSinceLastHandshakeMilliseconds: >= 0 };
                }
                finally
                {
                    await connection.DisconnectAsync();
                    row["released"] = !connection.NeedsCleanup;
                }
                if (passed) break;
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
        catch (EzConnectServiceException error) { failure = error.Message; }
        catch (Exception error) { failure = error.GetType().Name; }
        finally
        {
            try { await connection.SignOutAsync(); }
            catch (Exception error) { failure ??= error.GetType().Name; }
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed, failure, runs }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed && failure == null ? 0 : 1;
    }

    static async Task<bool> OpenFreshFirefoxAsync(string executable, string screenshot)
    {
        var profile = Directory.CreateTempSubdirectory("routefinder-firefox-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(27));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(profile.FullName, "user.js"),
                "user_pref(\"browser.shell.checkDefaultBrowser\", false);\n" +
                "user_pref(\"network.captive-portal-service.enabled\", false);\n" +
                "user_pref(\"network.connectivity-service.enabled\", false);\n");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-no-remote", "-profile", profile.FullName, "-headless", "-screenshot", screenshot, "--window-size=900,550", "https://example.com/" })
                start.ArgumentList.Add(argument);
            using var browser = Process.Start(start) ?? throw new InvalidOperationException("Firefox did not start.");
            try { await browser.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!browser.HasExited) browser.Kill(entireProcessTree: true);
                await browser.WaitForExitAsync();
                return false;
            }
            return browser.ExitCode == 0 && File.Exists(screenshot) && new FileInfo(screenshot).Length > 0;
        }
        finally
        {
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (profile.Parent?.FullName.Equals(temp, StringComparison.OrdinalIgnoreCase) == true &&
                profile.Name.StartsWith("routefinder-firefox-", StringComparison.Ordinal))
            {
                try { profile.Delete(recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
