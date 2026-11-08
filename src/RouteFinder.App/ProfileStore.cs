using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RouteFinder.App;

public sealed class SavedProfile
{
    public string Name { get; set; } = "";
    public string Configuration { get; set; } = "";
    public List<string> Applications { get; set; } = [];
    public override string ToString() => Name;
}

internal static class ProfileStore
{
    internal static string? VerificationDirectory { get; set; }
    public static string DirectoryPath => VerificationDirectory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RouteFinder");
    public static string FilePath => Path.Combine(DirectoryPath, "profiles.dat");
    static string AppPath => Path.Combine(DirectoryPath, "applications.dat");
    static string LoginPath => Path.Combine(DirectoryPath, "login.dat");
    static string PreferencesPath => Path.Combine(DirectoryPath, "preferences.dat");
    static string MeasurementsPath => Path.Combine(DirectoryPath, "route-tests.dat");
    static string PresetsPath => Path.Combine(DirectoryPath, "presets.dat");
    internal sealed record RememberedLogin(string Email, string RefreshToken);
    internal sealed record AppRoutingProfile(string Name, Dictionary<string, string> Adapters,
        Dictionary<string, string> VpnRoutes, string? DefaultAdapter);
    /// <summary>The tunnel and connection options a preset restores. Proxy credentials are never stored here.</summary>
    internal sealed record PresetSettings(bool KillSwitch, bool PortableEngine, bool AutoMtu, bool BypassLan, string DnsOverride,
        string BypassDestinations, string[] ExcludedApplications, int? TunnelMtu, int? KeepaliveSeconds, int PeerResponseSeconds,
        int RouteTestSeconds, int OnDemandIdleSeconds, bool MinimizeToTray, bool RecoverStoppedTunnel);

    /// <summary>
    /// A saved setup. Older presets only hold the route, traffic scope, apps, and adapter. A full preset also
    /// records which listed apps are on, how each connects, the default adapter, and the connection settings.
    /// </summary>
    internal sealed record RoutingPreset(string Name, string RouteKey, string RouteName, bool EntirePc, string[] Apps, string? CatalogGame,
        bool BlockOtherApps = false, string? AdapterId = null, bool HasAdapterChoice = false,
        string[]? EnabledApps = null, Dictionary<string, string>? AppAdapters = null, Dictionary<string, string>? AppVpnRoutes = null,
        string? DefaultAdapter = null, bool HasDefaultAdapter = false, PresetSettings? Settings = null)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsFull => EnabledApps is not null;
    }
    internal sealed record Preferences(bool MinimizeToTray, List<string> Favorites, bool EntirePcMode = false,
        bool KillSwitch = false, bool PortableEngine = false, string DnsOverride = "",
        bool Ipv6PreferenceConfigured = false, bool EnableIpv6Bindings = false, bool AutoMtu = false,
        bool AutoAddLinkedApps = true, int WindowPreset = 0, bool ShowTutorials = false, int SeenTutorials = 0,
        bool BypassLan = false, string BypassDestinations = "", int OnDemandIdleSeconds = 30,
        int? KeepaliveSeconds = null, string[]? ExcludedApplications = null, int? TunnelMtu = null,
        string? LastConnectedRoute = null, int PeerResponseSeconds = 25, int RouteTestSeconds = 20,
        int RouteSortMetric = 0, string SocksProxyOverride = "", ProxyOverride? SocksProxyCredentials = null,
        bool RecoverStoppedTunnel = false, string[]? UncheckedApplications = null, bool BlockOtherApps = false,
        Dictionary<string, string>? RouteUplinks = null, Dictionary<string, string>? DirectAppAdapters = null,
        string? DirectFallbackAdapter = null, Dictionary<string, string>? AppVpnRoutes = null,
        List<AppRoutingProfile>? AppRoutingProfiles = null);

    public static Preferences LoadPreferences() => File.Exists(PreferencesPath)
        ? JsonSerializer.Deserialize<Preferences>(Protect(File.ReadAllBytes(PreferencesPath), false)) ?? new(false, []) : new(false, []);
    public static void SavePreferences(Preferences value) => SaveProtected(PreferencesPath, value);
    public static Dictionary<string, RouteTestResult> LoadMeasurements()
    {
        var results = File.Exists(MeasurementsPath)
            ? JsonSerializer.Deserialize<Dictionary<string, RouteTestResult>>(Protect(File.ReadAllBytes(MeasurementsPath), false)) ?? [] : [];
        foreach (string key in results.Keys.ToArray())
            results[key] = results[key] with { Jitter = null, RttSpread = null };
        return results;
    }
    public static void SaveMeasurements(Dictionary<string, RouteTestResult> value) => SaveProtected(MeasurementsPath, value);
    public static List<RoutingPreset> LoadPresets() => File.Exists(PresetsPath)
        ? JsonSerializer.Deserialize<List<RoutingPreset>>(Protect(File.ReadAllBytes(PresetsPath), false)) ?? [] : [];
    public static void SavePresets(List<RoutingPreset> value) => SaveProtected(PresetsPath, value);

    public static RememberedLogin? LoadLogin()
    {
        if (!File.Exists(LoginPath)) return null;
        var record = JsonSerializer.Deserialize<RememberedLogin>(Protect(File.ReadAllBytes(LoginPath), false));
        return string.IsNullOrWhiteSpace(record?.Email) || string.IsNullOrWhiteSpace(record.RefreshToken) ? null : record;
    }

    public static void SaveLogin(string email, string refreshToken) => SaveProtected(LoginPath, new RememberedLogin(email, refreshToken));
    public static void ClearLogin() { if (File.Exists(LoginPath)) File.Delete(LoginPath); }
    static string CleanupPath(string email) => Path.Combine(DirectoryPath, "cleanup-" +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToUpperInvariant()))) + ".dat");
    public static string? LoadAllocation(string email)
    {
        var path = CleanupPath(email);
        if (!File.Exists(path)) return null;
        // An unreadable record (corrupt, or protected for another Windows user) must not block signing in.
        try { return JsonSerializer.Deserialize<string>(Protect(File.ReadAllBytes(path), false)); }
        catch (Exception error) when (error is JsonException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
    public static void SaveAllocation(string email, string? id)
    {
        var path = CleanupPath(email);
        if (id is null) File.Delete(path);
        else SaveProtected(path, id);
    }
    public static List<SavedProfile> Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<List<SavedProfile>>(Protect(File.ReadAllBytes(FilePath), false)) ?? [] : [];
    public static void Save(List<SavedProfile> profiles)
        => SaveProtected(FilePath, profiles);

    public static List<string>? LoadApplications() => File.Exists(AppPath)
        ? JsonSerializer.Deserialize<List<string>>(Protect(File.ReadAllBytes(AppPath), false)) : null;

    public static void SaveApplications(List<string> paths) => SaveProtected(AppPath, paths);

    static void SaveProtected<T>(string path, T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, Protect(bytes, true));
            File.Move(temp, path, true);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob data, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    internal static byte[] Protect(byte[] data, bool encrypt)
    {
        var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            Blob output;
            bool ok = encrypt ? CryptProtectData(ref input, "RouteFinder profiles", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.Copy(new byte[data.Length], 0, input.Data, data.Length); Marshal.FreeHGlobal(input.Data); }
    }
}
