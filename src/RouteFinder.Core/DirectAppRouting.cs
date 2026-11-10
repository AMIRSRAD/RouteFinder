using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RouteFinder.Core;

public sealed class DirectAppRouting : IDisposable
{
    public sealed record Rule(string Executable, string AdapterId);

    private readonly EngineHandle engine;
    private readonly Semaphore policySettings;
    private readonly HashSet<string> enabledFamilies = [];
    private bool stopped;
    private DirectAppRouting(EngineHandle engine, Semaphore policySettings)
    {
        this.engine = engine;
        this.policySettings = policySettings;
    }

    public static bool IsSupported()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess ||
            !NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "fwpuclnt.dll"), out nint library))
            return false;
        try { return NativeLibrary.TryGetExport(library, "FwpmConnectionPolicyAdd0", out _); }
        finally { NativeLibrary.Free(library); }
    }

    public static bool HasInternetGateway(NetworkInterface adapter) =>
        adapter.OperationalStatus == OperationalStatus.Up &&
        adapter.GetIPProperties().GatewayAddresses.Any(gateway =>
            !gateway.Address.Equals(IPAddress.Any) && !gateway.Address.Equals(IPAddress.IPv6Any));

    public static string? DefaultIpv4AdapterName(IEnumerable<NetworkInterface> adapters)
    {
        if (TunnelUplink.DefaultIpv4InterfaceIndex() is not uint index) return null;
        return adapters.FirstOrDefault(adapter => adapter.Supports(NetworkInterfaceComponent.IPv4) &&
            adapter.GetIPProperties().GetIPv4Properties()?.Index == index)?.Name;
    }

    /// <summary>
    /// Route-policy families (ipv4, ipv6) this instance switched on. When replacing one set of rules with another,
    /// pass these to the next Start so Windows' setting stays on instead of being turned off and on again.
    /// </summary>
    public string[] EnabledFamilies => enabledFamilies.ToArray();

    public static DirectAppRouting Start(IEnumerable<Rule> rules, string? fallbackAdapterId = null,
        IEnumerable<string>? retainedFamilies = null)
    {
        if (!IsSupported()) throw new PlatformNotSupportedException("This Windows build does not expose per-app connection policies.");
        var adapters = NetworkInterface.GetAllNetworkInterfaces().ToDictionary(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configured = rules.Select(rule =>
        {
            if (!Path.IsPathFullyQualified(rule.Executable) || !rule.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(rule.Executable))
                throw new ArgumentException("Choose an existing executable for each direct routing rule.", nameof(rules));
            string path = Path.GetFullPath(rule.Executable);
            if (!paths.Add(path)) throw new ArgumentException("Each executable can use one direct routing adapter.", nameof(rules));
            if (!adapters.TryGetValue(rule.AdapterId, out var adapter) || adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                throw new InvalidOperationException($"The adapter for {Path.GetFileName(path)} is unavailable.");
            Check(ConvertInterfaceGuidToLuid(Guid.Parse(adapter.Id), out ulong luid), $"Resolve adapter {adapter.Name}");
            return (Path: path, Luid: luid);
        }).ToArray();
        ulong? fallbackLuid = null;
        if (fallbackAdapterId is not null)
        {
            if (!adapters.TryGetValue(fallbackAdapterId, out var fallback) || !HasInternetGateway(fallback) ||
                fallback.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                throw new InvalidOperationException("Choose a connected default adapter with an internet gateway.");
            Check(ConvertInterfaceGuidToLuid(Guid.Parse(fallback.Id), out ulong luid), $"Resolve adapter {fallback.Name}");
            fallbackLuid = luid;
        }
        if (configured.Length == 0 && fallbackLuid is null)
            throw new ArgumentException("Add an app rule or choose a default adapter.", nameof(rules));
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Windows user identity is unavailable.");
        var descriptor = new RawSecurityDescriptor($"D:(A;;0x1;;;{user.Value})"); // FWP_ACTRL_MATCH_FILTER
        var userCondition = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(userCondition, 0);

        // Dynamic policies are scoped to this handle, including if the process exits unexpectedly.
        var session = new Session { Flags = 1, TransactionWaitMilliseconds = 5000 };
        var policySettings = new Semaphore(1, 1, @"Global\RouteFinder.WindowsRouting");
        if (!policySettings.WaitOne(0))
        {
            policySettings.Dispose();
            throw new InvalidOperationException("Windows app routing is already active in another RouteFinder process.");
        }
        DirectAppRouting? routing = null;
        try
        {
            uint opened = FwpmEngineOpen0(0, 10, 0, ref session, out var engine); // RPC_C_AUTHN_WINNT
            Check(opened, "Open Windows Filtering Platform");
            routing = new DirectAppRouting(engine, policySettings);
            Check(FwpmTransactionBegin0(engine, 0), "Begin direct routing update");
            foreach (var rule in configured)
            {
                AddPolicy(engine, rule.Path, rule.Luid, 0, userCondition); // IPv4
                AddPolicy(engine, rule.Path, rule.Luid, 1, userCondition); // IPv6
            }
            if (fallbackLuid is ulong defaultLuid)
            {
                AddPolicy(engine, null, defaultLuid, 0, userCondition);
                AddPolicy(engine, null, defaultLuid, 1, userCondition);
            }
            Check(FwpmTransactionCommit0(engine), "Apply direct routing rules");
            var retained = (retainedFamilies ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string family in new[] { "ipv4", "ipv6" })
            {
                if (retained.Contains(family)) routing.enabledFamilies.Add(family);
                else routing.EnableRoutePolicies(family);
            }
            return routing;
        }
        catch (Exception startupError)
        {
            try { routing?.Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException("Windows routing startup and rollback failed.", startupError, cleanupError); }
            finally { policySettings.Dispose(); }
            throw;
        }
    }

    private static unsafe void AddPolicy(EngineHandle engine, string? path, ulong interfaceLuid, uint ipVersion, byte[] userCondition)
    {
        nint appId = 0;
        if (path is not null)
            Check(FwpmGetAppIdFromFileName0(path, out appId), $"Resolve {Path.GetFileName(path)}");
        try
        {
            var setting = new PolicySetting
            {
                Type = 1, // FWP_NETWORK_CONNECTION_POLICY_NEXT_HOP_INTERFACE
                Value = new Value { Type = 4, Data = (nuint)(&interfaceLuid) } // FWP_UINT64 is a pointer
            };
            var settings = new PolicySettings { Count = 1, Items = &setting };
            var conditions = stackalloc Condition[3];
            conditions[0] = new Condition { Key = AppIdKey, Value = new Value { Type = 12, Data = (nuint)appId } };
            conditions[1] = new Condition { Key = FlagsKey, Match = 8, Value = new Value { Type = 3, Data = 1 } };
            // FWP_MATCH_FLAGS_NONE_SET with FWP_CONDITION_FLAG_IS_LOOPBACK keeps local IPC working.
            var context = new ProviderContext { Key = Guid.NewGuid(), Type = 13, Settings = &settings }; // FWPM_NETWORK_CONNECTION_POLICY_CONTEXT
            fixed (char* name = "RouteFinder direct app routing")
            fixed (byte* user = userCondition)
            {
                var descriptor = new Blob { Size = (uint)userCondition.Length, Data = user };
                conditions[2] = new Condition { Key = UserIdKey, Value = new Value { Type = 14, Data = (nuint)(&descriptor) } };
                context.Name = (nint)name;
                // The fallback omits the app condition and ranks below explicit app assignments.
                Check(FwpmConnectionPolicyAdd0(engine, ref context, ipVersion, path is null ? 0u : 1u,
                    path is null ? 2u : 3u, path is null ? conditions + 1 : conditions, 0),
                    $"Set {(ipVersion == 0 ? "IPv4" : "IPv6")} policy for {(path is null ? "unassigned apps" : Path.GetFileName(path))}");
            }
        }
        finally { if (appId != 0) FwpmFreeMemory0(ref appId); }
    }

    public void Dispose() => Release(restorePolicies: true);

    /// <summary>
    /// Removes this instance's rules. With restorePolicies false, Windows' route-policy setting is left on for the
    /// next instance; the caller must then start one or call <see cref="RestorePolicies"/>.
    /// </summary>
    public void Release(bool restorePolicies)
    {
        if (stopped) return;
        var failures = new List<Exception>();
        // Restore route policies even when closing the filtering engine fails; a retry closes the engine again.
        try { engine.CloseChecked(); }
        catch (Exception error) { failures.Add(error); }
        foreach (string family in restorePolicies ? enabledFamilies.ToArray() : [])
        {
            try
            {
                RunNetsh(family, "set", "global", "routepolicies=disabled", "store=active");
                enabledFamilies.Remove(family);
            }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count > 0) throw new AggregateException("Could not restore Windows route policy settings.", failures);
        stopped = true;
        policySettings.Release();
        policySettings.Dispose();
    }

    // netsh prints localized labels and values, so the setting is detected by comparing the parameter block
    // before and after enabling it. Only a family this process changed is restored on Dispose.
    /// <summary>Turns Windows route policies back off for families that were kept on across a swap that then failed.</summary>
    public static void RestorePolicies(IEnumerable<string> families)
    {
        var failures = new List<Exception>();
        foreach (string family in families)
        {
            try { RunNetsh(family, "set", "global", "routepolicies=disabled", "store=active"); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count > 0) throw new AggregateException("Could not restore Windows route policy settings.", failures);
    }

    private void EnableRoutePolicies(string family)
    {
        var before = GlobalParameters(family);
        try { RunNetsh(family, "set", "global", "routepolicies=enabled", "store=active"); }
        catch
        {
            if (!before.SequenceEqual(GlobalParameters(family))) enabledFamilies.Add(family);
            throw;
        }
        if (!before.SequenceEqual(GlobalParameters(family))) enabledFamilies.Add(family);
    }

    private static string[] GlobalParameters(string family)
    {
        var lines = RunNetsh(family, "show", "global").Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        int rule = Array.FindIndex(lines, line => line.Trim().Length >= 3 && line.Trim().All(character => character == '-'));
        if (rule < 0) throw new InvalidOperationException($"Could not read the Windows {family.ToUpperInvariant()} global settings.");
        // The first block after the heading rule holds the settings; later blocks are live statistics.
        return lines.Skip(rule + 1).TakeWhile(line => line.Trim().Length > 0).ToArray();
    }

    private static string RunNetsh(string family, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "interface", family }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Windows networking helper.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill();
            process.WaitForExit();
            throw new TimeoutException($"Windows did not finish updating {family.ToUpperInvariant()} route policies.");
        }
        if (process.ExitCode != 0 || (output.GetAwaiter().GetResult().Trim().Length == 0 && errors.GetAwaiter().GetResult().Trim().Length > 0))
            throw new InvalidOperationException($"Windows could not manage {family.ToUpperInvariant()} route policies. Verify administrator access and Windows support.");
        return output.GetAwaiter().GetResult();
    }

    private static void Check(uint status, string action)
    {
        if (status == 5)
            throw new UnauthorizedAccessException($"{action} was denied. Run as administrator and check Windows policy permissions.");
        if (status != 0)
            throw new Win32Exception(unchecked((int)status), $"{action} failed (Windows error 0x{status:X8}).");
    }

    private static readonly Guid AppIdKey = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid UserIdKey = new("af043a0a-b34d-4f86-979c-c90371af6e66");
    private static readonly Guid FlagsKey = new("632ce23b-5167-435c-86d7-e903684aa80c");

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Blob { public uint Size; public byte* Data; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Value { public uint Type; public nuint Data; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Condition { public Guid Key; public uint Match; public Value Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PolicySetting { public uint Type; public Value Value; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PolicySettings { public uint Count; public PolicySetting* Items; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProviderContext
    {
        public Guid Key;
        public nint Name, Description;
        public uint Flags;
        public nint ProviderKey;
        public uint ProviderDataSize;
        public nint ProviderData;
        public uint Type;
        public PolicySettings* Settings;
        public ulong Id;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Session
    {
        public Guid Key;
        public nint Name, Description;
        public uint Flags, TransactionWaitMilliseconds, ProcessId;
        public nint Sid, Username;
        public int KernelMode;
    }

    private sealed class EngineHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        public void CloseChecked()
        {
            if (IsClosed || IsInvalid) return;
            Check(FwpmEngineClose0(handle), "Remove Windows app routing rules");
            SetHandleAsInvalid();
        }

        protected override bool ReleaseHandle() => FwpmEngineClose0(handle) == 0;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint ConvertInterfaceGuidToLuid(in Guid id, out ulong luid);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmEngineOpen0(nint server, uint auth, nint identity, ref Session session, out EngineHandle engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmEngineClose0(nint engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmTransactionBegin0(EngineHandle engine, uint flags);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmTransactionCommit0(EngineHandle engine);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint FwpmGetAppIdFromFileName0(string path, out nint appId);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern void FwpmFreeMemory0(ref nint memory);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern unsafe uint FwpmConnectionPolicyAdd0(EngineHandle engine, ref ProviderContext context,
        uint ipVersion, ulong weight, uint conditionCount, Condition* conditions, nint securityDescriptor);
}
