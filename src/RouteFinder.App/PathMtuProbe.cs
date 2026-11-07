using System.Net.NetworkInformation;
using System.Net;

namespace RouteFinder.App;

/// <summary>Estimates the current IPv4 path MTU with ICMP echo probes using the Don't Fragment flag.</summary>
internal static class PathMtuProbe
{
    static readonly SemaphoreSlim ProbeGate = new(1, 1);
    static readonly object CacheGate = new();
    static int? cachedMtu;
    static IPAddress? cachedAddress;
    static DateTimeOffset cacheTime;
    static int networkGeneration;

    static PathMtuProbe()
    {
        NetworkChange.NetworkAddressChanged += (_, _) =>
        {
            lock (CacheGate)
            {
                cachedMtu = null;
                networkGeneration++;
            }
        };
    }

    public static async Task<int?> MeasureAsync(IPAddress address, CancellationToken cancellation = default)
    {
        await ProbeGate.WaitAsync(cancellation);
        try
        {
            int generation;
            lock (CacheGate)
            {
                if (cachedMtu is { } cached && address.Equals(cachedAddress) &&
                    DateTimeOffset.UtcNow - cacheTime < TimeSpan.FromMinutes(10)) return cached;
                generation = networkGeneration;
            }
            int? measured = null;
            using (var ping = new Ping())
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var baseline = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(500), new byte[32], new PingOptions(64, false), cancellation);
                    if (baseline.Status != IPStatus.Success) return null;
                    var baselineDf = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(400), new byte[32], new PingOptions(64, true), cancellation);
                    if (baselineDf.Status != IPStatus.Success) return null;

                    int low = 32, high = 1472;
                    bool inconclusive = false;
                    while (low < high)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int payload = (low + high + 1) / 2;
                        var response = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(400), new byte[payload], new PingOptions(64, true), cancellation);
                        if (response.Status == IPStatus.Success) low = payload;
                        else if (response.Status == IPStatus.PacketTooBig) high = payload - 1;
                        else
                        {
                            inconclusive = true;
                            break;
                        }
                    }
                    if (inconclusive) return null;
                    measured = low + 28; // IPv4 header and ICMP header
                }
                catch (PingException) { }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            }
            lock (CacheGate)
            {
                if (generation != networkGeneration) return null;
                if (measured is { } value)
                {
                    cachedMtu = value;
                    cachedAddress = address;
                    cacheTime = DateTimeOffset.UtcNow;
                }
                return measured;
            }
        }
        finally { ProbeGate.Release(); }
    }
}
