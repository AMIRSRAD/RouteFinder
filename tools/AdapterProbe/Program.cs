using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RouteFinder.Core;

const int packetSize = 80;
const int tokenSize = 16;

try
{
    if (args is ["internet", var internetAdapter])
        return await InternetProbe.Run(internetAdapter);
    if (args.Length >= 3 && args[0] == "policy" && args.Length % 2 == 1)
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces();
        var rules = args.Skip(1).Chunk(2).Select(pair =>
        {
            var adapterId = adapters.SingleOrDefault(item => item.Name.Equals(pair[0], StringComparison.OrdinalIgnoreCase))?.Id
                ?? throw new InvalidOperationException($"Windows adapter '{pair[0]}' was not found.");
            return new DirectAppRouting.Rule(Path.GetFullPath(pair[1]), adapterId);
        });
        using var routing = DirectAppRouting.Start(rules);
        Console.WriteLine("Windows accepted the temporary policy. Press Enter to remove it.");
        Console.ReadLine();
        return 0;
    }
    if (args is ["serve", var bind, var servePort] && TryEndpoint(bind, servePort, out var listen))
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        using var udp = new UdpClient(listen);
        using var tcp = new TcpListener(listen);
        tcp.Start();
        Console.WriteLine($"Listening on {listen} for TCP and UDP.");
        try { await Task.WhenAll(ServeTcp(tcp, stop.Token), ServeUdp(udp, stop.Token)); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        return 0;
    }

    if (args is ["probe", var adapter, var host, var port] && TryEndpoint(host, port, out var destination))
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run the probe client on Windows.");
        var network = adapter == "default" ? null : NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(item =>
            item.Name.Equals(adapter, StringComparison.OrdinalIgnoreCase) || item.Id.Equals(adapter, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Windows adapter '{adapter}' was not found.");
        if (network is not null && network.OperationalStatus != OperationalStatus.Up)
            throw new InvalidOperationException($"Windows adapter '{adapter}' is down.");
        int? index = null;
        if (network is not null)
        {
            var properties = network.GetIPProperties();
            index = destination.AddressFamily == AddressFamily.InterNetwork
                ? properties.GetIPv4Properties()?.Index : properties.GetIPv6Properties()?.Index;
            if (index is not > 0) throw new InvalidOperationException("The adapter has no interface index for this IP family.");
        }
        bool tcpPassed = await Probe(destination, network, index, SocketType.Stream, ProtocolType.Tcp);
        bool udpPassed = await Probe(destination, network, index, SocketType.Dgram, ProtocolType.Udp);
        return tcpPassed && udpPassed ? 0 : 1;
    }

    Console.Error.WriteLine("AdapterProbe serve <bind-ip> <port>");
    Console.Error.WriteLine("AdapterProbe probe <adapter-name|default> <server-ip> <port>");
    Console.Error.WriteLine("AdapterProbe policy <adapter-name> <executable-path> [<adapter-name> <executable-path> ...]");
    Console.Error.WriteLine("AdapterProbe internet <adapter-name|default>");
    return 2;
}
catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException
    or ArgumentException or UnauthorizedAccessException or TimeoutException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static bool TryEndpoint(string ip, string port, out IPEndPoint endpoint)
{
    endpoint = null!;
    if (!IPAddress.TryParse(ip, out var address) || !int.TryParse(port, out int number) || number is < 1 or > 65535)
        return false;
    endpoint = new IPEndPoint(address, number);
    return true;
}

static async Task<bool> Probe(IPEndPoint destination, NetworkInterface? network, int? index, SocketType kind, ProtocolType protocol)
{
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new Socket(destination.AddressFamily, kind, protocol);
        if (index is int interfaceIndex)
        {
            const SocketOptionName unicastInterface = (SocketOptionName)31; // IP_UNICAST_IF / IPV6_UNICAST_IF
            bool ipv4 = destination.AddressFamily == AddressFamily.InterNetwork;
            socket.SetSocketOption(ipv4 ? SocketOptionLevel.IP : SocketOptionLevel.IPv6, unicastInterface,
                ipv4 ? IPAddress.HostToNetworkOrder(interfaceIndex) : interfaceIndex);
        }
        await socket.ConnectAsync(destination, deadline.Token);
        var request = new byte[packetSize];
        RandomNumberGenerator.Fill(request.AsSpan(0, tokenSize));
        var response = new byte[packetSize];
        if (kind == SocketType.Stream)
        {
            using var stream = new NetworkStream(socket);
            await stream.WriteAsync(request, deadline.Token);
            await stream.ReadExactlyAsync(response, deadline.Token);
        }
        else
        {
            await socket.SendAsync(request, SocketFlags.None, deadline.Token);
            if (await socket.ReceiveAsync(response, SocketFlags.None, deadline.Token) != packetSize)
                throw new IOException("Unexpected UDP reply length.");
        }
        if (!request.AsSpan(0, tokenSize).SequenceEqual(response.AsSpan(0, tokenSize)) ||
            !IPAddress.TryParse(Encoding.ASCII.GetString(response.AsSpan(tokenSize)).TrimEnd('\0'), out var observed))
            throw new IOException("The reply did not match this probe.");
        var local = ((IPEndPoint)socket.LocalEndPoint!).Address;
        if (network is not null && !network.GetIPProperties().UnicastAddresses.Any(item => item.Address.Equals(local)))
            throw new IOException($"Windows selected local address {local} outside {network.Name}.");
        Console.WriteLine($"{protocol}: {network?.Name ?? "Windows default"} | local {local} | server observed {observed}");
        return true;
    }
    catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
    {
        Console.Error.WriteLine($"{protocol}: {(e is OperationCanceledException ? "Timed out." : e.Message)}");
        return false;
    }
}

static async Task ServeTcp(TcpListener listener, CancellationToken stop)
{
    while (true)
    {
        using var client = await listener.AcceptTcpClientAsync(stop);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var packet = new byte[packetSize];
        try
        {
            var stream = client.GetStream();
            await stream.ReadExactlyAsync(packet, deadline.Token);
            WriteObservedAddress(packet, (IPEndPoint)client.Client.RemoteEndPoint!);
            await stream.WriteAsync(packet, deadline.Token);
        }
        catch (IOException) { }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
    }
}

static async Task ServeUdp(UdpClient listener, CancellationToken stop)
{
    while (true)
    {
        var request = await listener.ReceiveAsync(stop);
        if (request.Buffer.Length != packetSize) continue;
        WriteObservedAddress(request.Buffer, request.RemoteEndPoint);
        await listener.SendAsync(request.Buffer, request.RemoteEndPoint, stop);
    }
}

static void WriteObservedAddress(byte[] packet, IPEndPoint remote)
{
    var field = packet.AsSpan(tokenSize);
    field.Clear();
    Encoding.ASCII.GetBytes(remote.Address.ToString(), field);
}
