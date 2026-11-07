using System.Net.Sockets;
using RouteFinder.Core;
using RouteFinder.Service;

namespace RouteFinder.App;

internal sealed class ConnectionSession(EzConnectClient client) : IDisposable
{
    AuthTokens? tokens;
    string? allocation;
    WireSockTunnel? tunnel;
    public event Action<string>? Diagnostic;
    public event Action<string>? RefreshTokenChanged;
    public bool SignedIn => tokens != null;
    public bool HasTunnel => tunnel != null;
    public bool TunnelIsActive => tunnel?.IsActive == true;
    public bool PeerResponseObserved => tunnel?.HandshakeResponseObserved == true;
    public string? ActiveMtu { get; private set; }
    public string? ActiveKeepalive { get; private set; }
    public string[] ActiveRoutingDetails { get; private set; } = [];
    public string? ActiveProxyStatus => tunnel?.ProxyStatus;
    public uint? ActiveTunnelError => tunnel?.LastTunnelError;
    public void ResetTunnelTcpConnections() =>
        (tunnel ?? throw new InvalidOperationException("No active tunnel is available.")).ResetTunnelTcpConnections();
    public bool NeedsCleanup => allocation != null;
    public TunnelState? State => tunnel?.GetState();

    public async Task<BootstrapData> SignInAsync(string email, string password)
    {
        var device = await Task.Run(DeviceIdentity.ReadCurrent);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var authenticated = await client.LoginAsync(email, password, device, deadline.Token);
        var catalog = await client.BootstrapAsync(authenticated.AccessToken, deadline.Token);
        tokens = authenticated;
        return catalog;
    }

    public async Task<BootstrapData> RestoreAsync(string refreshToken, Action<string>? onRefreshed = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var restored = await client.RefreshAsync(refreshToken, deadline.Token);
        onRefreshed?.Invoke(restored.RefreshToken);
        var catalog = await client.BootstrapAsync(restored.AccessToken, deadline.Token);
        tokens = restored;
        return catalog;
    }

    public string? RefreshToken => tokens?.RefreshToken;

    public async Task<BootstrapData> ReloadAsync() => await Authorized((token, ct) => client.BootstrapAsync(token, ct));

    public async Task SignOutAsync()
    {
        await DisconnectAsync();
        if (tokens == null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await client.SignOutAsync(tokens.RefreshToken, deadline.Token); }
        finally { tokens = null; }
    }

    public async Task<string> ConnectAsync(ServiceRoute? route, TunnelProfile? local, string[] apps, bool entirePc = false,
        bool networkLock = false, bool portable = false, string dnsOverride = "", bool autoMtu = false,
        string[]? bypassIps = null, string[]? bypassDomains = null, bool consoleMode = false, bool bypassLan = false,
        int? keepaliveSeconds = null, string[]? excludedApplications = null, int? tunnelMtu = null,
        ProxyOverride? socksProxyOverride = null, bool blockOtherApps = false)
    {
        var bypass = new List<string>(blockOtherApps ? [] : bypassIps ?? []);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            foreach (string domain in blockOtherApps ? [] : bypassDomains ?? [])
            {
                if (Uri.CheckHostName(domain) != UriHostNameType.Dns)
                    throw new FormatException("A destination bypass hostname is invalid.");
                try
                {
                    var addresses = await System.Net.Dns.GetHostAddressesAsync(domain, deadline.Token);
                    if (addresses.Length == 0) throw new InvalidOperationException($"No IP addresses were found for bypass domain {domain}.");
                    bypass.AddRange(addresses.Select(address => address.ToString()));
                }
                catch (Exception error) when (error is SocketException or OperationCanceledException)
                {
                    throw new InvalidOperationException($"Could not resolve bypass hostname {domain}. Check DNS and try again.", error);
                }
            }
        }
        var profile = await PrepareAsync(route, local, entirePc);
        try
        {
            if (local is not null && !entirePc && profile.GetInterface("AllowedApps") is { Length: > 0 } allowlist)
            {
                var configuredPaths = allowlist.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (configuredPaths.Any(path => !Path.IsPathFullyQualified(path) ||
                    !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.IndexOfAny(['*', '?']) >= 0))
                    throw new FormatException("This profile uses an app rule that RouteFinder cannot match to an exact executable. Edit AllowedApps to use absolute executable paths.");
                var allowedPaths = configuredPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (apps.Any(path => !Path.IsPathFullyQualified(path) || !allowedPaths.Contains(Path.GetFullPath(path))))
                    throw new FormatException("The selected apps include an executable outside this profile's AllowedApps list. Select only allowed executables.");
            }
            if (!entirePc && profile.GetInterface("DisallowedApps") is { Length: > 0 } exclusions)
            {
                var paths = exclusions.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (paths.Any(path => !Path.IsPathFullyQualified(path) ||
                    !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.IndexOfAny(['*', '?']) >= 0))
                    throw new FormatException("Selected apps mode requires exact executable paths in DisallowedApps.");
                var excluded = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (apps.Any(path => excluded.Contains(Path.GetFullPath(path))))
                    throw new FormatException("A selected executable is excluded by this profile. Uncheck it before connecting.");
                // The selected executable list already excludes every path in this rule.
                profile.Interface.Remove("DisallowedApps");
            }
            ApplyDnsOverride(profile, dnsOverride);
            ApplyProxyOverride(profile, socksProxyOverride);
            if (entirePc && !consoleMode && excludedApplications is { Length: > 0 })
            {
                if (excludedApplications.Any(path => !Path.IsPathFullyQualified(path) || path.Contains('|')))
                    throw new FormatException("App exclusions must be absolute paths without a pipe character.");
                profile.Interface["DisallowedApps"] = string.Join("|",
                    profile.GetInterface("DisallowedApps").Split('|', StringSplitOptions.RemoveEmptyEntries)
                        .Concat(excludedApplications).Distinct(StringComparer.OrdinalIgnoreCase));
            }
            if (consoleMode)
            {
                profile.Interface["DisallowedApps"] = string.Join("|",
                    Enumerable.Range('A', 26).Select(letter => $"{(char)letter}:\\"));
            }
            if (blockOtherApps)
            {
                profile.Interface.Remove("IgnoredIPs");
                profile.Interface.Remove("DisallowedIPs");
            }
            else if (consoleMode || bypassLan)
            {
                profile.Interface["IgnoredIPs"] = string.Join(",", new[]
                {
                    profile.GetInterface("IgnoredIPs"), "10.0.0.0/8", "172.16.0.0/12",
                    "192.168.0.0/16", "169.254.0.0/16", "224.0.0.0/4", "255.255.255.255/32"
                }.Where(value => value.Length > 0));
            }
            if (bypass.Count > 0)
            {
                var ranges = bypass.Select(value =>
                {
                    if (!System.Net.IPAddress.TryParse(value, out var address)) return value;
                    int prefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                    return $"{address}/{prefix}";
                });
                profile.Interface["DisallowedIPs"] = string.Join(",", ranges
                    .Prepend(profile.GetInterface("DisallowedIPs"))
                    .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
                profile.Validate();
                Diagnostic?.Invoke("Applied destination IP bypass rules.");
            }
            await ApplyTunnelOverridesAsync(profile, autoMtu, tunnelMtu, keepaliveSeconds, CancellationToken.None);
            var owned = await Task.Run(() => StartTunnel(profile, apps, networkLock, portable || consoleMode, allApplications: entirePc));
            tunnel = owned;
            ActiveMtu = profile.GetInterface("MTU");
            ActiveKeepalive = profile.GetPeer("PersistentKeepalive");
            ActiveRoutingDetails =
            [
                $"WireSock mode   {(portable || consoleMode ? "Virtual adapter" : "Transparent")}",
                $"Tunnel addresses   {profile.GetInterface("Address")}",
                $"Allowed IP ranges   {profile.GetPeer("AllowedIPs")}",
                $"DNS   {DescribeInterfaceOption(profile, "DNS", "System default")}",
                $"SOCKS5 proxy   {(profile.GetInterface("Socks5Proxy").Length > 0 ? "Configured" : "None")}",
                $"Ignored IP ranges   {DescribeInterfaceOption(profile, "IgnoredIPs", "None")}",
                $"Disallowed IP ranges   {DescribeInterfaceOption(profile, "DisallowedIPs", "None")}",
                $"Included apps   {(entirePc ? "All apps subject to exclusions" : string.Join(", ", apps))}",
                $"Excluded apps   {(consoleMode ? "Local executable drive paths" : DescribeInterfaceOption(profile, "DisallowedApps", "None").Replace("|", ", "))}",
                $"Kill switch   {(networkLock ? "Enabled" : "Disabled")}"
            ];
            return profile.Endpoint;
        }
        catch
        {
            await ReleaseAsync();
            throw;
        }
    }

    public async Task<RouteTestResult> TestAsync(ServiceRoute? route, TunnelProfile? local, CancellationToken cancellation = default,
        bool portable = false, string dnsOverride = "", bool autoMtu = false,
        int? tunnelMtu = null, int? keepaliveSeconds = null, int routeTestSeconds = 20,
        ProxyOverride? socksProxyOverride = null)
    {
        TunnelProfile profile;
        try { profile = await PrepareAsync(route, local, cancellation: cancellation); }
        catch (EzConnectServiceException error) when (error.Code == "ROUTE_PROFILE_RELEASED" && !NeedsCleanup)
        {
            cancellation.ThrowIfCancellationRequested();
            Diagnostic?.Invoke(error.Message);
            return new RouteTestResult("", false, null, 0, ProfileUnavailable: true);
        }
        try
        {
            ApplyDnsOverride(profile, dnsOverride);
            ApplyProxyOverride(profile, socksProxyOverride);
            await ApplyTunnelOverridesAsync(profile, autoMtu, tunnelMtu, keepaliveSeconds, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var ranges = profile.GetPeer("AllowedIPs").Split(',', StringSplitOptions.TrimEntries);
            var ipv4Target = System.Net.IPAddress.Parse("1.1.1.1");
            var ipv6Target = System.Net.IPAddress.Parse("2606:4700:4700::1111");
            var endpointAddresses = await GetProbeEndpointAddressesAsync(profile, cancellation, socksProxyOverride);
            var target = new[] { ipv4Target, ipv6Target }.FirstOrDefault(address =>
                ranges.Any(range => CoversAddress(range, address)) &&
                !IsExcluded(profile, address) && !endpointAddresses.Contains(address))
                ?? throw new DestinationProbeException("Neither standard probe address can be used with this profile. Try Test custom destination.");
            return await Task.Run(async () =>
            {
                using var probe = StartTunnel(profile, [Environment.ProcessPath!], portable: portable);
                int probes = 0;
                bool tcpReached = false;
                TunnelState? confirmed = null;
                using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                int limitSeconds = Math.Clamp(routeTestSeconds, 5, 120);
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                double? firstResponseSeconds = null;
                window.CancelAfter(TimeSpan.FromSeconds(limitSeconds));
                while (!window.IsCancellationRequested)
                {
                    probes++;
                    using var socket = new TcpClient(target.AddressFamily);
                    using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(window.Token);
                    connectDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        await socket.ConnectAsync(target, 443, connectDeadline.Token);
                        tcpReached = true;
                    }
                    catch (SocketException) { }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                    try { await Task.Delay(600, window.Token); }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                    var state = probe.GetState();
                    if (probe.HandshakeResponseObserved || state.TimeSinceLastHandshakeMilliseconds >= 0 || state.RxBytes > 0)
                    {
                        confirmed = state;
                        double elapsedSinceStart = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
                        if (firstResponseSeconds is null)
                        {
                            firstResponseSeconds = elapsedSinceStart;
                            // Give the SDK a short window to update its peer estimates.
                            // Keep the original deadline if it will expire sooner.
                            var remaining = Math.Min(4, limitSeconds - elapsedSinceStart);
                            if (remaining <= 0) break;
                            window.CancelAfter(TimeSpan.FromSeconds(remaining));
                        }
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
                if (confirmed is not { } measured) return new RouteTestResult(profile.Endpoint, false, null, probes,
                    TcpReached: tcpReached, ProbeSeconds: elapsed, ProbeLimitSeconds: limitSeconds,
                    ProbeTarget: target.ToString(), ProxyStatus: probe.ProxyStatus, TunnelError: probe.LastTunnelError,
                    HandshakeAttempts: probe.HandshakeAttempts);
                return new RouteTestResult(profile.Endpoint, true, measured.EstimatedRtt > 0 ? measured.EstimatedRtt : null, probes,
                    measured.RxBytes > 0 && float.IsFinite(measured.EstimatedLoss) && measured.EstimatedLoss is >= 0 and <= 100 ? measured.EstimatedLoss : null,
                    TcpReached: tcpReached,
                    ProbeSeconds: elapsed, ProbeLimitSeconds: limitSeconds, FirstResponseSeconds: firstResponseSeconds,
                    ProbeTarget: target.ToString(), ProxyStatus: probe.ProxyStatus, TunnelError: probe.LastTunnelError,
                    HandshakeAttempts: probe.HandshakeAttempts);
            });
        }
        finally { await ReleaseAsync(); }
    }

    public async Task<PingReport> ProbeDestinationAsync(ServiceRoute? route, TunnelProfile? local,
        System.Net.IPAddress destination, int tcpPort, bool portable, CancellationToken cancellation = default,
        int timeoutMilliseconds = 1500, bool autoMtu = false, int? tunnelMtu = null,
        int? keepaliveSeconds = null, ProxyOverride? socksProxyOverride = null)
    {
        if (tcpPort is < 1 or > 65535)
            throw new DestinationProbeException("Enter a TCP port between 1 and 65535.");
        cancellation.ThrowIfCancellationRequested();
        if (local is not null && (local.GetInterface("AllowedApps").Length > 0 ||
            local.GetInterface("DisallowedApps").Length > 0))
            throw new DestinationProbeException("Destination tests require an imported profile without application filters.");
        var profile = await PrepareAsync(route, local, cancellation: cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var endpointAddresses = await GetProbeEndpointAddressesAsync(profile, cancellation, socksProxyOverride);
            if (endpointAddresses.Contains(destination))
                throw new DestinationProbeException("The destination is the tunnel endpoint or SOCKS5 proxy. Choose a different address.");
            if (IsExcluded(profile, destination))
                throw new DestinationProbeException("This profile bypasses the destination. Choose a different address or profile.");
            if (local is not null && !profile.GetPeer("AllowedIPs")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(range => CoversAddress(range, destination)))
                throw new DestinationProbeException("The destination is outside this imported profile's allowed IP ranges.");
            bool ipv6 = destination.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            if (ipv6 && !profile.GetInterface("Address").Split(',').Any(value =>
                System.Net.IPAddress.TryParse(value.Trim().Split('/')[0], out var ip) &&
                ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6))
                throw new DestinationProbeException("This profile has no IPv6 tunnel address.");
            profile.Peer["AllowedIPs"] = $"{destination}/{(ipv6 ? 128 : 32)}";
            profile.Interface.Remove("DNS");
            ApplyProxyOverride(profile, socksProxyOverride);
            await ApplyTunnelOverridesAsync(profile, autoMtu, tunnelMtu, keepaliveSeconds, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return await Task.Run(async () =>
            {
                string executable = Environment.ProcessPath ??
                    throw new InvalidOperationException("Could not locate the RouteFinder executable for the TCP probe.");
                using var tunnel = StartTunnel(profile, [executable], portable: portable);
                var before = tunnel.GetState();
                var tcp = await Diagnostics.MeasureTcp(destination.ToString(), tcpPort, cancellation, timeoutMilliseconds);
                var report = new PingReport(tcp.Detail, Tcp: tcp);
                var after = tunnel.GetState();
                bool handshake = tunnel.HandshakeResponseObserved || after.TimeSinceLastHandshakeMilliseconds >= 0;
                ulong received = after.RxBytes - Math.Min(before.RxBytes, after.RxBytes);
                bool pathUnverified = tcp.Connected && (!handshake || received == 0);
                uint? tunnelError = tunnel.LastTunnelError;
                return report with
                {
                    TunnelHandshake = handshake,
                    ProxyStatus = tunnel.ProxyStatus,
                    TunnelError = tunnelError,
                    TunnelPathUnverified = pathUnverified,
                    TunnelReceivedBytes = received,
                    EstimatedPeerRtt = handshake && after.EstimatedRtt > 0 ? after.EstimatedRtt : null,
                    Detail = report.Detail +
                        (tunnel.ProxyStatus is { } proxyStatus ? Environment.NewLine + $"SOCKS5 status: {proxyStatus}" : "") +
                        (tunnelError is uint error ? Environment.NewLine + $"Last WireSock tunnel error: 0x{error:X8}" : "") +
                        Environment.NewLine +
                        (handshake ? "WireSock reported a peer handshake." : "WireSock did not report a peer handshake.") +
                        Environment.NewLine +
                        $"WireSock counters during probe: received {received} bytes, sent {after.TxBytes - Math.Min(before.TxBytes, after.TxBytes)} bytes. Counters do not identify individual TCP connections." +
                        (pathUnverified ? Environment.NewLine + "A TCP reply was received, but the tunnel path was not verified." : "")
                };
            });
        }
        finally { await ReleaseAsync(); }
    }

    static async Task<System.Net.IPAddress[]> GetProbeEndpointAddressesAsync(TunnelProfile profile,
        CancellationToken cancellation, ProxyOverride? proxyOverride)
    {
        var addresses = await ResolveProbeHostAsync(TunnelProfile.SplitEndpoint(profile.Endpoint).Host, cancellation);
        string proxyEndpoint = string.IsNullOrWhiteSpace(proxyOverride?.Endpoint)
            ? profile.GetInterface("Socks5Proxy").Trim() : proxyOverride!.Endpoint;
        if (proxyEndpoint.Length == 0) return addresses;
        string proxyHost;
        try
        {
            proxyHost = System.Net.IPAddress.TryParse(proxyEndpoint, out _) ||
                Uri.CheckHostName(proxyEndpoint) == UriHostNameType.Dns
                ? proxyEndpoint : TunnelProfile.SplitEndpoint(proxyEndpoint).Host;
        }
        catch (FormatException)
        {
            throw new DestinationProbeException("Could not identify the profile's SOCKS5 host for a safe probe.");
        }
        var proxy = await ResolveProbeHostAsync(proxyHost, cancellation);
        return addresses.Concat(proxy).ToArray();
    }

    static async Task<System.Net.IPAddress[]> ResolveProbeHostAsync(string endpoint, CancellationToken cancellation)
    {
        if (System.Net.IPAddress.TryParse(endpoint, out var address)) return [address];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await System.Net.Dns.GetHostAddressesAsync(endpoint, deadline.Token); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new DestinationProbeException("The tunnel endpoint or proxy hostname lookup timed out.");
        }
        catch (SocketException)
        {
            throw new DestinationProbeException("The tunnel endpoint or proxy hostname could not be resolved.");
        }
    }

    static string DescribeInterfaceOption(TunnelProfile profile, string key, string fallback) =>
        profile.GetInterface(key) is { Length: > 0 } value ? value : fallback;

    static bool IsExcluded(TunnelProfile profile, System.Net.IPAddress address) =>
        new[] { "IgnoredIPs", "DisallowedIPs" }.Any(key => profile.GetInterface(key)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(range => CoversAddress(range, address)));

    static bool CoversAddress(string range, System.Net.IPAddress address)
    {
        var parts = range.Split('/');
        if (parts.Length != 2 || !System.Net.IPAddress.TryParse(parts[0], out var network) ||
            network.AddressFamily != address.AddressFamily || !int.TryParse(parts[1], out int prefix))
            return false;
        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        if (prefix < 0 || prefix > networkBytes.Length * 8) return false;
        int fullBytes = prefix / 8;
        int remainingBits = prefix % 8;
        return networkBytes.AsSpan(0, fullBytes).SequenceEqual(addressBytes.AsSpan(0, fullBytes)) &&
            (remainingBits == 0 || (networkBytes[fullBytes] ^ addressBytes[fullBytes]) >> (8 - remainingBits) == 0);
    }

    static WireSockTunnel StartTunnel(TunnelProfile profile, string[] apps, bool networkLock = false, bool portable = false,
        bool allApplications = false)
    {
        var sdk = new WireSockTunnel(portable: portable, observeEvents: true);
        try { sdk.Connect(profile, apps, networkLock, allApplications); return sdk; }
        catch { sdk.Dispose(); throw; }
    }

    static void ApplyDnsOverride(TunnelProfile profile, string dns)
    {
        if (string.IsNullOrWhiteSpace(dns)) return;
        var servers = dns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (servers.Length == 0 || servers.Any(value => !System.Net.IPAddress.TryParse(value, out _)))
            throw new FormatException("DNS override must contain IPv4 or IPv6 addresses separated by commas.");
        profile.Interface["DNS"] = string.Join(", ", servers);
    }

    static void ApplyProxyOverride(TunnelProfile profile, ProxyOverride? proxy)
    {
        if (proxy is null || string.IsNullOrWhiteSpace(proxy.Endpoint)) return;
        TunnelProfile.SplitEndpoint(proxy.Endpoint);
        profile.Interface["Socks5Proxy"] = proxy.Endpoint;
        profile.Interface["Socks5ProxyUsername"] = proxy.Username;
        profile.Interface["Socks5ProxyPassword"] = proxy.Password;
        if (proxy.AllTraffic)
            profile.Interface["Socks5ProxyAllTraffic"] = "1";
        else
            profile.Interface.Remove("Socks5ProxyAllTraffic");
    }

    async Task ApplyTunnelOverridesAsync(TunnelProfile profile, bool autoMtu, int? tunnelMtu,
        int? keepaliveSeconds, CancellationToken cancellation)
    {
        if (keepaliveSeconds is int seconds)
            profile.Peer["PersistentKeepalive"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (tunnelMtu is int mtu)
            profile.Interface["MTU"] = mtu.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else
            await ApplyAutoMtuAsync(profile, autoMtu, cancellation);
    }

    async Task ApplyAutoMtuAsync(TunnelProfile profile, bool enabled, CancellationToken cancellation)
    {
        if (!enabled) return;
        string endpointHost = TunnelProfile.SplitEndpoint(profile.Endpoint).Host;
        if (profile.GetInterface("Socks5Proxy").Length > 0 ||
            !System.Net.IPAddress.TryParse(endpointHost, out var endpointIp) ||
            endpointIp.AddressFamily != AddressFamily.InterNetwork)
        {
            Diagnostic?.Invoke("Auto MTU needs a direct numeric IPv4 endpoint. Kept the profile MTU.");
            return;
        }
        int? networkMtu = await PathMtuProbe.MeasureAsync(endpointIp, cancellation);
        if (networkMtu is not { } pathMtu)
        {
            Diagnostic?.Invoke("Auto MTU: path probes were inconclusive; kept the profile MTU.");
            return;
        }
        int serverMtu = int.TryParse(profile.GetInterface("MTU"), out int configured) ? configured : 1380;
        const int wireGuardOverhead = 60;
        int optimized = Math.Clamp(Math.Min(serverMtu, pathMtu - wireGuardOverhead), 576, serverMtu);
        profile.Interface["MTU"] = optimized.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Diagnostic?.Invoke($"Auto MTU: path estimate {pathMtu}, tunnel MTU set to {optimized} (profile value {serverMtu}).");
    }

    async Task<TunnelProfile> PrepareAsync(ServiceRoute? route, TunnelProfile? local, bool entirePc = false,
        CancellationToken cancellation = default)
    {
        if (tunnel != null) throw new InvalidOperationException("Disconnect the current route first.");
        cancellation.ThrowIfCancellationRequested();
        await ReleaseAsync();
        cancellation.ThrowIfCancellationRequested();
        if (route == null)
        {
            var localProfile = local ?? throw new InvalidOperationException("Select a connection profile.");
            if (entirePc && localProfile.GetInterface("AllowedApps").Length > 0)
                throw new InvalidOperationException("This imported profile limits routing to specific apps. Use Connect with saved apps or remove AllowedApps from the profile before using Entire PC mode.");
            if (entirePc && !localProfile.GetPeer("AllowedIPs").Split(',').Any(value =>
                value.Trim() is "0.0.0.0/0" or "::/0"))
                throw new InvalidOperationException("Entire PC mode requires an IPv4 or IPv6 default route in this profile.");
            return localProfile;
        }
        var keys = WireGuardKeys.Generate();
        ProvisionedSession session;
        try
        {
            session = await Authorized((token, ct) => client.ConnectAsync(route, keys.PublicKey, keys.PrivateKey, token, ct));
        }
        catch (PendingSessionCleanupException error)
        {
            allocation = error.SessionId;
            throw;
        }
        allocation = session.SessionId;
        // Provisioning can create a session even if the user has already canceled.
        // Capture the allocation first, then release it before returning cancellation.
        if (cancellation.IsCancellationRequested)
        {
            await ReleaseAsync();
            cancellation.ThrowIfCancellationRequested();
        }
        if (session.Profile.GetPeer("PersistentKeepalive").Length == 0)
            session.Profile.Peer["PersistentKeepalive"] = "25";
        if (entirePc)
            session.Profile.Peer["AllowedIPs"] = route.HasIpv6 ? "0.0.0.0/0,::/0" : "0.0.0.0/0";
        return session.Profile;
    }

    public async Task DisconnectAsync()
    {
        if (tunnel is { } owned)
        {
            tunnel = null;
            ActiveMtu = ActiveKeepalive = null;
            ActiveRoutingDetails = [];
            await Task.Run(owned.Dispose);
        }
        await ReleaseAsync();
    }

    async Task ReleaseAsync()
    {
        if (allocation is not { } id) return;
        Task<bool> ReleaseRequest() =>
            Authorized(async (token, ct) => { await client.DisconnectAsync(id, token, ct); return true; });
        try
        {
            try { await ReleaseRequest(); }
            catch (EzConnectServiceException error) when (error.StatusCode == 429 &&
                error.RetryAfter is { } delay && delay >= TimeSpan.Zero && delay <= TimeSpan.FromSeconds(15))
            {
                Diagnostic?.Invoke($"Session release rate limited. Retrying in {Math.Ceiling(delay.TotalSeconds)} seconds.");
                await Task.Delay(delay);
                await ReleaseRequest();
            }
            allocation = null;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("The local tunnel is closed, but EZConnect did not confirm session release. Use Retry cleanup before starting another route.", error);
        }
    }

    async Task<T> Authorized<T>(Func<string, CancellationToken, Task<T>> request)
    {
        var auth = tokens ?? throw new InvalidOperationException("Sign in to load EZConnect routes.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { return await request(auth.AccessToken, deadline.Token); }
        catch (EzConnectServiceException error) when (error.StatusCode == 401)
        {
            tokens = await client.RefreshAsync(auth.RefreshToken, deadline.Token);
            if (!string.Equals(auth.RefreshToken, tokens.RefreshToken, StringComparison.Ordinal))
                RefreshTokenChanged?.Invoke(tokens.RefreshToken);
            return await request(tokens.AccessToken, deadline.Token);
        }
    }

    public void Dispose()
    {
        try { tunnel?.Dispose(); }
        finally { client.Dispose(); }
    }
}

internal sealed record RouteTestResult(string Endpoint, bool Succeeded, int? Latency, int Probes, float? Loss = null,
    int? RttSpread = null, bool? TcpReached = null, bool ProfileUnavailable = false, float? Jitter = null,
    DateTimeOffset? TestedAt = null, double? ProbeSeconds = null, int? ProbeLimitSeconds = null,
    double? FirstResponseSeconds = null, string? ProbeTarget = null, string? ProxyStatus = null,
    uint? TunnelError = null, int? HandshakeAttempts = null)
{
    public string ProbeTiming => ProbeSeconds is double elapsed && ProbeLimitSeconds is int limit
        ? $"Probe time {elapsed:0.0} s / {limit} s limit" +
          (ProbeTarget is { Length: > 0 } target ? $" · TCP destination {target} (port 443)" : "") +
          (ProxyStatus is { } proxy ? $" · SOCKS5 {proxy}" : "") +
          (TunnelError is uint error ? $" · WireSock error 0x{error:X8}" : "") +
          (HandshakeAttempts is int attempts ? $" · Handshake attempts {attempts}" : "") +
          (FirstResponseSeconds is double response ? $" · First peer response observed at {response:0.0} s" : "")
        : "";
}

internal sealed class DestinationProbeException(string message) : Exception(message);

internal sealed record ProxyOverride(string Endpoint, string Username, string Password, bool AllTraffic = false)
{
    public override string ToString() => "SOCKS5 override";
}
