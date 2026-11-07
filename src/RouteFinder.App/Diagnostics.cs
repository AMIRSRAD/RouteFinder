using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RouteFinder.App;

internal sealed record PingReport(string Detail, int Sent = 0, int Received = 0,
    double? AverageRtt = null, double? Jitter = null, bool? TunnelHandshake = null,
    bool TunnelPathUnverified = false, int? EstimatedPeerRtt = null,
    int Timeouts = 0, int NetworkErrors = 0, int ProbeFailures = 0, TcpProbeResult? Tcp = null,
    ulong TunnelReceivedBytes = 0, string? ProxyStatus = null, uint? TunnelError = null)
{
    public string CompareWith(PingReport baseline)
    {
        if (Tcp?.ElapsedMilliseconds is long tcpTime && baseline.Tcp?.ElapsedMilliseconds is long directTime &&
            TunnelHandshake == true && TunnelReceivedBytes > 0)
            return $" · vs Windows: {tcpTime - directTime:+0;-0;0} ms TCP connect time";
        if (Sent == 0 || baseline.Sent == 0 || TunnelPathUnverified) return "";
        double lossDelta = 100.0 * ((double)baseline.Received / baseline.Sent - (double)Received / Sent);
        string comparison = $" · vs Windows: {lossDelta:+0.#;-0.#;0} pp loss";
        if (AverageRtt is double rtt && baseline.AverageRtt is double baselineRtt)
            comparison += $", {rtt - baselineRtt:+0.0;-0.0;0.0} ms RTT";
        return comparison;
    }

    public string Summary => (Tcp is { } tcp ? tcp.Detail : Sent == 0 ? "No measurements" :
        $"{Received}/{Sent} replies · {(Sent - Received) * 100.0 / Sent:0.#}% loss · " +
        $"Avg RTT {AverageRtt?.ToString("0.0") ?? "—"} ms · Jitter {Jitter?.ToString("0.0") ?? "—"} ms") +
        (TunnelHandshake switch { true => " · Peer handshake reported", false => " · No peer handshake reported", null => "" }) +
        (TunnelPathUnverified ? " · Reply path unverified" : "") +
        (ProxyStatus is { } proxy ? $" · SOCKS5 {proxy}" : "") +
        (TunnelError is uint error ? $" · WireSock error 0x{error:X8}" : "") +
        (Timeouts + NetworkErrors + ProbeFailures > 0
            ? $" · Timeouts {Timeouts}, network errors {NetworkErrors}, probe failures {ProbeFailures}" : "") +
        (EstimatedPeerRtt is int peerRtt ? $" · Est. peer RTT {peerRtt} ms" : "");
}

internal sealed record TcpProbeResult(string Detail, bool Connected, long? ElapsedMilliseconds = null);

internal static class Diagnostics
{
    public static async Task<string> ReadSocketSnapshot(CancellationToken cancellation)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "netstat.exe"),
            Arguments = "-ano",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Could not start Windows socket diagnostics.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            string output = await process.StandardOutput.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException("Windows socket diagnostics failed.");
            var names = new Dictionary<int, string>();
            foreach (var app in Process.GetProcesses())
            {
                using (app)
                {
                    try { names[app.Id] = app.ProcessName; }
                    catch (InvalidOperationException) { }
                }
            }
            var rows = output.Split('\n').Select(line => line.Trim())
                .Where(line => line.StartsWith("TCP ", StringComparison.Ordinal) || line.StartsWith("UDP ", StringComparison.Ordinal))
                .Select(line =>
                {
                    int separator = line.LastIndexOf(' ');
                    return separator >= 0 && int.TryParse(line[(separator + 1)..], out int pid) &&
                        names.TryGetValue(pid, out string? name) ? $"{name,-28} {line}" : $"{"Unknown",-28} {line}";
                });
            return string.Join(Environment.NewLine, rows);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
            }
        }
    }

    public static async Task<string> Probe(string host, int count = 4, bool tunnelExpected = false,
        CancellationToken cancellation = default) =>
        (await MeasurePing(host, count, tunnelExpected, cancellation)).Detail;

    public static async Task<PingReport> MeasurePing(string host, int count = 4, bool tunnelExpected = false,
        CancellationToken cancellation = default, int intervalMilliseconds = 0,
        int payloadBytes = 32, int timeoutMilliseconds = 1500, int ttl = 128)
    {
        var lines = new List<string> { $"ICMP reachability test • {host}", tunnelExpected
            ? "Temporary WireSock route for this destination. A blocked ICMP reply does not prove the route is unavailable."
            : "This tests the current Windows route. A blocked ICMP reply does not prove a VPN is unavailable." };
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host, cancellation).WaitAsync(TimeSpan.FromSeconds(5), cancellation); }
        catch (Exception e) when (e is not OperationCanceledException) { return new(string.Join(Environment.NewLine, lines.Append("DNS failed: " + e.Message))); }
        lines.Add("Resolved: " + string.Join(", ", addresses.Select(x => x.ToString())));
        var address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        if (address == null) return new("DNS returned no addresses.");
        var times = new List<long>();
        var variation = new List<long>();
        long? previous = null;
        int timeouts = 0, networkErrors = 0, probeFailures = 0;
        using var ping = new Ping();
        var payload = new byte[payloadBytes];
        var options = new PingOptions(ttl, dontFragment: false);
        for (int i = 0; i < count; i++)
        {
            if (i > 0 && intervalMilliseconds > 0)
                await Task.Delay(intervalMilliseconds, cancellation);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var result = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMilliseconds), payload, options, cancellation);
                if (result.Status == IPStatus.Success)
                {
                    long rtt = result.RoundtripTime;
                    if (previous is long last) variation.Add(Math.Abs(rtt - last));
                    previous = rtt;
                    times.Add(rtt);
                    lines.Add($"Probe {i + 1}: {rtt} ms • Reply TTL {result.Options?.Ttl.ToString() ?? "unavailable"}");
                }
                else
                {
                    if (result.Status == IPStatus.TimedOut) timeouts++;
                    else networkErrors++;
                    previous = null;
                    lines.Add($"Probe {i + 1}: {result.Status}");
                }
            }
            catch (PingException e)
            {
                probeFailures++;
                previous = null;
                lines.Add($"Probe {i + 1} failed locally: {e.Message}");
            }
        }
        int sent = count - probeFailures;
        lines.Add(sent == 0 ? "Loss unavailable because every probe failed locally." :
            $"Loss: {(sent - times.Count) * 100.0 / sent:0}%" + (times.Count > 0 ? $" • Min/avg/max: {times.Min()}/{times.Average():0.0}/{times.Max()} ms" : ""));
        lines.Add($"Timeouts: {timeouts} • Network errors: {networkErrors} • Local failures: {probeFailures}");
        lines.Add(variation.Count == 0 ? "Consecutive RTT variation unavailable." :
            $"Mean consecutive RTT variation: {variation.Average():0.0} ms");
        return new(string.Join(Environment.NewLine, lines), sent, times.Count,
            times.Count == 0 ? null : times.Average(), variation.Count == 0 ? null : variation.Average(),
            Timeouts: timeouts, NetworkErrors: networkErrors, ProbeFailures: probeFailures);
    }

    public static async Task<string> TcpProbe(string host, int port, CancellationToken cancellation = default,
        int timeoutMilliseconds = 4000) =>
        (await MeasureTcp(host, port, cancellation, timeoutMilliseconds)).Detail;

    public static async Task<TcpProbeResult> MeasureTcp(string host, int port, CancellationToken cancellation,
        int timeoutMilliseconds)
    {
        using var client = new TcpClient();
        var timer = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(timeoutMilliseconds));
        try
        {
            await client.ConnectAsync(host, port, deadline.Token);
            long elapsed = timer.ElapsedMilliseconds;
            return new($"TCP {host} port {port} reachable in {elapsed} ms", true, elapsed);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new($"TCP {host} port {port} timed out", false); }
        catch (SocketException e) { return new($"TCP {host} port {port} failed: {e.Message}", false); }
    }
}
