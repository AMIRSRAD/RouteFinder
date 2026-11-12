using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

/// <summary>
/// Detailed diagnostic log (see <see cref="DiagnosticLog"/>): what RouteFinder decided and what Windows actually did.
/// Logs the full setup whenever it changes, every adapter and route change, and a snapshot of routes, the tunnel, and
/// every process's connections every few seconds, so routing problems can be traced after the fact.
/// </summary>
public sealed partial class MainWindow
{
    internal static string LogFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RouteFinder", "logs");

    Microsoft.UI.Dispatching.DispatcherQueueTimer? snapshotTimer;
    string lastSetupDump = "", lastAdapterDump = "", lastRouteDump = "", lastBestDump = "";
    Dictionary<string, string> lastProcessLines = new(StringComparer.OrdinalIgnoreCase);
    int snapshotRunning;
    string lastOddRemotes = "", lastStuck = "";
    int snapshotCount;

    void StartDiagnostics()
    {
        if (preview || verifying) return;
        // Handlers and the snapshot timer do nothing while logging is off, so the setting can change at any time.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            DiagnosticLog.Write("CRASH", $"Unhandled exception (terminating {args.IsTerminating}): {args.ExceptionObject}");
            DiagnosticLog.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) => DiagnosticLog.Error("CRASH", "Unobserved task exception", args.Exception);
        Microsoft.UI.Xaml.Application.Current.UnhandledException += (_, args) => DiagnosticLog.Error("CRASH", "UI exception", args.Exception);
        Closed += (_, _) => { DiagnosticLog.Write("STOP", "Window closed."); DiagnosticLog.Flush(); };
        snapshotTimer = DispatcherQueue.CreateTimer();
        snapshotTimer.Interval = TimeSpan.FromSeconds(5);
        snapshotTimer.IsRepeating = true;
        snapshotTimer.Tick += (_, _) => SnapshotTick();
        snapshotTimer.Start();
        if (preferences.DetailedLogging) StartDetailedLog();
    }

    /// <summary>Settings → Detailed logging (off by default). Starts a new log file, or stops writing.</summary>
    void SetDetailedLogging(bool on)
    {
        if (preview || verifying) return;
        if (on) StartDetailedLog();
        else if (DiagnosticLog.Enabled)
        {
            DiagnosticLog.Write("STOP", "Detailed logging turned off in Settings.");
            DiagnosticLog.Flush(800);
            DiagnosticLog.Stop();
        }
    }

    void StartDetailedLog()
    {
        if (DiagnosticLog.Enabled) return;
        try { DiagnosticLog.Start(LogFolder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        lastSetupDump = lastAdapterDump = lastRouteDump = lastBestDump = lastOddRemotes = lastStuck = "";
        lastProcessLines = new(StringComparer.OrdinalIgnoreCase);
        bool elevated;
        using (var identity = WindowsIdentity.GetCurrent()) elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        string sdk;
        try { sdk = FileVersionInfo.GetVersionInfo(WireSockTunnel.FindSdkPath()).FileVersion ?? "unknown"; }
        catch (Exception error) { sdk = $"unavailable ({error.GetType().Name})"; }
        DiagnosticLog.Write("START", $"RouteFinder {typeof(MainWindow).Assembly.GetName().Version} · pid {Environment.ProcessId} · {Environment.ProcessPath}\n" +
            $"Windows {Environment.OSVersion.Version} · elevated {elevated} · WireSock SDK {sdk} · WFP routing supported {DirectAppRouting.IsSupported()}\n" +
            $"Log file {DiagnosticLog.CurrentFile}");
        DispatcherQueue.TryEnqueue(SnapshotTick);
    }

    static void Trace(string category, string message) => DiagnosticLog.Write(category, message);

    // A stale or filtered answer (such as the 10.10.34.x block page) cached before connecting would keep sending an
    // app to the wrong address through the tunnel, so the cache is cleared whenever the tunnel starts or stops.
    static void FlushDnsCache(string reason)
    {
        try { DiagnosticLog.Write("DNS", $"Flushed the Windows DNS cache ({reason}): {(DnsFlushResolverCache() ? "ok" : "failed")}."); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { DiagnosticLog.Error("DNS", "DNS cache flush unavailable", error); }
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DnsFlushResolverCache();

    static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] < 32 || b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] >= 64 && b[1] < 128;
    }

    // Which cached DNS names point at these addresses, to explain a connection to an odd address.
    static string DnsNamesFor(IEnumerable<IPAddress> addresses)
    {
        var wanted = addresses.Select(address => address.ToString()).Distinct().ToArray();
        if (wanted.Length == 0) return "";
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                "Get-DnsClientCache | Where-Object { $_.Data -in @(" + string.Join(",", wanted.Select(ip => $"'{ip}'")) + ") } | ForEach-Object { \"$($_.Entry) -> $($_.Data) (TTL $($_.TimeToLive))\" }" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return "";
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(8000);
            return output.Length == 0 ? "no cached DNS name points there" : output;
        }
        catch (Exception error) { return $"DNS cache unavailable: {error.Message}"; }
    }

    string AdapterName(string? id) => id is null ? "(none)" : adapterNames.TryGetValue(id, out var name) ? $"{name}" :
        windowsAdapters.FirstOrDefault(adapter => adapter.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Label ?? $"unknown {id}";

    /// <summary>The whole routing setup as RouteFinder sees it, logged whenever any part of it changes.</summary>
    string SetupDump()
    {
        var lines = new List<string>();
        string scope = Scope.SelectedIndex switch { 1 => "Entire PC", 2 => "Selected apps only (network lock)", _ => "Selected apps" };
        lines.Add($"Scope {scope} · connected {session.HasTunnel} · console {connectedForConsole} · busy {busy} · cleanup pending {session.NeedsCleanup}");
        if (route is { } current)
        {
            lines.Add($"Route {current.Title} [{current.Key}] source {current.Source} · SOCKS5/automatic-only {RouteNeedsAutomaticUplink(current)}");
            lines.Add($"Uplink chosen {AdapterName(UplinkFor(current))} · effective {AdapterName(EffectiveUplink(current))} · pinned tunnel adapter {AdapterName(PinnedTunnelAdapter(DesiredFallback()))}");
        }
        else lines.Add("Route (none selected)");
        lines.Add($"Default adapter chosen {AdapterName(windowsFallbackDraft)} · desired {AdapterName(DesiredFallback())} · active {AdapterName(activeFallback)}");
        lines.Add($"Another VPN holding the default route: {foreignVpnName ?? "no"} · physical stand-in {AdapterName(autoPhysicalUplink)} · tunnel pinning needed {TunnelNeedsPinning(DesiredFallback())}");
        lines.Add($"Adapter rules running {directRouting is not null} · paused {AdapterRulesPaused} · last error {lastAdapterError ?? "none"} · active rules {activeCombined.Length}");
        foreach (var rule in activeCombined) lines.Add($"  active rule {Path.GetFileName(rule.Executable)} -> {AdapterName(rule.AdapterId)}  ({rule.Executable})");
        lines.Add($"Settings: kill switch {preferences.KillSwitch} · virtual adapter {preferences.PortableEngine} · auto MTU {preferences.AutoMtu} · MTU {preferences.TunnelMtu} · keepalive {preferences.KeepaliveSeconds} · bypass LAN {preferences.BypassLan} · DNS override '{preferences.DnsOverride}' · bypass '{preferences.BypassDestinations}' · excluded {string.Join(",", preferences.ExcludedApplications ?? [])} · proxy {(ConfiguredProxy is null ? "none" : "set")}");
        lines.Add($"Apps ({apps.Count}):");
        var assignments = DesiredAssignments();
        foreach (string path in apps)
        {
            string adapter = windowsAppDraft?.GetValueOrDefault(path) ?? "";
            string mode = !checkedApps.Contains(path) ? "OFF"
                : IsAdapterApp(path) ? $"DIRECT {AdapterName(adapter)}" + (assignments.Any(rule => rule.Executable.Equals(path, StringComparison.OrdinalIgnoreCase)) ? "" : " · NOT APPLIED (" +
                    (!ExistsCached(path) ? "file missing" : "adapter offline or missing") + ")")
                : appVpnDraft.ContainsKey(path) ? $"VPN via adapter-rule route {appVpnDraft[path]}"
                : "THROUGH VPN";
            lines.Add($"  {mode,-40} {path}{(ExistsCached(path) ? "" : "  [file missing]")}");
        }
        foreach (var group in groups)
            lines.Add($"Group {group.Name} · on {group.Enabled} · {(group.Direct ? "DIRECT " + AdapterName(group.AdapterId) : "THROUGH VPN")} · members {group.Members.Count} · waiting {string.Join(", ", group.Pending)}");
        var targets = VpnTargets();
        lines.Add($"Tunnel targets ({targets.Length}): {string.Join(" | ", targets)}");
        var pins = VpnPins(DesiredFallback());
        lines.Add($"Pins ({pins.Length}): {string.Join(" | ", pins.Select(pin => $"{Path.GetFileName(pin.Executable)}->{AdapterName(pin.AdapterId)}"))}");
        return string.Join("\n", lines);
    }

    void LogSetupIfChanged(string reason)
    {
        if (!DiagnosticLog.Enabled) return;
        string dump;
        try { dump = SetupDump(); }
        catch (Exception error) { DiagnosticLog.Error("SETUP", "Could not describe the setup", error); return; }
        if (dump == lastSetupDump) return;
        lastSetupDump = dump;
        Trace("SETUP", $"{reason}\n{dump}");
    }

    /// <summary>Every adapter with addresses, gateways and DNS; logged when anything about them changes.</summary>
    void LogAdapters(NetworkInterface[] adapters, string? defaultName)
    {
        if (!DiagnosticLog.Enabled) return;
        var lines = new List<string> { $"Windows IPv4 default adapter: {defaultName ?? "none"} · another VPN: {foreignVpnName ?? "no"} · physical stand-in: {AdapterName(autoPhysicalUplink)}" };
        foreach (var adapter in adapters.Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            try
            {
                var ip = adapter.GetIPProperties();
                int? index = adapter.Supports(NetworkInterfaceComponent.IPv4) ? ip.GetIPv4Properties()?.Index : null;
                lines.Add($"  {adapter.Name} · {adapter.Description} · {adapter.NetworkInterfaceType} · {adapter.OperationalStatus} · ifIndex {index?.ToString() ?? "-"} · id {adapter.Id}" +
                    $" · looks like VPN {LooksLikeVpn(adapter.NetworkInterfaceType, adapter.Description, adapter.Name)} · gateway ok {DirectAppRouting.HasInternetGateway(adapter)}");
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                lines.Add($"      addresses {string.Join(", ", ip.UnicastAddresses.Select(address => $"{address.Address}/{address.PrefixLength}"))}");
                lines.Add($"      gateways {string.Join(", ", ip.GatewayAddresses.Select(gateway => gateway.Address.ToString()).DefaultIfEmpty("none"))} · DNS {string.Join(", ", ip.DnsAddresses.Select(dns => dns.ToString()).DefaultIfEmpty("none"))}");
            }
            catch (NetworkInformationException error) { lines.Add($"  {adapter.Name}: {error.Message}"); }
        }
        string dump = string.Join("\n", lines);
        if (dump == lastAdapterDump) return;
        lastAdapterDump = dump;
        Trace("ADAPTERS", dump);
    }

    // Gathers UI state on the UI thread, then reads routes and connections in the background.
    void SnapshotTick()
    {
        if (closing || !DiagnosticLog.Enabled) return;
        LogSetupIfChanged("Setup changed");
        bool connected = session.HasTunnel;
        // Every 5 s while connected, every 30 s otherwise.
        if (!connected && snapshotCount++ % 6 != 0) return;
        if (connected) snapshotCount = 0;
        if (Interlocked.Exchange(ref snapshotRunning, 1) == 1) return;
        string tunnelLine = "Tunnel stopped";
        if (connected)
        {
            try
            {
                tunnelLine = session.State is { } state
                    ? $"Tunnel up · data events sent/received {session.TakeDataEventCounts()} · handshake age {state.TimeSinceLastHandshakeMilliseconds} ms · TX {state.TxBytes} · RX {state.RxBytes} · loss {state.EstimatedLoss:0.###} · RTT {state.EstimatedRtt} ms · error {(session.ActiveTunnelError is uint error ? $"0x{error:X8}" : "none")} · endpoint {connectedEndpoint}"
                    : "Tunnel up · no state";
            }
            catch (Exception error) { tunnelLine = $"Tunnel state unavailable: {error.Message}"; }
        }
        var watched = new HashSet<string>(apps, StringComparer.OrdinalIgnoreCase);
        var watchedNames = new HashSet<string>(apps.Select(Path.GetFileName).OfType<string>()
            .Concat(groups.SelectMany(group => group.Pending)), StringComparer.OrdinalIgnoreCase);
        string? endpointHost = connectedEndpoint is { } endpoint ? endpoint.Split(':')[0].Trim('[', ']') : null;
        // While connected without the relay policy, blocked names are fixed in the hosts file.
        string? fixDns = connected && !connectedForConsole && UsesDnsRelay(false) && !dnsPolicyActive ? session.ActiveDns ?? "" : null;
        _ = Task.Run(() =>
        {
            try { Snapshot(tunnelLine, watched, watchedNames, endpointHost, fixDns); }
            catch (Exception error) { DiagnosticLog.Error("SNAPSHOT", "Snapshot failed", error); }
            finally { Volatile.Write(ref snapshotRunning, 0); }
        });
    }

    void Snapshot(string tunnelLine, HashSet<string> watched, HashSet<string> watchedNames, string? endpointHost, string? fixDns = null)
    {
        if (tunnelLine != "Tunnel stopped") Trace("TUNNEL", tunnelLine);

        // Which adapter Windows picks for typical destinations and for the VPN endpoint.
        var adapters = NetworkInterface.GetAllNetworkInterfaces();
        var byIndex = new Dictionary<int, string>();
        var byAddress = new Dictionary<IPAddress, string>();
        foreach (var adapter in adapters)
        {
            try
            {
                var ip = adapter.GetIPProperties();
                if (adapter.Supports(NetworkInterfaceComponent.IPv4) && ip.GetIPv4Properties() is { } v4) byIndex[v4.Index] = adapter.Name;
                foreach (var address in ip.UnicastAddresses) byAddress[address.Address] = adapter.Name;
            }
            catch (NetworkInformationException) { }
        }
        var best = new List<string>();
        foreach (string target in new[] { "1.1.1.1", "8.8.8.8" }.Concat(endpointHost is null ? [] : [endpointHost]))
        {
            if (!IPAddress.TryParse(target, out var address) || address.AddressFamily != AddressFamily.InterNetwork) continue;
            uint destination = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
            best.Add(GetBestInterface(destination, out uint index) == 0
                ? $"{target} -> {byIndex.GetValueOrDefault((int)index, $"ifIndex {index}")}" : $"{target} -> lookup failed");
        }
        string bestDump = string.Join(" · ", best);
        if (bestDump != lastBestDump) { lastBestDump = bestDump; Trace("PATH", "Windows picks: " + bestDump); }

        // Default-like routes (whole internet and split halves) and the endpoint host route.
        string routes = RouteLines(endpointHost);
        if (routes != lastRouteDump) { lastRouteDump = routes; Trace("ROUTES", routes); }

        // Every process's connections, grouped; watched apps always, others when they change.
        var rows = ReadSockets();
        var images = new Dictionary<int, string>();
        string Image(int pid) => images.TryGetValue(pid, out var known) ? known
            : images[pid] = pid == 0 ? "System Idle" : pid == 4 ? "System" : ProcessImagePath(pid) ?? "(name needs admin)";
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var watchedLines = new List<string>();
        foreach (var process in rows.GroupBy(row => row.Pid))
        {
            string image = Image(process.Key);
            string name = Path.GetFileName(image);
            string Where(IPAddress local) => IPAddress.IsLoopback(local) ? "loopback"
                : local.Equals(IPAddress.Any) || local.Equals(IPAddress.IPv6Any) ? "any" : byAddress.GetValueOrDefault(local, local.ToString());
            var tcp = process.Where(row => row.Tcp && !IPAddress.IsLoopback(row.Remote ?? IPAddress.Loopback)).ToArray();
            var udp = process.Where(row => !row.Tcp).ToArray();
            if (tcp.Length == 0 && udp.Length == 0) continue;
            string summary = $"{name} pid {process.Key} · TCP {tcp.Length} [{string.Join(", ", tcp.GroupBy(row => Where(row.Local)).Select(group => $"{group.Key} {group.Count()}"))}]" +
                $" · UDP {udp.Length} [{string.Join(", ", udp.GroupBy(row => Where(row.Local)).Select(group => $"{group.Key} {group.Count()}"))}]";
            bool isWatched = watched.Contains(image) || watchedNames.Contains(name);
            if (isWatched)
            {
                watchedLines.Add(summary + $" · {image}");
                foreach (var row in tcp.Take(25))
                    watchedLines.Add($"      {row.Local} -> {row.Remote}:{row.RemotePort} ({row.State}) via {Where(row.Local)}");
            }
            else current[$"{image}|{process.Key}"] = summary;
        }
        Trace("APPS", watchedLines.Count > 0 ? string.Join("\n", watchedLines) : "No connections from your listed apps.");
        // A listed app talking to a private address that is not on any local network is almost always a filtered DNS
        // answer (the 10.10.34.x block page): log which cached name points there.
        var localPrefixes = byAddress.Keys.Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => string.Join('.', address.ToString().Split('.').Take(3)) + ".").ToArray();
        var odd = rows.Where(row => row.Tcp && row.Remote is { } remote && IsPrivate(remote) &&
                !localPrefixes.Any(prefix => remote.ToString().StartsWith(prefix, StringComparison.Ordinal)) &&
                (watched.Contains(Image(row.Pid)) || watchedNames.Contains(Path.GetFileName(Image(row.Pid)))))
            .Select(row => row.Remote!).Distinct().ToArray();
        string oddKey = string.Join(",", odd.Select(address => address.ToString()).Order());
        if (odd.Length > 0 && (oddKey != lastOddRemotes || fixDns is not null))
        {
            string cached = DnsNamesFor(odd);
            if (oddKey != lastOddRemotes)
                Trace("DNS", $"Listed apps are connecting to private addresses {oddKey}; this usually means a filtered DNS answer.\n{cached}");
            // Names Windows resolved to the block page get their real addresses through the tunnel.
            if (fixDns is not null)
            {
                var blocked = cached.Split('\n').Select(line => line.Trim()).Where(line => line.Contains(" -> 10.10.34."))
                    .Select(line => line.Split(' ')[0]).ToArray();
                if (blocked.Length > 0) FixBlockedNames(blocked, fixDns);
            }
        }
        lastOddRemotes = oddKey;
        // Connections stuck in SYN_SENT (cannot connect), for every program.
        var stuck = rows.Where(row => row.Tcp && row.State == "SYN_SENT").GroupBy(row => Image(row.Pid))
            .Select(group => $"{Path.GetFileName(group.Key)}: {string.Join(", ", group.Select(row => $"{row.Remote}:{row.RemotePort} via {byAddress.GetValueOrDefault(row.Local, row.Local.ToString())}").Distinct().Take(12))}")
            .Order().ToArray();
        string stuckKey = string.Join("|", stuck);
        if (stuckKey != lastStuck) { lastStuck = stuckKey; if (stuck.Length > 0) Trace("STUCK", "Connections that are not getting through:\n" + string.Join("\n", stuck)); else Trace("STUCK", "No stuck connections."); }
        var changed = current.Where(pair => lastProcessLines.GetValueOrDefault(pair.Key) != pair.Value).Select(pair => pair.Value).ToList();
        var gone = lastProcessLines.Keys.Where(key => !current.ContainsKey(key)).Select(key => key.Split('|')[0]).ToList();
        lastProcessLines = current;
        if (changed.Count > 0 || gone.Count > 0)
            Trace("OTHERS", string.Join("\n", changed.Concat(gone.Select(image => $"{Path.GetFileName(image)} has no more connections"))));
    }

    static string RouteLines(string? endpointHost)
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "interface ipv4 show route")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var process = Process.Start(start);
            if (process is null) return "netsh unavailable";
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            var wanted = new List<string> { " 0.0.0.0/0", " 0.0.0.0/1", " 128.0.0.0/1" };
            if (endpointHost is not null) wanted.Add($" {endpointHost}/32");
            var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => wanted.Any(line.Contains)).ToArray();
            return lines.Length == 0 ? "No default routes." : "Default routes:\n" + string.Join("\n", lines);
        }
        catch (Exception error) { return $"Routes unavailable: {error.Message}"; }
    }

    readonly record struct SocketRow(bool Tcp, int Pid, IPAddress Local, IPAddress? Remote, int RemotePort, string State);

    static List<SocketRow> ReadSockets()
    {
        var rows = new List<SocketRow>();
        ReadTable(true, AddressFamily.InterNetwork, rows);
        ReadTable(true, AddressFamily.InterNetworkV6, rows);
        ReadTable(false, AddressFamily.InterNetwork, rows);
        ReadTable(false, AddressFamily.InterNetworkV6, rows);
        return rows;
    }

    static readonly string[] TcpStates = ["?", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED", "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB"];

    static void ReadTable(bool tcp, AddressFamily family, List<SocketRow> rows)
    {
        int af = family == AddressFamily.InterNetwork ? 2 : 23;
        int size = 0;
        uint Call(nint buffer) => tcp ? GetExtendedTcpTable(buffer, ref size, false, af, 5 /* OWNER_PID_ALL */, 0)
            : GetExtendedUdpTable(buffer, ref size, false, af, 1 /* OWNER_PID */, 0);
        Call(0);
        for (int attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint result = Call(buffer);
                if (result == 122 /* insufficient buffer */) continue;
                if (result != 0) return;
                int count = Marshal.ReadInt32(buffer);
                nint row = buffer + 4;
                bool v4 = af == 2;
                int rowSize = tcp ? (v4 ? 24 : 56) : (v4 ? 12 : 28);
                for (int i = 0; i < count; i++, row += rowSize)
                {
                    if (tcp && v4)
                    {
                        int state = Marshal.ReadInt32(row);
                        if (state == 2) continue; // listening sockets say nothing about routing
                        rows.Add(new(true, Marshal.ReadInt32(row + 20), new IPAddress((uint)Marshal.ReadInt32(row + 4)),
                            new IPAddress((uint)Marshal.ReadInt32(row + 12)), Port(Marshal.ReadInt32(row + 16)), TcpStates.ElementAtOrDefault(state) ?? "?"));
                    }
                    else if (tcp)
                    {
                        int state = Marshal.ReadInt32(row + 48);
                        if (state == 2) continue;
                        rows.Add(new(true, Marshal.ReadInt32(row + 52), Ipv6(row), Ipv6(row + 24), Port(Marshal.ReadInt32(row + 44)), TcpStates.ElementAtOrDefault(state) ?? "?"));
                    }
                    else if (v4)
                        rows.Add(new(false, Marshal.ReadInt32(row + 8), new IPAddress((uint)Marshal.ReadInt32(row)), null, 0, "UDP"));
                    else
                        rows.Add(new(false, Marshal.ReadInt32(row + 24), Ipv6(row), null, 0, "UDP"));
                }
                return;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    static IPAddress Ipv6(nint at)
    {
        var bytes = new byte[16];
        Marshal.Copy(at, bytes, 0, 16);
        return new IPAddress(bytes);
    }

    static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    [DllImport("iphlpapi.dll")]
    static extern uint GetBestInterface(uint destination, out uint index);

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedUdpTable(nint table, ref int size, bool order, int family, int tableClass, uint reserved);
}
