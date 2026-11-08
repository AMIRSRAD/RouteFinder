using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RouteFinder.Core;

public readonly record struct TunnelState(long TimeSinceLastHandshakeMilliseconds, ulong TxBytes, ulong RxBytes, float EstimatedLoss, int EstimatedRtt);

/// <summary>Wraps the locally installed x64 WgBooster SDK. Creating the wrapper only loads its DLL; Connect changes networking.</summary>
public sealed class WireSockTunnel : IDisposable
{
    private readonly object gate = new();
    private readonly nint library;
    private nint handle;
    private bool disposed;
    private readonly NativeLog logger;
    private readonly GetHandle getHandle;
    private readonly GetHandleEx? getHandleEx;
    private readonly NativeEvent? eventLogger;
    private readonly string backendPrefix;
    private readonly ReleaseHandle releaseHandle;
    private readonly CreateTunnel createTunnel;
    private readonly HandleBool startTunnel, stopTunnel, getActive;
    private readonly DropTunnel dropTunnel;
    private readonly NetworkLock setNetworkLock;
    private readonly SetGlobalOption setGlobalOption;
    private readonly GetStats getStats;
    private readonly string? sdkFileVersion;
    private readonly List<(nint Pointer, int Bytes)> allocatedStrings = [];
    private bool created;
    private bool ownsNetworkLock;
    private TunnelUplink? uplink;

    public string SdkPath { get; }
    public string? UplinkName => uplink?.AdapterName;
    public bool UplinkIsCurrent { get { lock (gate) return uplink?.IsCurrent ?? true; } }
    public event Action<string>? Log;
    public event Action<uint, uint>? TunnelEvent;
    private int proxyEvent;
    private int handshakeAttempts;
    private int handshakeResponseObserved;
    public bool HandshakeResponseObserved => Volatile.Read(ref handshakeResponseObserved) != 0;
    public int? HandshakeAttempts => getHandleEx is null ? null : Volatile.Read(ref handshakeAttempts);
    private long lastTunnelError = -1;
    public uint? LastTunnelError => Interlocked.Read(ref lastTunnelError) is long status && status >= 0
        ? (uint)status : null;
    public string? ProxyStatus => Volatile.Read(ref proxyEvent) switch
    {
        8 => "Connected",
        9 => "Disconnected",
        10 => "Error reported",
        _ => null
    };
    public bool IsActive { get { lock (gate) { CheckDisposed(); return handle != 0 && getActive(handle) != 0; } } }

    public static string FindSdkPath()
    {
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireSock Secure Connect", "sdk", "wgbooster.dll");
        if (!File.Exists(file)) throw new FileNotFoundException("Install WireSock Secure Connect before connecting. Its SDK is not present.", file);
        return file;
    }

    public WireSockTunnel(string? sdkPath = null, bool portable = false, bool observeEvents = false)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("WireSock requires 64-bit Windows.");
        SdkPath = Path.GetFullPath(sdkPath ?? FindSdkPath());
        if (!File.Exists(SdkPath)) throw new FileNotFoundException("WireSock SDK DLL not found.", SdkPath);
        sdkFileVersion = FileVersionInfo.GetVersionInfo(SdkPath).FileVersion;
        // Restrict dependency lookup to the DLL directory and standard trusted Windows directories.
        library = LoadLibraryEx(SdkPath, 0, 0x00000100 | 0x00001000);
        if (library == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load the WireSock SDK.");
        try
        {
            var prefix = portable ? "wgbp_" : "wgb_";
            backendPrefix = prefix;
            getHandle = Bind<GetHandle>(prefix + "get_handle");
            if (observeEvents && NativeLibrary.TryGetExport(library, prefix + "get_handle_ex", out var eventExport))
            {
                getHandleEx = Marshal.GetDelegateForFunctionPointer<GetHandleEx>(eventExport);
                eventLogger = value =>
                {
                    // SOCKS5 events from wg_tunnel_event_type in the SDK header.
                    if (value.Type is >= 8 and <= 10) Volatile.Write(ref proxyEvent, (int)value.Type);
                    if (value.Type == 4) Interlocked.Exchange(ref lastTunnelError, value.Status);
                    if (value.Type == 0) Interlocked.Increment(ref handshakeAttempts);
                    if (value.Type == 1) Volatile.Write(ref handshakeResponseObserved, 1);
                    try { TunnelEvent?.Invoke(value.Type, value.Status); }
                    catch { /* Keep exceptions inside the managed callback. */ }
                };
            }
            releaseHandle = Bind<ReleaseHandle>(prefix + "release_handle");
            createTunnel = Bind<CreateTunnel>(prefix + "create_tunnel_ex");
            startTunnel = Bind<HandleBool>(prefix + "start_tunnel");
            stopTunnel = Bind<HandleBool>(prefix + "stop_tunnel");
            getActive = Bind<HandleBool>(prefix + "get_tunnel_active");
            dropTunnel = Bind<DropTunnel>(prefix + "drop_tunnel");
            setNetworkLock = Bind<NetworkLock>(prefix + "set_network_lock_mode");
            setGlobalOption = Bind<SetGlobalOption>("wgb_set_global_option");
            getStats = Bind<GetStats>(prefix + "get_tunnel_state");
            logger = pointer =>
            {
                try
                {
                    var line = Marshal.PtrToStringUTF8(pointer) ?? "";
                    // The native SDK can print config details. Never forward key material or proxy credentials.
                    line = Regex.Replace(line, @"(?i)(PrivateKey|PresharedKey|password|username)\s*[:=]\s*\S+", "$1=[redacted]");
                    line = Regex.Replace(line, @"(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{43}=(?![A-Za-z0-9+/])", "[key redacted]");
                    string safe = line;
                    ThreadPool.QueueUserWorkItem(_ => { try { Log?.Invoke(safe); } catch { /* Exceptions must never cross the native boundary. */ } });
                }
                catch { /* Exceptions must never cross the native callback boundary. */ }
            };
        }
        catch { NativeLibrary.Free(library); throw; }
    }

    public void Connect(TunnelProfile profile, IEnumerable<string> applications, bool networkLock = false,
        bool allApplications = false, string? uplinkAdapterId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        ArgumentNullException.ThrowIfNull(applications);
        var originalApps = applications.ToArray();
        if (originalApps.Any(x => !Path.IsPathFullyQualified(x))) throw new ArgumentException("Selected application paths must be absolute.", nameof(applications));
        var apps = originalApps.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string app in apps)
            if (!File.Exists(app) || !app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || app.Contains('|')) throw new ArgumentException("Selected applications must be existing executable files with no pipe character in their paths.", nameof(applications));
        if (apps.Length == 0 && !allApplications)
            throw new ArgumentException("Select at least one application to route.", nameof(applications));
        if (apps.Length > 0 && allApplications)
            throw new ArgumentException("All-applications routing cannot use a selected-app list.", nameof(applications));
        if (apps.Length > 0 && profile.GetInterface("DisallowedApps").Length > 0) throw new FormatException("This profile uses DisallowedApps. Remove that option to use selected-application routing.");
        bool forceComma = Environment.GetCommandLineArgs().Contains("--wiresock-comma-app-list");
        string allowedApps = FormatAppList(apps, sdkFileVersion, forceComma);
        string disallowedApps = FormatAppList(profile.GetInterface("DisallowedApps")
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), sdkFileVersion, forceComma);
        if (uplinkAdapterId is not null && profile.GetInterface("Socks5Proxy").Length > 0)
            throw new InvalidOperationException("Choose Automatic internet adapter for a route that uses a SOCKS5 proxy.");
        lock (gate)
        {
            CheckDisposed();
            StopInternal();
            Volatile.Write(ref handshakeAttempts, 0);
            Volatile.Write(ref handshakeResponseObserved, 0);
            Volatile.Write(ref proxyEvent, 0);
            Interlocked.Exchange(ref lastTunnelError, -1);
            try
            {
                if (uplinkAdapterId is not null) uplink = TunnelUplink.Open(uplinkAdapterId, profile.Endpoint);
                if (profile.GetPeer("AllowedIPs").Split(',').Any(value => value.Trim() == "::/0") &&
                    setGlobalOption("EnableIPv6", "1") == 0)
                    Log?.Invoke("WireSock declined its optional IPv6 global setting; continuing with the profile's advertised routes.");
                try
                {
                    handle = getHandleEx is null ? getHandle(logger, 2, false) : getHandleEx(logger, 2, eventLogger!, false, true);
                }
                catch (SEHException error)
                {
                    throw new InvalidOperationException(
                        "WireSock could not initialize its networking backend. Run as administrator and check the WireSock driver status in Activity.", error);
                }
                if (handle == 0) throw NativeFailure("Initialize WireSock (administrator access and its driver are required)");
                nint I(string key) => Utf8(profile.GetInterface(key));
                nint P(string key) => Utf8(profile.GetPeer(key));
                var iface = new NativeInterface
                {
                    PrivateKey = I("PrivateKey"), Address = I("Address"), Dns = I("DNS"), Mtu = I("MTU"), ListenPort = I("ListenPort"),
                    Jc = I("Jc"), Jmin = I("Jmin"), Jmax = I("Jmax"), Jd = I("Jd"), Id = I("Id"), Ip = I("Ip"), Ib = I("Ib"),
                    S1 = I("S1"), S2 = I("S2"), S3 = I("S3"), S4 = I("S4"), H1 = I("H1"), H2 = I("H2"), H3 = I("H3"), H4 = I("H4")
                };
                var peer = new NativePeer { PublicKey = P("PublicKey"), PresharedKey = P("PresharedKey"), AllowedIps = P("AllowedIPs"), Endpoint = Utf8(uplink?.Endpoint ?? profile.Endpoint), KeepAlive = uint.TryParse(profile.GetPeer("PersistentKeepalive"), out var keepAlive) ? keepAlive : 0 };
                string ignored = string.Join(",", new[] { profile.GetInterface("IgnoredIPs"), profile.GetInterface("DisallowedIPs") }.Where(x => x.Length > 0));
                var extra = new NativeExtra
                {
                    Size = (nuint)Marshal.SizeOf<NativeExtra>(),
                    AllowedApps = Wide(allowedApps),
                    DisallowedApps = Wide(disallowedApps),
                    IgnoredIps = Utf8(ignored),
                    Proxy = I("Socks5Proxy"), Username = I("Socks5ProxyUsername"), Password = I("Socks5ProxyPassword"), AllTraffic = I("Socks5ProxyAllTraffic")
                };
                if (createTunnel(handle, profile.Name, ref iface, ref peer, ref extra) == 0) throw NativeFailure("Create tunnel");
                created = true;
                uplink?.Verify();
                if (networkLock)
                {
                    if (setNetworkLock(handle, 1) == 0) throw NativeFailure("Enable network lock");
                    ownsNetworkLock = true;
                }
                if (startTunnel(handle) == 0) throw NativeFailure("Start tunnel");
                uplink?.Verify();
                // An active interface is not proof of a successful peer handshake. Inspect GetState()/traffic.
            }
            catch (Exception startupError)
            {
                try { StopInternal(); }
                catch (Exception cleanupError) { throw new AggregateException("Tunnel startup and cleanup failed.", startupError, cleanupError); }
                throw;
            }
        }
    }

    public TunnelState GetState()
    {
        lock (gate)
        {
            CheckDisposed();
            if (handle == 0) return default;
            var state = getStats(handle);
            return new(state.HandshakeAge, state.TxBytes, state.RxBytes, state.Loss, state.Rtt);
        }
    }

    public void Stop() { lock (gate) { CheckDisposed(); StopInternal(); } }
    private void StopInternal()
    {
        Exception? nativeFailure = null;
        try { StopNativeTunnel(); }
        catch (Exception error) { nativeFailure = error; }
        try
        {
            uplink?.Dispose();
            uplink = null;
        }
        catch (Exception error) when (nativeFailure is not null)
        {
            throw new AggregateException("WireSock and temporary adapter route cleanup failed.", nativeFailure, error);
        }
        if (nativeFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(nativeFailure);
    }

    private void StopNativeTunnel()
    {
        var failures = new List<Exception>();
        if (handle != 0)
        {
            try
            {
                if (created)
                {
                    if (getActive(handle) != 0 && stopTunnel(handle) == 0)
                        failures.Add(NativeFailure("Stop tunnel"));
                    if (ownsNetworkLock && setNetworkLock(handle, 0) == 0)
                        failures.Add(NativeFailure("Disable network lock"));
                    if (dropTunnel(handle, ownsNetworkLock ? 0 : 1) == 0)
                        failures.Add(NativeFailure("Drop tunnel"));
                }
            }
            finally { releaseHandle(handle); handle = 0; created = ownsNetworkLock = false; }
        }
        foreach (var allocation in allocatedStrings)
        {
            // Remove key and proxy credential bytes before returning the native allocation.
            Marshal.Copy(new byte[allocation.Bytes], 0, allocation.Pointer, allocation.Bytes);
            Marshal.FreeHGlobal(allocation.Pointer);
        }
        allocatedStrings.Clear();
        GC.KeepAlive(logger);
        GC.KeepAlive(eventLogger);
        if (failures.Count > 0)
            throw new AggregateException("WireSock did not confirm complete tunnel cleanup.", failures);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            try { StopInternal(); }
            finally { disposed = true; NativeLibrary.Free(library); }
        }
        GC.SuppressFinalize(this);
    }
    public bool? QueryDriverNetworkLock()
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(WireSockTunnel));
            if (Bind<DriverOperation>("wg_is_network_lock_active")() != 0) return true;
            return Marshal.GetLastWin32Error() == 0 ? false : null;
        }
    }

    public void ResetDriverNetworkLock()
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(WireSockTunnel));
            if (handle != 0) throw new InvalidOperationException("Disconnect the tunnel before resetting the driver lock.");
            if (Bind<DriverOperation>("wg_reset_network_lock")() == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WireSock could not reset the driver network lock.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
    private delegate int DriverOperation();

    public void ResetTunnelTcpConnections()
    {
        lock (gate)
        {
            CheckDisposed();
            if (handle == 0) throw new InvalidOperationException("No active tunnel is available.");
            if (Bind<HandleBool>(backendPrefix + "drop_all_tcp_sockets")(handle) == 0)
                throw new InvalidOperationException("WireSock could not reset the tunnel's TCP connections.");
        }
    }

    private T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static string FormatAppList(string[] apps, string? fileVersion, bool forceComma)
    {
        // Both create_tunnel_ex exports in 3.4.8.1 split the list on commas despite the SDK guide.
        bool commaSeparated = fileVersion == "3.4.8.1" || forceComma;
        if (commaSeparated && apps.Any(path => path.Contains(',')))
            throw new ArgumentException("This WireSock app list cannot represent executable paths containing commas.", nameof(apps));
        return string.Join(commaSeparated ? ',' : '|', apps);
    }

    private nint Utf8(string value)
    {
        if (value.Length == 0) return 0;
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        nint pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length); allocatedStrings.Add((pointer, bytes.Length));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        return pointer;
    }
    private nint Wide(string value)
    {
        // The SDK expects a null pointer for an unused app list. Allow and deny lists are mutually exclusive.
        if (value.Length == 0) return 0;
        nint pointer = Marshal.StringToHGlobalUni(value);
        allocatedStrings.Add((pointer, checked((value.Length + 1) * 2)));
        return pointer;
    }
    private static Exception NativeFailure(string action) => new Win32Exception(Marshal.GetLastPInvokeError(), action + " failed. See the WireSock log for details.");
    private void CheckDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeLog(nint text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeEvent(NativeTunnelEvent value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
    private delegate nint GetHandleEx(NativeLog printer, int level, NativeEvent events, [MarshalAs(UnmanagedType.I1)] bool capture, [MarshalAs(UnmanagedType.I1)] bool analytics);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)] private delegate nint GetHandle(NativeLog printer, int level, [MarshalAs(UnmanagedType.I1)] bool capture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ReleaseHandle(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)] private delegate int CreateTunnel(nint handle, [MarshalAs(UnmanagedType.LPWStr)] string name, ref NativeInterface iface, ref NativePeer peer, ref NativeExtra extra);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)] private delegate int HandleBool(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)] private delegate int DropTunnel(nint handle, int preserveLock);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)] private delegate int NetworkLock(nint handle, int mode);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
    private delegate int SetGlobalOption([MarshalAs(UnmanagedType.LPWStr)] string option, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate NativeStats GetStats(nint handle);
    [StructLayout(LayoutKind.Sequential)] private struct NativeStats { public long HandshakeAge; public ulong TxBytes, RxBytes; public float Loss; public int Rtt; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeTunnelEvent { public uint Type, Status; public nuint Data; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInterface
    {
        public nint PrivateKey, Address, Dns, Mtu, ListenPort;
        public nint ScriptTimeout, PreUp, PostUp, PreDown, PostDown;
        public nint Jc, Jmin, Jmax, Jd, Id, Ip, Ib, S1, S2, S3, S4, H1, H2, H3, H4;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePeer { public nint PublicKey, PresharedKey, AllowedIps, Endpoint; public uint KeepAlive; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeExtra { public nuint Size; public nint AllowedApps, IgnoredIps, DisallowedApps, Proxy, Username, Password, AllTraffic; }
}
