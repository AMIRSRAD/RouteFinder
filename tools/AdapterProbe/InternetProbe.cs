using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

internal static class InternetProbe
{
    internal static async Task<int> Run(string adapter)
    {
        int? index = adapter == "default" ? null : NetworkInterface.GetAllNetworkInterfaces()
            .Single(item => item.Name.Equals(adapter, StringComparison.OrdinalIgnoreCase)).GetIPProperties().GetIPv4Properties()?.Index
            ?? throw new InvalidOperationException($"Adapter {adapter} has no IPv4 interface.");
        var results = await Task.WhenAll(Check("TCP", Tcp), Check("UDP", Udp));
        return results.All(ok => ok) ? 0 : 1;

        Socket Open(SocketType kind, ProtocolType protocol)
        {
            var socket = new Socket(AddressFamily.InterNetwork, kind, protocol);
            try
            {
                if (index is int selected)
                    socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(selected));
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }

        async Task Tcp(CancellationToken token)
        {
            string? local = null;
            using var handler = new SocketsHttpHandler { UseProxy = false };
            handler.ConnectCallback = async (context, cancellation) =>
            {
                var address = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellation))[0];
                var socket = Open(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellation);
                    local = ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
            using var http = new HttpClient(handler);
            string response = await http.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", token);
            var observed = response.Split('\n').First(line => line.StartsWith("ip=", StringComparison.Ordinal))[3..].Trim();
            if (!IPAddress.TryParse(observed, out _)) throw new IOException("The HTTPS response did not contain an IP address.");
            Console.WriteLine($"TCP local={local} observed={observed}");
        }

        async Task Udp(CancellationToken token)
        {
            var address = (await Dns.GetHostAddressesAsync("stun.cloudflare.com", AddressFamily.InterNetwork, token))[0];
            using var socket = Open(SocketType.Dgram, ProtocolType.Udp);
            await socket.ConnectAsync(address, 3478, token);
            byte[] request = [0, 1, 0, 0, 0x21, 0x12, 0xa4, 0x42, .. RandomNumberGenerator.GetBytes(12)];
            var response = new byte[1024];
            await socket.SendAsync(request, SocketFlags.None, token);
            int length = await socket.ReceiveAsync(response, SocketFlags.None, token);
            if (length < 20 || response[0] != 1 || response[1] != 1 ||
                !response.AsSpan(4, 16).SequenceEqual(request.AsSpan(4, 16)))
                throw new IOException("The STUN server did not return a matching binding response.");
            for (int offset = 20; offset + 4 <= length;)
            {
                int type = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset));
                int size = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 2));
                if (size > length - offset - 4) break;
                if (type == 0x0020 && size >= 8 && response[offset + 5] == 1)
                {
                    var mapped = new byte[4];
                    for (int i = 0; i < mapped.Length; i++) mapped[i] = (byte)(response[offset + 8 + i] ^ request[4 + i]);
                    Console.WriteLine($"UDP local={((IPEndPoint)socket.LocalEndPoint!).Address} observed={new IPAddress(mapped)}");
                    return;
                }
                offset += 4 + ((size + 3) & ~3);
            }
            throw new IOException("The STUN response did not contain an IPv4 mapping.");
        }
    }

    private static async Task<bool> Check(string protocol, Func<CancellationToken, Task> probe)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await probe(deadline.Token); return true; }
        catch (Exception error) when (error is SocketException or IOException or HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            Console.Error.WriteLine($"{protocol}: {(error is OperationCanceledException ? "timed out" : error.Message)}");
            return false;
        }
    }
}
