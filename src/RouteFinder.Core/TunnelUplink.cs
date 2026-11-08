using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RouteFinder.Core;

internal sealed class TunnelUplink(TunnelUplink.SockAddress destination, uint index,
    string adapterName, string endpoint, TunnelUplink.RouteRow? ownedRoute) : IDisposable
{
    private long renewedAt = Environment.TickCount64;
    private const uint RouteLifetimeSeconds = 300;
    internal string AdapterName => adapterName;
    internal string Endpoint => endpoint;

    internal static uint? DefaultIpv4InterfaceIndex()
    {
        // Route lookup only. The documentation address is never contacted.
        var destination = SockAddress.From(IPAddress.Parse("192.0.2.1"));
        return GetBestInterfaceEx(ref destination, out uint index) == 0 ? index : null;
    }

    internal static TunnelUplink Open(string adapterId, string endpoint)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
            string.Equals(item.Id, adapterId, StringComparison.OrdinalIgnoreCase));
        if (adapter?.OperationalStatus != OperationalStatus.Up)
            throw new InvalidOperationException("The saved internet adapter is unavailable. Reconnect it or choose another adapter in Route options.");

        var (host, port) = TunnelProfile.SplitEndpoint(endpoint);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        IPAddress[] addresses = IPAddress.TryParse(host, out var address) ? [address]
            : Dns.GetHostAddressesAsync(host, deadline.Token).GetAwaiter().GetResult();
        var properties = adapter.GetIPProperties();
        foreach (var ip in addresses.Distinct())
        {
            bool ipv4 = ip.AddressFamily == AddressFamily.InterNetwork;
            if (!adapter.Supports(ipv4 ? NetworkInterfaceComponent.IPv4 : NetworkInterfaceComponent.IPv6)) continue;
            int? interfaceIndex = ipv4 ? properties.GetIPv4Properties()?.Index : properties.GetIPv6Properties()?.Index;
            if (interfaceIndex is not > 0) continue;
            uint index = (uint)interfaceIndex.Value;
            var destination = SockAddress.From(ip);
            if (GetBestRoute2(0, index, 0, ref destination, 0, out var nextRoute, out _) != 0 ||
                nextRoute.InterfaceIndex != index || nextRoute.Loopback != 0)
                continue;

            // A host route keeps the VPN endpoint on this uplink even when default-route metrics differ.
            RouteRow route = default;
            route.InterfaceLuid = nextRoute.InterfaceLuid;
            route.InterfaceIndex = index;
            route.Destination = destination;
            route.PrefixLength = ipv4 ? (byte)32 : (byte)128;
            route.NextHop = nextRoute.NextHop;
            route.ValidLifetime = route.PreferredLifetime = RouteLifetimeSeconds;
            route.Protocol = 3; // MIB_IPPROTO_NETMGMT
            uint result = CreateIpForwardEntry2(ref route);
            const uint alreadyExists = 5010;
            if (result != 0 && result != alreadyExists)
                throw new Win32Exception((int)result, $"Windows could not route the VPN endpoint through {adapter.Name}.");

            var uplink = new TunnelUplink(destination, index, adapter.Name,
                ipv4 ? $"{ip}:{port}" : $"[{ip}]:{port}", result == 0 ? route : null);
            try { uplink.Verify(); return uplink; }
            catch { uplink.Dispose(); throw; }
        }
        throw new InvalidOperationException($"Windows has no usable path to the VPN endpoint through {adapter.Name}.");
    }

    internal bool IsCurrent
    {
        get
        {
            if (GetBestInterfaceEx(ref destination, out uint best) != 0 || best != index) return false;
            if (ownedRoute is not { } route || Environment.TickCount64 - renewedAt < 60_000) return true;
            // Let the route expire if RouteFinder exits before it can remove the entry.
            if (SetIpForwardEntry2(ref route) != 0) return false;
            renewedAt = Environment.TickCount64;
            return true;
        }
    }

    internal void Verify()
    {
        if (!IsCurrent)
            throw new InvalidOperationException($"Windows could not select {adapterName} for the VPN endpoint. Check that it is connected and that no competing endpoint route is installed.");
    }

    public void Dispose()
    {
        if (ownedRoute is not { } route) return;
        uint result = DeleteIpForwardEntry2(ref route);
        if (result != 0 && result != 2 && result != 1168)
            throw new Win32Exception((int)result, $"Windows could not remove the temporary VPN endpoint route for {adapterName}.");
        ownedRoute = null;
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    internal struct SockAddress
    {
        [FieldOffset(0)] internal ushort Family;
        [FieldOffset(4)] internal uint V4;
        [FieldOffset(8)] internal ulong V6First;
        [FieldOffset(16)] internal ulong V6Last;
        [FieldOffset(24)] internal uint Scope;

        internal static SockAddress From(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return address.AddressFamily == AddressFamily.InterNetwork
                ? new() { Family = 2, V4 = BinaryPrimitives.ReadUInt32LittleEndian(bytes) }
                : new() { Family = 23, V6First = BinaryPrimitives.ReadUInt64LittleEndian(bytes),
                    V6Last = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)), Scope = (uint)address.ScopeId };
        }
    }

    // Native MIB_IPFORWARD_ROW2 layout. The remaining flags and metric default to zero.
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    internal struct RouteRow
    {
        [FieldOffset(0)] internal ulong InterfaceLuid;
        [FieldOffset(8)] internal uint InterfaceIndex;
        [FieldOffset(12)] internal SockAddress Destination;
        [FieldOffset(40)] internal byte PrefixLength;
        [FieldOffset(44)] internal SockAddress NextHop;
        [FieldOffset(76)] internal uint ValidLifetime;
        [FieldOffset(80)] internal uint PreferredLifetime;
        [FieldOffset(88)] internal uint Protocol;
        [FieldOffset(92)] internal byte Loopback;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestRoute2(nint luid, uint index, nint source, ref SockAddress destination,
        uint options, out RouteRow route, out SockAddress bestSource);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterfaceEx(ref SockAddress destination, out uint index);
    [DllImport("iphlpapi.dll")]
    private static extern uint CreateIpForwardEntry2(ref RouteRow route);
    [DllImport("iphlpapi.dll")]
    private static extern uint SetIpForwardEntry2(ref RouteRow route);
    [DllImport("iphlpapi.dll")]
    private static extern uint DeleteIpForwardEntry2(ref RouteRow route);
}
