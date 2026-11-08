using System.Reflection;
using System.Runtime.InteropServices;
using RouteFinder.Core;

// Synthetic keys and a reserved .invalid hostname are parser fixtures, never connection defaults.
var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
string valid = $"[Interface]\nPrivateKey={key}\nAddress=10.0.0.2/32\nDNS=1.1.1.1\nMTU=1280\n[Peer]\nPublicKey={key}\nAllowedIPs=0.0.0.0/0, ::/0\nEndpoint=fixture.invalid:51820\nPersistentKeepalive=25\n";
int passed = 0, failed = 0;
void Check(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
}
void Require(bool value, string message) { if (!value) throw new Exception(message); }
void Reject(Action action)
{
    try { action(); } catch (FormatException) { return; }
    throw new Exception("Invalid configuration was accepted.");
}
Check("valid configuration preserves routing and secrets", () =>
{
    var profile = TunnelProfile.Parse(valid, "Fixture");
    Require(profile.Name == "Fixture" && profile.GetPeer("AllowedIPs") == "0.0.0.0/0, ::/0" && profile.GetInterface("privatekey") == key, "Values changed during import.");
});
Check("case-insensitive keys", () => TunnelProfile.Parse(valid.Replace("PrivateKey", "privatekey").Replace("[Peer]", "[peer]")));
Check("IPv6 endpoint and address", () =>
{
    var profile = TunnelProfile.Parse(valid.Replace("fixture.invalid:51820", "[2001:db8::1]:51820").Replace("10.0.0.2/32", "2001:db8::2/128"));
    var (host, port) = TunnelProfile.SplitEndpoint(profile.Endpoint);
    Require(host == "2001:db8::1" && port == 51820, "IPv6 endpoint parsed incorrectly.");
});
Check("reject missing peer", () => Reject(() => TunnelProfile.Parse(valid[..valid.IndexOf("[Peer]", StringComparison.Ordinal)])));
Check("reject invalid key", () => Reject(() => TunnelProfile.Parse(valid.Replace("PrivateKey=" + key, "PrivateKey=invalid"))));
Check("reject invalid CIDR", () => Reject(() => TunnelProfile.Parse(valid.Replace("10.0.0.2/32", "10.0.0.2/33"))));
Check("reject invalid port", () => Reject(() => TunnelProfile.Parse(valid.Replace(":51820", ":65536"))));
Check("reject malformed host", () => Reject(() => TunnelProfile.Parse(valid.Replace("fixture.invalid:51820", "bad host:51820"))));
Check("reject unknown option without silently losing it", () => Reject(() => TunnelProfile.Parse(valid.Replace("MTU=1280", "UnsupportedThing=1"))));
Check("reject scripts before connection", () =>
{
    foreach (var script in new[] { "PreUp", "PostUp", "PreDown", "PostDown", "ScriptTimeout" })
        Reject(() => TunnelProfile.Parse(valid.Replace("MTU=1280", script + "=anything")));
});
Check("reject multi-peer configuration", () => Reject(() => TunnelProfile.Parse(valid + "[Peer]\nPublicKey=" + key)));
Check("reject duplicate key differing by case", () => Reject(() => TunnelProfile.Parse(valid.Replace("MTU=1280", "MTU=1280\nmtu=1420"))));
Check("reject conflicting app routing", () => Reject(() => TunnelProfile.Parse(valid.Replace("MTU=1280", "AllowedApps=a.exe\nDisallowedApps=b.exe"))));
Check("extended Amnezia options preserved", () =>
{
    var profile = TunnelProfile.Parse(valid.Replace("MTU=1280", "S3=4\nS4=5\nH4=6789"));
    Require(profile.GetInterface("S4") == "5" && profile.GetInterface("H4") == "6789", "Extended options lost.");
});
Check("scripts rejected in deserialized profiles", () =>
{
    var profile = TunnelProfile.Parse(valid); profile.Interface["PostUp"] = "anything";
    Reject(profile.Validate);
});
Check("x64 native structure sizes match installed header", () =>
{
    Require(Environment.Is64BitProcess, "Checks must run x64.");
    foreach (var (name, size) in new[] { ("NativeInterface", 200), ("NativePeer", 40), ("NativeExtra", 64), ("NativeStats", 32) })
    {
        Type type = typeof(WireSockTunnel).GetNestedType(name, BindingFlags.NonPublic) ?? throw new Exception("Missing native structure.");
        Require(Marshal.SizeOf(type) == size, $"{name} ABI size mismatch.");
    }
});
Check("load/bind/dispose installed SDK without creating handle", () =>
{
    using var tunnel = new WireSockTunnel();
    Require(File.Exists(tunnel.SdkPath), "SDK path missing.");
    Require(!tunnel.IsActive && tunnel.GetState() == default, "An unused wrapper must not have a tunnel.");
    tunnel.Stop();
    tunnel.Dispose();
    tunnel.Dispose();
});
Check("Windows connection policy structs match the x64 SDK layout", () =>
{
    foreach (var (name, size, member, offset) in new[]
    {
        ("Session", 72, "Sid", 48), ("ProviderContext", 88, "Settings", 72),
        ("Condition", 40, "Value", 24), ("PolicySetting", 24, "Value", 8),
        ("PolicySettings", 16, "Items", 8)
    })
    {
        var type = typeof(DirectAppRouting).GetNestedType(name, BindingFlags.NonPublic)!;
        Require(Marshal.SizeOf(type) == size && Marshal.OffsetOf(type, member).ToInt32() == offset,
            $"{name} layout does not match the Windows SDK.");
    }
});
Check("reject relative app paths before native handle creation", () =>
{
    using var tunnel = new WireSockTunnel();
    try { tunnel.Connect(TunnelProfile.Parse(valid), new[] { "relative.exe" }); }
    catch (ArgumentException) { Require(!tunnel.IsActive, "Native handle should not be active."); return; }
    throw new Exception("Relative application path accepted.");
});
Check("marshal app lists using the installed SDK version's separator", () =>
{
    using var tunnel = new WireSockTunnel();
    var wide = typeof(WireSockTunnel).GetMethod("Wide", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var format = typeof(WireSockTunnel).GetMethod("FormatAppList", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Func<string[], string?, bool, string>>();
    string[] paths = [@"C:\Telegram Desktop\Telegram.exe", @"C:\Apps\Discord.exe"];
    const string version = "3.4.8.1";
    nint allowed = (nint)wide.Invoke(tunnel, [format(paths, version, false)])!;
    Require(Marshal.PtrToStringUni(allowed) == string.Join(',', paths),
        "WireSock 3.4.8.1 must receive two comma-separated full paths.");
    Require(format([@"C:\", @"D:\"], version, false) == @"C:\,D:\", "Console exclusions must use the same separator.");
    Require(format(paths, null, false) == string.Join('|', paths), "Unverified SDKs use the documented separator.");
    Require(format(paths, null, true) == string.Join(',', paths), "The diagnostic switch must force the comma separator.");
    Require(format([paths[0]], version, false) == paths[0], "A single path needs no delimiter.");
    Require((nint)wide.Invoke(tunnel, [format([], version, false)])! == 0 &&
        (nint)wide.Invoke(tunnel, [""])! == 0, "Unused allow and deny lists must marshal as null.");
    try { format([@"C:\Apps, Inc\Telegram.exe"], version, false); }
    catch (ArgumentException) { return; }
    throw new Exception("A comma within a path would be parsed as a second app rule.");
});
Check("teardown preserves unowned locks and reports cleanup failures", () =>
{
    foreach (bool ownsLock in new[] { false, true })
    {
        using var tunnel = new WireSockTunnel();
        var native = new CleanupFixture();
        FieldInfo Field(string name) => typeof(WireSockTunnel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Replace(string name, string method)
        {
            var field = Field(name);
            field.SetValue(tunnel, Delegate.CreateDelegate(field.FieldType, native, method));
        }
        Replace("getActive", nameof(CleanupFixture.Active));
        Replace("stopTunnel", nameof(CleanupFixture.Stop));
        Replace("setNetworkLock", nameof(CleanupFixture.Unlock));
        Replace("dropTunnel", nameof(CleanupFixture.Drop));
        Replace("releaseHandle", nameof(CleanupFixture.Release));
        Field("handle").SetValue(tunnel, (nint)1);
        Field("created").SetValue(tunnel, true);
        Field("ownsNetworkLock").SetValue(tunnel, ownsLock);

        try { tunnel.Stop(); throw new Exception("Cleanup reported success."); }
        catch (AggregateException error)
        {
            Require(error.InnerExceptions.Count == (ownsLock ? 3 : 2), "Cleanup failures were lost.");
        }
        Require(native.Calls.SequenceEqual(ownsLock
            ? ["stop", "unlock:0", "drop:0", "release"] : ["stop", "drop:1", "release"]),
            "Cleanup did not respect lock ownership or skipped a step.");
        Require(!tunnel.IsActive, "The released native handle is still owned.");
    }
});
Check("reject empty app selection before native handle creation", () =>
{
    using var tunnel = new WireSockTunnel();
    try { tunnel.Connect(TunnelProfile.Parse(valid), Array.Empty<string>()); }
    catch (ArgumentException) { Require(!tunnel.IsActive, "Native handle should not be active."); return; }
    throw new Exception("Empty application selection accepted.");
});
Console.WriteLine($"{passed} passed; {failed} failed. No native handle was created and no tunnel was started.");
return failed == 0 ? 0 : 1;

sealed class CleanupFixture
{
    public List<string> Calls { get; } = [];
    public int Active(nint _) => 1;
    public int Stop(nint _) { Calls.Add("stop"); return 0; }
    public int Unlock(nint _, int mode) { Calls.Add($"unlock:{mode}"); return 0; }
    public int Drop(nint _, int preserve) { Calls.Add($"drop:{preserve}"); return 0; }
    public void Release(nint _) => Calls.Add("release");
}
