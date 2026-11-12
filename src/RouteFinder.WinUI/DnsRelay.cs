using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

/// <summary>
/// Windows looks up names for every app in one system service, so a Through VPN app's lookups never enter the tunnel
/// and a filtered network answers them (for example discord.com -> 10.10.34.36, a block page). While connected,
/// RouteFinder runs this relay as a separate tunneled process and points Windows' resolver at it with a name
/// resolution policy, so lookups are answered by the route's DNS server through the VPN.
/// </summary>
internal static class DnsRelay
{
    internal const string ListenAddress = "127.0.0.53";
    internal const string PolicyComment = "RouteFinder DNS relay";
    internal const string RelayFileName = "RouteFinder.DnsRelay.exe";

    static long queries, failures;

    /// <summary>Relay process entry: --dns-relay &lt;upstream,upstream&gt; &lt;parent pid&gt;.</summary>
    internal static void Run(string[] upstreams, int parentPid, string? logFolder = null)
    {
        if (logFolder is { Length: > 1 })
            try { DiagnosticLog.Start(logFolder); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Log($"DNS relay pid {Environment.ProcessId} as {Environment.UserName} for RouteFinder pid {parentPid} · listening on {ListenAddress}:53 · upstream {string.Join(", ", upstreams)}");
        var listen = IPAddress.Parse(ListenAddress);
        try
        {
            var udp = new UdpClient(new IPEndPoint(listen, 53));
            _ = Task.Run(() => UdpLoop(udp, upstreams));
            var tcp = new TcpListener(listen, 53);
            tcp.Start();
            _ = Task.Run(() => TcpLoop(tcp, upstreams));
            Log("Bound UDP and TCP port 53; waiting for lookups.");
        }
        catch (SocketException error)
        {
            Log($"Could not listen on {ListenAddress}:53: {error.Message}");
            DiagnosticLog.Flush();
            Environment.Exit(2);
        }
        new Thread(() =>
        {
            try { Process.GetProcessById(parentPid).WaitForExit(); } catch (ArgumentException) { }
            Log($"RouteFinder exited; relay stopping after {queries} lookups ({failures} failed).");
            DiagnosticLog.Flush();
            Environment.Exit(0);
        }) { IsBackground = true }.Start();
        new Timer(_ => Log($"Relay alive · {Interlocked.Read(ref queries)} lookups · {Interlocked.Read(ref failures)} failed"), null, 60_000, 60_000);
    }

    static void Log(string message) => DiagnosticLog.Write("RELAY", message);

    static async Task UdpLoop(UdpClient server, string[] upstreams)
    {
        while (true)
        {
            UdpReceiveResult request;
            try { request = await server.ReceiveAsync(); }
            catch (SocketException) { continue; }
            _ = Task.Run(async () =>
            {
                var answer = await Forward(request.Buffer, upstreams);
                if (answer is not null)
                {
                    try { await server.SendAsync(answer, answer.Length, request.RemoteEndPoint); }
                    catch (SocketException error) { Log($"Could not reply to {request.RemoteEndPoint}: {error.Message}"); }
                }
            });
        }
    }

    static async Task TcpLoop(TcpListener server, string[] upstreams)
    {
        while (true)
        {
            TcpClient client;
            try { client = await server.AcceptTcpClientAsync(); }
            catch (SocketException) { continue; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var length = new byte[2];
                        await stream.ReadExactlyAsync(length);
                        var query = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
                        await stream.ReadExactlyAsync(query);
                        var answer = await Forward(query, upstreams, tcp: true);
                        if (answer is null) return;
                        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)answer.Length);
                        await stream.WriteAsync(length);
                        await stream.WriteAsync(answer);
                    }
                    catch (Exception error) when (error is IOException or SocketException or EndOfStreamException) { }
                }
            });
        }
    }

    // DNS over HTTPS first: plain DNS on port 53 is answered with the filter's block page on this kind of network,
    // while HTTPS to 1.1.1.1 / 8.8.8.8 cannot be tampered with. Plain servers are a last resort, and block-page
    // answers from them are rejected.
    static async Task<byte[]?> Forward(byte[] query, string[] upstreams, bool tcp = false)
    {
        Interlocked.Increment(ref queries);
        string name = QuestionName(query);
        var started = Stopwatch.StartNew();
        // Names only the local network knows (printers, PCs, the router) go to that network's own DNS.
        if (IsLocalName(name) && LocalDnsServers() is { Length: > 0 } local)
        {
            foreach (string server in local)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(1500);
                    using var client = new UdpClient(AddressFamily.InterNetwork);
                    client.Connect(server, 53);
                    await client.SendAsync(query, timeout.Token);
                    var answer = (await client.ReceiveAsync(timeout.Token)).Buffer;
                    if (answer.Length < 12 || answer[0] != query[0] || answer[1] != query[1]) continue;
                    Log($"{name} {QuestionType(query)} -> {DescribeAnswer(answer)} · via local DNS {server} · {started.ElapsedMilliseconds} ms");
                    return answer;
                }
                catch (Exception error) when (error is SocketException or OperationCanceledException) { }
            }
        }
        foreach (string doh in DohServers)
        {
            try
            {
                using var timeout = new CancellationTokenSource(5000);
                using var content = new ByteArrayContent(query);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");
                using var response = await Http.PostAsync(doh, content, timeout.Token);
                var answer = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                if (!response.IsSuccessStatusCode || answer.Length < 12) { Log($"{name} via {doh} failed: HTTP {(int)response.StatusCode}"); continue; }
                answer[0] = query[0];
                answer[1] = query[1];
                Log($"{name} {QuestionType(query)} -> {DescribeAnswer(answer)} · via {doh} · {started.ElapsedMilliseconds} ms");
                return answer;
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
            {
                Log($"{name} via {doh} failed: {error.GetType().Name} {error.Message}");
            }
        }
        foreach (string upstream in upstreams)
        {
            try
            {
                using var timeout = new CancellationTokenSource(tcp ? 4000 : 2000);
                byte[] answer;
                if (tcp)
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(upstream, 53, timeout.Token);
                    var stream = client.GetStream();
                    var length = new byte[2];
                    BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)query.Length);
                    await stream.WriteAsync(length, timeout.Token);
                    await stream.WriteAsync(query, timeout.Token);
                    await stream.ReadExactlyAsync(length, timeout.Token);
                    answer = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
                    await stream.ReadExactlyAsync(answer, timeout.Token);
                }
                else
                {
                    using var client = new UdpClient(AddressFamily.InterNetwork);
                    client.Connect(upstream, 53);
                    await client.SendAsync(query, timeout.Token);
                    answer = (await client.ReceiveAsync(timeout.Token)).Buffer;
                }
                if (answer.Length < 12 || answer[0] != query[0] || answer[1] != query[1]) continue;
                string described = DescribeAnswer(answer);
                if (IsBlockPage(answer))
                {
                    Log($"{name} {QuestionType(query)} -> {described} · via {upstream} is a block page; rejected.");
                    continue;
                }
                Log($"{name} {QuestionType(query)} -> {described} · via {upstream}{(tcp ? " TCP" : "")} (plain DNS fallback) · {started.ElapsedMilliseconds} ms");
                return answer;
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException or IOException)
            {
                Log($"{name} via {upstream} failed: {error.GetType().Name} {error.Message}");
            }
        }
        Interlocked.Increment(ref failures);
        return null;
    }

    static readonly string[] DohServers = ["https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query"];

    static readonly string[] LocalSuffixes = [".lan", ".local", ".home", ".internal", ".localdomain", ".home.arpa", ".corp", ".intranet"];

    internal static bool IsLocalName(string name)
    {
        string lower = name.TrimEnd('.').ToLowerInvariant();
        if (lower.Length == 0 || lower == "(no name)") return false;
        if (!lower.Contains('.')) return true; // single-label names such as a PC name
        if (LocalSuffixes.Any(lower.EndsWith)) return true;
        // Reverse lookups of private IPv4 addresses.
        if (lower.EndsWith(".in-addr.arpa", StringComparison.Ordinal))
        {
            var parts = lower[..^".in-addr.arpa".Length].Split('.').Reverse().ToArray();
            if (parts.Length >= 2 && int.TryParse(parts[0], out int first) && int.TryParse(parts[1], out int second))
                return first == 10 || first == 127 || first == 192 && second == 168 || first == 172 && second is >= 16 and < 32 || first == 169 && second == 254;
        }
        return false;
    }

    static string[]? localDns;
    static DateTime localDnsAt;

    // The adapters' own IPv4 DNS servers (from DHCP or settings), refreshed every minute.
    static string[] LocalDnsServers()
    {
        if (localDns is not null && DateTime.UtcNow - localDnsAt < TimeSpan.FromMinutes(1)) return localDns;
        try
        {
            localDns = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                    adapter.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))
                .SelectMany(adapter => adapter.GetIPProperties().DnsAddresses)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Select(address => address.ToString()).Distinct().ToArray();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { localDns = []; }
        localDnsAt = DateTime.UtcNow;
        return localDns;
    }
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    // The filter's block page lives in 10.10.34.0/24; an answer made only of such addresses is not a real one.
    static bool IsBlockPage(byte[] answer) => Addresses(answer) is { Count: > 0 } found && found.All(IsBlockAddress);

    internal static bool IsBlockAddress(string address) => address.StartsWith("10.10.34.", StringComparison.Ordinal);

    /// <summary>One-shot mode for the hosts-file fix: --dns-resolve &lt;names&gt; &lt;upstreams&gt; &lt;output file&gt;.</summary>
    internal static void ResolveOnce(string[] names, string[] upstreams, string output, string? logFolder = null)
    {
        if (logFolder is { Length: > 1 })
            try { DiagnosticLog.Start(logFolder); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Log($"Resolving {names.Length} name(s) through the tunnel: {string.Join(", ", names)}");
        var lines = new List<string>();
        foreach (string name in names)
        {
            var query = new List<byte> { (byte)Random.Shared.Next(256), (byte)Random.Shared.Next(256), 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (label.Length > 63) continue;
                query.Add((byte)label.Length);
                query.AddRange(Encoding.ASCII.GetBytes(label));
            }
            query.AddRange([0, 0, 1, 0, 1]);
            var answer = Forward(query.ToArray(), upstreams).GetAwaiter().GetResult();
            var addresses = answer is null ? [] : Addresses(answer).Where(address => !IsBlockAddress(address)).ToList();
            if (addresses.Count > 0) lines.Add($"{name} {string.Join(" ", addresses)}");
        }
        File.WriteAllLines(output, lines);
        Log($"Resolved {lines.Count} of {names.Length} name(s).");
        DiagnosticLog.Flush();
    }

    static string QuestionName(byte[] message)
    {
        var name = new StringBuilder();
        int offset = 12;
        while (offset < message.Length && message[offset] is > 0 and < 64)
        {
            int length = message[offset];
            if (offset + 1 + length > message.Length) break;
            if (name.Length > 0) name.Append('.');
            name.Append(Encoding.ASCII.GetString(message, offset + 1, length));
            offset += 1 + length;
        }
        return name.Length == 0 ? "(no name)" : name.ToString();
    }

    static int QuestionEnd(byte[] message)
    {
        int offset = 12;
        while (offset < message.Length && message[offset] != 0) offset += 1 + message[offset];
        return offset + 5; // zero label, type, class
    }

    static string QuestionType(byte[] message)
    {
        int end = QuestionEnd(message);
        if (end > message.Length) return "?";
        int type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(end - 4));
        return type switch { 1 => "A", 28 => "AAAA", 5 => "CNAME", 65 => "HTTPS", 12 => "PTR", 33 => "SRV", 16 => "TXT", _ => $"type {type}" };
    }

    // Addresses in the answer section, plus the response code when it is not success.
    static string DescribeAnswer(byte[] message)
    {
        int code = message[3] & 0x0F;
        var found = Addresses(message);
        string codeText = code switch { 0 => "", 2 => "SERVFAIL ", 3 => "NXDOMAIN ", 5 => "REFUSED ", _ => $"rcode {code} " };
        return codeText + (found.Count == 0 ? "(no addresses)" : string.Join(", ", found));
    }

    static List<string> Addresses(byte[] message)
    {
        if (message.Length < 12) return [];
        int count = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6));
        var found = new List<string>();
        int offset = QuestionEnd(message);
        for (int i = 0; i < count && offset < message.Length; i++)
        {
            // Name: a pointer (two bytes) or a run of labels.
            if ((message[offset] & 0xC0) == 0xC0) offset += 2;
            else { while (offset < message.Length && message[offset] != 0) offset += 1 + message[offset]; offset++; }
            if (offset + 10 > message.Length) break;
            int type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
            int length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8));
            offset += 10;
            if (offset + length > message.Length) break;
            if (type == 1 && length == 4) found.Add(new IPAddress(message.AsSpan(offset, 4)).ToString());
            else if (type == 28 && length == 16) found.Add(new IPAddress(message.AsSpan(offset, 16)).ToString());
            offset += length;
        }
        return found;
    }
}

public sealed partial class MainWindow
{
    bool dnsPolicyActive;

    static string DnsRelayPath => Path.Combine(AppContext.BaseDirectory, DnsRelay.RelayFileName);

    // The relay is used for transparent tunnels; virtual adapter mode and console sharing set DNS on their own adapter.
    bool UsesDnsRelay(bool console) => preferences.DnsThroughVpn && !console && !preferences.PortableEngine;

    /// <summary>
    /// Copies RouteFinder's launcher under a separate name, so the relay is its own app to WireSock and Windows while
    /// still running this build. Returns the path, or null when it cannot be prepared.
    /// </summary>
    static string? PrepareDnsRelay()
    {
        try
        {
            string source = Environment.ProcessPath ?? throw new InvalidOperationException("RouteFinder's path is unknown.");
            var from = new FileInfo(source);
            var to = new FileInfo(DnsRelayPath);
            if (!to.Exists || to.Length != from.Length || to.LastWriteTimeUtc < from.LastWriteTimeUtc)
            {
                foreach (var stray in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(DnsRelay.RelayFileName)))
                    try { stray.Kill(); stray.WaitForExit(2000); } catch (Exception) { }
                File.Copy(source, to.FullName, overwrite: true);
                Trace("DNS", $"Prepared the DNS relay launcher at {to.FullName}.");
            }
            return to.FullName;
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("DNS", "Could not prepare the DNS relay", error);
            return null;
        }
    }

    const string RelayTask = "RouteFinder DNS relay";

    /// <summary>
    /// Starts the relay as SYSTEM through Task Scheduler. RouteFinder's adapter rules apply to the signed-in user's
    /// apps and also catch their loopback DNS traffic ("address not valid"), so a relay running as the user could
    /// not answer Windows. Then points Windows' resolver at it and checks that a real lookup works.
    /// </summary>
    async Task StartDnsRelay(string? tunnelDns)
    {
        await Task.Run(StopRelayTask);
        var upstreams = (tunnelDns ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(value => IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
            .Distinct().ToArray();
        if (upstreams.Length == 0) upstreams = ["1.1.1.1"];
        string command = $"\"{DnsRelayPath}\" --dns-relay {string.Join(",", upstreams)} {Environment.ProcessId} \"{(DiagnosticLog.Enabled ? LogFolder : "-")}\"";
        if (command.Length > 255)
        {
            Trace("DNS", $"The relay command is too long for Task Scheduler ({command.Length} characters); fixing blocked names in the hosts file instead.");
            AddActivity("Fixing blocked names through the VPN instead.");
            _ = Task.Run(() => FixBlockedNames(LoadSavedFixes(), tunnelDns));
            return;
        }
        string created = await Task.Run(() => RunTool("schtasks.exe", "/Create", "/TN", RelayTask, "/TR", command, "/SC", "ONCE", "/ST", "00:00",
            "/RU", "SYSTEM", "/RL", "HIGHEST", "/F"));
        string ran = await Task.Run(() => RunTool("schtasks.exe", "/Run", "/TN", RelayTask));
        Trace("DNS", $"Started the DNS relay as SYSTEM · upstream {string.Join(", ", upstreams)}\ncreate: {created}\nrun: {ran}");
        await Task.Delay(1500);
        bool running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(DnsRelay.RelayFileName)).Length > 0;
        if (!running)
        {
            Trace("DNS", "The DNS relay is not running; fixing blocked names in the hosts file instead.");
            AddActivity("Fixing blocked names through the VPN instead.");
            await Task.Run(StopRelayTask);
            _ = Task.Run(() => FixBlockedNames(LoadSavedFixes(), tunnelDns));
            return;
        }
        string result = await Task.Run(() => RunPowerShell(
            $"Get-DnsClientNrptRule | Where-Object Comment -eq '{DnsRelay.PolicyComment}' | Remove-DnsClientNrptRule -Force; " +
            $"Add-DnsClientNrptRule -Namespace '.' -NameServers '{DnsRelay.ListenAddress}' -Comment '{DnsRelay.PolicyComment}' -ErrorAction Stop | Out-Null; " +
            "Clear-DnsClientCache; 'ok'"));
        dnsPolicyActive = result.Trim().EndsWith("ok", StringComparison.Ordinal);
        if (!dnsPolicyActive)
        {
            Trace("DNS", $"Could not set the name resolution policy: {result}");
            await Task.Run(StopRelayTask);
            _ = Task.Run(() => FixBlockedNames(LoadSavedFixes(), tunnelDns));
            return;
        }
        // The real path: Windows' own resolver, through the policy, to the relay.
        string? problem = null;
        try
        {
            using var timeout = new CancellationTokenSource(8000);
            var found = await Dns.GetHostAddressesAsync("discord.com", AddressFamily.InterNetwork, timeout.Token);
            if (found.Length == 0) problem = "no addresses";
            else if (found.All(address => DnsRelay.IsBlockAddress(address.ToString()))) problem = $"still the block page ({found[0]})";
            else Trace("DNS", $"Check through Windows' resolver: discord.com -> {string.Join(", ", found.Select(address => address.ToString()))}");
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException) { problem = error.Message; }
        if (problem is not null)
        {
            Trace("DNS", $"Lookups through the relay do not work ({problem}); restoring Windows' DNS and fixing blocked names in the hosts file instead.");
            await StopDnsRelay();
            AddActivity("Fixing blocked names through the VPN instead.");
            _ = Task.Run(() => FixBlockedNames(LoadSavedFixes(), tunnelDns));
            return;
        }
        Trace("DNS", "Windows now resolves every name through RouteFinder's secure DNS relay.");
        AddActivity("DNS lookups now go through secure DNS.");
    }

    // Ends and deletes the scheduled relay; the relay also exits by itself when RouteFinder does.
    static void StopRelayTask()
    {
        RunTool("schtasks.exe", "/End", "/TN", RelayTask);
        RunTool("schtasks.exe", "/Delete", "/TN", RelayTask, "/F");
        foreach (var stray in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(DnsRelay.RelayFileName)))
            try { stray.Kill(); stray.WaitForExit(2000); } catch (Exception) { }
    }

    static string RunTool(string tool, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, tool))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000)) { try { process.Kill(); } catch (Exception) { } return "timed out"; }
            return (output.Result + errors.Result).Trim().Replace("\r\n", " ").Replace("\n", " ");
        }
        catch (Exception error) { return $"failed: {error.Message}"; }
    }

    // Windows' normal DNS comes back before the tunnel goes away, so lookups never point at a dead relay.
    async Task StopDnsRelay()
    {
        await Task.Run(RemoveHostsBlock);
        if (dnsPolicyActive)
        {
            dnsPolicyActive = false;
            string result = await Task.Run(() => RunPowerShell($"Get-DnsClientNrptRule | Where-Object Comment -eq '{DnsRelay.PolicyComment}' | Remove-DnsClientNrptRule -Force; Clear-DnsClientCache; 'ok'"));
            Trace("DNS", $"Removed the name resolution policy: {result.Trim()}");
        }
        await Task.Run(StopDnsRelayProcess);
    }

    void StopDnsRelayProcess()
    {
        StopRelayTask();
        Trace("DNS", "Stopped the DNS relay.");
    }

    // A crash could leave the policy pointing at a relay that no longer runs; clear it at startup.
    void ClearLeftoverDnsPolicy()
    {
        if (preview || verifying) return;
        _ = Task.Run(() =>
        {
            StopRelayTask();
            RemoveHostsBlock();
            string result = RunPowerShell($"$r = @(Get-DnsClientNrptRule | Where-Object Comment -eq '{DnsRelay.PolicyComment}'); $r | Remove-DnsClientNrptRule -Force; if ($r.Count) {{ Clear-DnsClientCache }}; \"removed $($r.Count)\"");
            Trace("DNS", $"Startup check for a leftover name resolution policy: {result.Trim()}");
        });
    }

    // ---------- Hosts-file fix: correct addresses for names the network filters ----------

    const string HostsBegin = "# >>> RouteFinder: names fixed through the VPN (removed on disconnect)";
    const string HostsEnd = "# <<< RouteFinder";
    static string HostsPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
    static string SavedFixesPath => Path.Combine(Path.GetDirectoryName(LogFolder)!, "dns-fixes.txt");
    static readonly object hostsGate = new();
    readonly Dictionary<string, string[]> hostsFixes = new(StringComparer.OrdinalIgnoreCase);
    DateTime lastFixAt;

    static string[] LoadSavedFixes()
    {
        try { return File.Exists(SavedFixesPath) ? File.ReadAllLines(SavedFixesPath).Where(IsHostName).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToArray() : []; }
        catch (IOException) { return []; }
    }

    static bool IsHostName(string name) => name.Length is > 3 and < 254 && name.Contains('.') &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    /// <summary>
    /// Resolves the names through the tunnel (the relay runs as a tunneled app) and pins the real addresses in the
    /// hosts file, so Windows stops handing apps the filter's block page. Runs on a background thread.
    /// </summary>
    void FixBlockedNames(IEnumerable<string> candidates, string? tunnelDns)
    {
        string[] names;
        lock (hostsGate)
        {
            names = candidates.Where(IsHostName).Where(name => !hostsFixes.ContainsKey(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length == 0 || DateTime.UtcNow - lastFixAt < TimeSpan.FromSeconds(8)) return;
            lastFixAt = DateTime.UtcNow;
        }
        if (!File.Exists(DnsRelayPath)) { Trace("DNS", "Cannot fix blocked names: the relay launcher is missing."); return; }
        var upstreams = (tunnelDns ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Append("1.1.1.1").Distinct().ToArray();
        string output = Path.Combine(Path.GetTempPath(), $"routefinder-dns-{Environment.ProcessId}-{Environment.TickCount64}.txt");
        try
        {
            var start = new ProcessStartInfo(DnsRelayPath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "--dns-resolve", string.Join(",", names), string.Join(",", upstreams), output, DiagnosticLog.Enabled ? LogFolder : "-" })
                start.ArgumentList.Add(argument);
            using (var process = Process.Start(start))
            {
                if (process is null || !process.WaitForExit(60_000)) { try { process?.Kill(); } catch (Exception) { } Trace("DNS", "Fixing blocked names timed out."); return; }
            }
            var results = File.Exists(output) ? File.ReadAllLines(output) : [];
            lock (hostsGate)
            {
                foreach (string line in results)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && IsHostName(parts[0]))
                        hostsFixes[parts[0]] = parts.Skip(1).Where(part => IPAddress.TryParse(part, out _)).Take(2).ToArray();
                }
                WriteHostsBlock();
            }
            var saved = LoadSavedFixes().Concat(results.Select(line => line.Split(' ')[0])).Distinct(StringComparer.OrdinalIgnoreCase).Take(200);
            File.WriteAllLines(SavedFixesPath, saved);
            FlushDnsCache("fixed blocked names");
            Trace("DNS", $"Fixed {results.Length} of {names.Length} blocked name(s) through the VPN:\n{string.Join("\n", results)}");
        }
        catch (Exception error) { DiagnosticLog.Error("DNS", "Could not fix blocked names", error); }
        finally { try { File.Delete(output); } catch (IOException) { } }
    }

    void WriteHostsBlock()
    {
        try
        {
            var kept = StripHostsBlock(File.Exists(HostsPath) ? File.ReadAllLines(HostsPath) : []);
            if (hostsFixes.Count > 0)
            {
                kept.Add(HostsBegin);
                foreach (var (name, addresses) in hostsFixes.OrderBy(pair => pair.Key))
                    foreach (string address in addresses) kept.Add($"{address} {name}");
                kept.Add(HostsEnd);
            }
            File.WriteAllLines(HostsPath, kept, new UTF8Encoding(false));
            Trace("DNS", $"Hosts file now pins {hostsFixes.Count} name(s).");
        }
        catch (Exception error) { DiagnosticLog.Error("DNS", "Could not update the hosts file (security software may protect it)", error); }
    }

    void RemoveHostsBlock()
    {
        lock (hostsGate)
        {
            hostsFixes.Clear();
            try
            {
                if (!File.Exists(HostsPath)) return;
                var lines = File.ReadAllLines(HostsPath);
                if (!lines.Contains(HostsBegin)) return;
                File.WriteAllLines(HostsPath, StripHostsBlock(lines), new UTF8Encoding(false));
                FlushDnsCache("removed hosts fixes");
                Trace("DNS", "Removed RouteFinder's names from the hosts file.");
            }
            catch (Exception error) { DiagnosticLog.Error("DNS", "Could not clean the hosts file", error); }
        }
    }

    static List<string> StripHostsBlock(IEnumerable<string> lines)
    {
        var kept = new List<string>();
        bool inside = false;
        foreach (string line in lines)
        {
            if (line == HostsBegin) { inside = true; continue; }
            if (inside && line == HostsEnd) { inside = false; continue; }
            if (!inside) kept.Add(line);
        }
        while (kept.Count > 0 && kept[^1].Length == 0) kept.RemoveAt(kept.Count - 1);
        return kept;
    }

    static string RunPowerShell(string command)
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20_000)) { try { process.Kill(); } catch (Exception) { } return "timed out"; }
            return (output.Result + errors.Result).Trim();
        }
        catch (Exception error) { return $"failed: {error.Message}"; }
    }
}
