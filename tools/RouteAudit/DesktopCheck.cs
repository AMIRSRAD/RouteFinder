using System.Net.Sockets;
using System.Text.Json;
using RouteFinder.App;
using RouteFinder.Service;

static class DesktopCheck
{
    public static async Task<int> RunAsync(string pipe, string output, string routeKey)
    {
        using var connection = new ConnectionSession(new EzConnectClient());
        var results = new Dictionary<string, object?>();
        int exitCode = 1;
        try
        {
            var (email, password) = await CredentialPipe.ReceiveAsync(pipe, CancellationToken.None);
            BootstrapData catalog;
            try { catalog = await connection.SignInAsync(email, password); }
            finally { password = ""; }
            results["routes"] = catalog.Routes.Count;
            results["games"] = catalog.Games.Count;
            var route = catalog.Routes.Single(item => $"{item.RegionCode}:{item.Name}" == routeKey);
            results["route"] = routeKey;
            var test = await connection.TestAsync(route, null);
            results["test"] = test;
            results["testSessionReleased"] = !connection.NeedsCleanup;
            results["connectedEndpoint"] = await connection.ConnectAsync(route, null, [Environment.ProcessPath!]);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!deadline.IsCancellationRequested)
            {
                using var socket = new TcpClient();
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                probe.CancelAfter(TimeSpan.FromSeconds(2));
                try { await socket.ConnectAsync("1.1.1.1", 443, probe.Token); }
                catch (Exception error) when (error is SocketException or OperationCanceledException) { }
                await Task.Delay(600);
                var state = connection.State;
                results["connection"] = state;
                if (state is { RxBytes: > 0 }) { exitCode = test.Succeeded ? 0 : 1; break; }
            }
        }
        catch (EzConnectServiceException error) { results["serviceError"] = error.Message; }
        catch (Exception error) { results["error"] = error.GetType().Name; }
        finally
        {
            try
            {
                await connection.DisconnectAsync();
                results["disconnectedAndReleased"] = !connection.HasTunnel && !connection.NeedsCleanup;
                if (connection.SignedIn)
                {
                    await connection.SignOutAsync();
                    results["signedOut"] = !connection.SignedIn;
                }
            }
            catch (Exception error) { results["cleanupError"] = error.GetType().Name; exitCode = 1; }
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        return exitCode;
    }
}
