using System.ComponentModel;
using System.Net.Sockets;
using RouteFinder.Core;

sealed record TunnelCheckResult(bool Started, ulong SentBytes, ulong ReceivedBytes, long? HandshakeAgeMilliseconds, string? Error, TunnelEventCount[] Events);
sealed record TunnelEventCount(uint Type, uint Status, int Count);

static class TunnelCheck
{
    public static async Task<TunnelCheckResult> RunAsync(TunnelProfile profile, CancellationToken ct, bool portable)
    {
        // Each phase uses a new SDK handle and a new socket. A cached handshake
        // from the active phase therefore cannot count as success after release.
        using var tunnel = new WireSockTunnel(portable: portable, observeEvents: true);
        var events = new System.Collections.Concurrent.ConcurrentDictionary<(uint Type, uint Status), int>();
        tunnel.TunnelEvent += (type, status) => events.AddOrUpdate((type, status), 1, (_, count) => count + 1);
        TunnelEventCount[] Snapshot() => events.ToArray().OrderBy(entry => entry.Key)
            .Select(entry => new TunnelEventCount(entry.Key.Type, entry.Key.Status, entry.Value)).ToArray();
        // Trigger a handshake even if the packet filter sees no application traffic.
        var probeProfile = new TunnelProfile
        {
            Name = "RouteFinder handshake check",
            Interface = new(profile.Interface, StringComparer.OrdinalIgnoreCase),
            Peer = new(profile.Peer, StringComparer.OrdinalIgnoreCase)
        };
        probeProfile.Peer["PersistentKeepalive"] = "5";
        try
        {
            tunnel.Connect(probeProfile, [Environment.ProcessPath!], networkLock: false);
        }
        catch (Win32Exception error)
        {
            return new(false, 0, 0, null, $"WireSock start failed (Windows error {error.NativeErrorCode}).", Snapshot());
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            using var socket = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                // The TCP handshake creates traffic from the selected audit executable.
                // The socket itself may succeed directly, so only the SDK's receive
                // counters count as proof that traffic came back through the tunnel.
                await socket.ConnectAsync("1.1.1.1", 443, deadline.Token);
            }
            catch (SocketException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var state = tunnel.GetState();
            if (state.RxBytes > 0 || state.TimeSinceLastHandshakeMilliseconds >= 0)
                return new(true, state.TxBytes, state.RxBytes, state.TimeSinceLastHandshakeMilliseconds, null, Snapshot());
        }
        var final = tunnel.GetState();
        return new(true, final.TxBytes, final.RxBytes, final.TimeSinceLastHandshakeMilliseconds, "WireSock reported no handshake or received traffic during the probe.", Snapshot());
    }
}
