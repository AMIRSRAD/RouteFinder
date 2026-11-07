using System.Diagnostics;
using RouteFinder.Core;

namespace RouteFinder.App;

internal static class WindowsNetworkTools
{
    public static async Task<string[]> ReadHotspotNeighborsAsync(HashSet<string> downstreamNames)
    {
        string shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "@(Get-NetNeighbor -AddressFamily IPv4 -ErrorAction Stop | Select-Object InterfaceAlias,IPAddress,@{n='State';e={$_.State.ToString()}}) | ConvertTo-Json -Compress" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not query Windows network neighbors.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            throw new TimeoutException("Windows did not return network neighbors in time.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows could not read the neighbor table.");
        string json = await output;
        await errors;
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        var entries = root.ValueKind == System.Text.Json.JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
        return entries.Where(entry => downstreamNames.Contains(entry.GetProperty("InterfaceAlias").GetString() ?? "") &&
                entry.GetProperty("State").GetString() is "Reachable" or "Stale" or "Delay" or "Probe")
            .Select(entry => $"{entry.GetProperty("IPAddress").GetString()} • {entry.GetProperty("State").GetString()}")
            .Distinct().ToArray();
    }

    public static Task<bool?> QueryNetworkLockAsync() => Task.Run(() =>
    {
        using var sdk = new WireSockTunnel();
        return sdk.QueryDriverNetworkLock();
    });

    public static string[] ReadWireSockStatus()
    {
        var lines = new List<string>();
        try
        {
            string path = WireSockTunnel.FindSdkPath();
            lines.Add($"WireSock SDK version {FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown"}.");
            foreach (bool portable in new[] { false, true })
            {
                string backend = portable ? "Virtual adapter" : "Transparent";
                try
                {
                    using var sdk = new WireSockTunnel(path, portable: portable);
                    lines.Add($"{backend} backend exports loaded successfully.");
                }
                catch (Exception error) { lines.Add($"{backend} backend unavailable. {error.Message}"); }
            }
        }
        catch (Exception error) { lines.Add($"WireSock SDK unavailable. {error.Message}"); }

        foreach (string name in new[] { "WireSock", "ndisrd", "wintun" })
        {
            try
            {
                lines.Add($"{name} driver: {DriverStatus.Read(name)}.");
            }
            catch (Exception error) { lines.Add($"{name} driver status unavailable. {error.Message}"); }
        }
        return lines.ToArray();
    }

    public static async Task ApplyIpv6Bindings(bool enabled)
    {
        await Task.Run(() =>
        {
            string target = enabled ? "$true" : "$false";
            string script = "$ErrorActionPreference='Stop'; $target=" + target + "; $changed=@(); " +
                "try { $up=Get-NetAdapter -ErrorAction Stop | Where-Object { $_.Status -eq 'Up' }; " +
                "foreach($adapter in $up) { $binding=Get-NetAdapterBinding -Name $adapter.Name -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue; " +
                "if($null -ne $binding -and $binding.Enabled -ne $target) { Set-NetAdapterBinding -Name $adapter.Name -ComponentID ms_tcpip6 -Enabled $target -ErrorAction Stop | Out-Null; " +
                "$changed += [pscustomobject]@{ Name=$adapter.Name; Was=$binding.Enabled } } } } " +
                "catch { foreach($item in $changed) { Set-NetAdapterBinding -Name $item.Name -ComponentID ms_tcpip6 -Enabled $item.Was -ErrorAction SilentlyContinue | Out-Null }; throw }";
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command"); start.ArgumentList.Add(script);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Windows network settings helper.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(45000))
            {
                process.Kill(true);
                throw new TimeoutException("Updating adapter IPv6 settings timed out.");
            }
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0) throw new InvalidOperationException("Windows did not accept the IPv6 adapter setting. Check administrator access and the NetAdapter module.");
        });
    }
}
