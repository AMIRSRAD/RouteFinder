using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using RouteFinder.Core;

namespace RouteFinder.App;

internal static class WindowsNetworkTools
{
    internal sealed record TcpObservation(string Executable, string LocalAddress, string RemoteAddress, int RemotePort);

    public static async Task<TcpObservation[]> ReadTcpConnections()
    {
        string json = await RunNetworkScript("""
            $paths = @{}
            foreach ($process in Get-CimInstance Win32_Process) {
                if ($process.ExecutablePath) { $paths[[int]$process.ProcessId] = $process.ExecutablePath }
            }
            $rows = @(Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue | ForEach-Object {
                $path = $paths[[int]$_.OwningProcess]
                if ($path) { [pscustomobject]@{ Executable=$path; LocalAddress=$_.LocalAddress; RemoteAddress=$_.RemoteAddress; RemotePort=$_.RemotePort } }
            })
            ConvertTo-Json -InputObject $rows -Compress
            """, readOnly: true);
        return JsonSerializer.Deserialize<TcpObservation[]>(json) ?? [];
    }

    public static async Task SetAdapterDns(string adapterId, IPAddress[] addresses)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(item =>
            item.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase));
        var properties = adapter?.GetIPProperties();
        int? index = properties?.GetIPv4Properties()?.Index ?? properties?.GetIPv6Properties()?.Index;
        if (index is not > 0 || !Guid.TryParse(adapterId, out var guid))
            throw new InvalidOperationException("The selected Windows interface is unavailable.");
        string dns = addresses.Length == 0 ? "-ResetServerAddresses" :
            "-ServerAddresses @(" + string.Join(",", addresses.Select(address => $"'{address}'")) + ")";
        await RunNetworkScript(FormattableString.Invariant($$"""
            $adapter = Get-NetAdapter -IncludeHidden | Where-Object { [guid]$_.InterfaceGuid -eq [guid]'{{guid:D}}' }
            if (!$adapter -or $adapter.ifIndex -ne {{index}}) { throw 'The Windows interface changed. Reopen Interfaces and try again.' }
            Set-DnsClientServerAddress -InterfaceIndex {{index}} {{dns}}
            """), readOnly: false);
    }

    static async Task<string> RunNetworkScript(string script, bool readOnly)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
                "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script)) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows network management.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(readOnly ? deadline.Token : CancellationToken.None); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            throw new TimeoutException("Windows did not return connection information in time.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException((await errors).Trim());
        return await output;
    }

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
            string script = "$ErrorActionPreference='Stop'; $target=" + target + "; $changed=@(); $clock=[Diagnostics.Stopwatch]::StartNew(); " +
                "function CheckDeadline { if($clock.Elapsed.TotalSeconds -ge 45) { throw 'Updating adapter IPv6 settings timed out.' } }; " +
                "try { $up=Get-NetAdapter -ErrorAction Stop | Where-Object { $_.Status -eq 'Up' }; " +
                "foreach($adapter in $up) { $binding=Get-NetAdapterBinding -Name $adapter.Name -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue; " +
                "CheckDeadline; if($null -ne $binding -and $binding.Enabled -ne $target) { " +
                "$changed += [pscustomobject]@{ Name=$adapter.Name; Was=$binding.Enabled }; " +
                "Set-NetAdapterBinding -Name $adapter.Name -ComponentID ms_tcpip6 -Enabled $target -ErrorAction Stop | Out-Null; CheckDeadline } } } " +
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
            // Let the helper finish rollback after its cooperative deadline.
            process.WaitForExit();
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0) throw new InvalidOperationException("Windows did not accept the IPv6 adapter setting. Check administrator access and the NetAdapter module.");
        });
    }
}
