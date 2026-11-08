using System.Net;
using System.Text;
using System.Text.Json;
using RouteFinder.Service;
using RouteFinder.App;

if (args.Contains("--compare-local-device"))
{
    var expected = Console.ReadLine()?.Trim();
    if (expected?.Length != 64 || expected.Any(c => !char.IsAsciiHexDigit(c))) return 2;
    var match = string.Equals(DeviceIdentity.ReadCurrent().DeviceHash, expected, StringComparison.OrdinalIgnoreCase);
    Console.WriteLine(match ? "Device fingerprint matches EZConnect's saved session." : "Device fingerprint differs from EZConnect's saved session.");
    return match ? 0 : 1;
}

int passed = 0, failed = 0;
async Task Check(string name, Func<Task> action) { try { await action(); Console.WriteLine("PASS " + name); passed++; } catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e.Message); failed++; } }
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
Task Sync(Action action) { action(); return Task.CompletedTask; }
const string AliceSecret = "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a";
const string AlicePublic = "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a";
string privateKey = Convert.ToBase64String(Convert.FromHexString(AliceSecret));
string publicKey = Convert.ToBase64String(Convert.FromHexString(AlicePublic));
string peerKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
var route = new ServiceRoute("Test route", "DE", "Test", false);
var device = new DeviceDescriptor(new string('a', 64), "Windows", "test", "test-device");

await Check("Verified original fingerprint composition", () => Sync(() =>
{
    var fingerprint = new DeviceFingerprint("cpu", "board", "ram", "uuid");
    Assert(fingerprint.HashInput == "cpu_model:cpu|motherboard_model:board|ram_model:ram|system_uuid:uuid", "Wrong field ordering");
    Assert(fingerprint.DeviceHash == "f9817cb65dd5a0cd20ddb64093880aaaabf8909ee9fb72cf93d3d39557f92e3f", "Wrong SHA256");
}));

await Check("RFC7748 Alice public-key vector", () => Sync(() => Assert(Convert.ToHexString(WireGuardKeys.PublicFromPrivate(Convert.FromHexString(AliceSecret))).Equals(AlicePublic, StringComparison.OrdinalIgnoreCase), "Public key mismatch")));
await Check("RFC7748 Bob public-key vector", () => Sync(() => Assert(Convert.ToHexString(WireGuardKeys.PublicFromPrivate(Convert.FromHexString("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb"))).Equals("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f", StringComparison.OrdinalIgnoreCase), "Public key mismatch")));
await Check("Fresh random key pairs validate and redact", () => Sync(() => { var keys = WireGuardKeys.Generate(); Assert(Convert.FromBase64String(keys.PrivateKey).Length == 32 && keys.PublicKey != keys.PrivateKey && !keys.ToString().Contains(keys.PrivateKey), "Invalid pair"); }));
await Check("Login origin, request and token parsing", async () =>
{
    var handler = new FakeHandler(async request =>
    {
        Assert(request.RequestUri!.AbsoluteUri == "https://desktop-windows-app-login.ezconnect.ir/api/desktop/auth/login", "Wrong login target");
        Assert(request.Method == HttpMethod.Post && request.Headers.GetValues("X-EzConnect-Version").Single() == "6.8.1", "Wrong headers");
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert(json.RootElement.GetProperty("device").GetProperty("device_hash").GetString() == device.DeviceHash, "Device mapping");
        Assert(json.RootElement.GetProperty("device").GetProperty("gpu_model").GetString() == "", "Original empty GPU field not preserved");
        Assert(json.RootElement.GetProperty("password").GetString() == "test-password", "Password missing");
        return Reply("{\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\",\"expires_in\":100}");
    });
    using var client = new EzConnectClient(handler); var tokens = await client.LoginAsync("test@example.invalid", "test-password", device);
    Assert(tokens.AccessToken == "test-access" && !tokens.ToString().Contains(tokens.AccessToken), "Token handling");
});
await Check("Device mismatch is not bypassed", async () =>
{
    using var client = new EzConnectClient(new FakeHandler(_ => Task.FromResult(Reply("{\"mismatch\":true,\"access_token\":\"x\",\"refresh_token\":\"y\"}"))));
    try { await client.LoginAsync("test@example.invalid", "pw", device); throw new Exception("Mismatch accepted"); } catch (EzConnectServiceException e) { Assert(e.Code == "DEVICE_MISMATCH", "Wrong failure"); }
});
await Check("Bootstrap uses bearer and retains game rules", async () =>
{
    using var client = new EzConnectClient(new FakeHandler(request =>
    {
        Assert(request.Method == HttpMethod.Get && request.RequestUri!.Host == "desktop-windows-app-login.ezconnect.ir", "Wrong bootstrap request");
        Assert(request.Headers.Authorization?.ToString() == "Bearer test-token", "No bearer");
        return Task.FromResult(Reply("{\"routes\":[{\"name\":\"R\",\"region_code\":\"DE\",\"ipv6_enabled\":true}],\"games\":[{\"name\":\"Game\",\"exe_name\":\"game.exe,launcher.exe\",\"global_disallowed_ips\":\"192.168.0.0/16\"}]}"));
    }));
    var bootstrap = await client.BootstrapAsync("test-token"); Assert(bootstrap.Routes.Count == 1 && bootstrap.Games[0].ExecutableNames.Length == 2 && bootstrap.Games[0].GlobalDisallowedIps[0] == "192.168.0.0/16", "Lost settings");
});
string GoodResponse() => JsonSerializer.Serialize(new { session_id = "test-session", config = new { Interface = new { Address = "10.0.0.2/32", MTU = 1380 }, Peer = new { PublicKey = peerKey, Port = 51820 } }, routes = new[] { new { name = route.Name, route_endpoint = "192.0.2.1", route_allowedips = "0.0.0.0/0", route_dns = "1.1.1.1" } } });
await Check("Cancel during provisioning releases the allocated session", async () =>
{
    using var cancel = new CancellationTokenSource();
    bool released = false;
    using var connection = new ConnectionSession(new EzConnectClient(new FakeHandler(async request =>
    {
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/api/desktop/auth/refresh":
                return Reply("""{"access_token":"test-access","refresh_token":"test-refresh"}""");
            case "/api/desktop/bootstrap":
                return Reply("""{"routes":[],"games":[]}""");
            case "/api/desktop/sessions/connect":
                cancel.Cancel();
                return Reply(GoodResponse());
            case "/api/desktop/sessions/disconnect":
                using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()))
                    released = body.RootElement.GetProperty("session_id").GetString() == "test-session";
                return Reply("{}");
            default:
                throw new InvalidOperationException("Unexpected service request.");
        }
    })));
    await connection.RestoreAsync("test-refresh");
    try
    {
        await connection.TestAsync(route, null, cancel.Token);
        throw new InvalidOperationException("The canceled test continued.");
    }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    Assert(released && !connection.HasTunnel && !connection.NeedsCleanup, "The canceled test left an allocation behind.");
});
await Check("Read route details nested inside the WireGuard peer", () => Sync(() =>
{
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        session_id = "test-session",
        config = new
        {
            Interface = new { Address = "10.0.0.2/32" },
            Peer = new
            {
                PublicKey = peerKey,
                Port = 3478,
                routes = new[] { new { name = route.Name, route_endpoint = "192.0.2.1", route_allowedips = "0.0.0.0/0,::/0" } }
            }
        }
    }));
    var session = EzConnectClient.ParseProvisionedSession(json.RootElement, route, privateKey);
    Assert(session.Profile.Endpoint == "192.0.2.1:3478" && session.Profile.GetPeer("AllowedIPs").Contains("::/0"), "Missing nested route settings");
    Assert(ConnectionSession.RemoveUnusableServiceIpv6Routes(session.Profile) &&
        session.Profile.GetPeer("AllowedIPs") == "0.0.0.0/0", "IPv4-only tunnel retained an IPv6 default route");
    session.Profile.Interface["Address"] = "10.0.0.2/32,fd00::2/128";
    session.Profile.Peer["AllowedIPs"] = "0.0.0.0/0,::/0";
    Assert(!ConnectionSession.RemoveUnusableServiceIpv6Routes(session.Profile) &&
        session.Profile.GetPeer("AllowedIPs") == "0.0.0.0/0,::/0", "Dual-stack profile lost IPv6 routing");
}));
await Check("Import EZConnect settings offline without a session ID", () => Sync(() =>
{
    var json = GoodResponse().Replace("\"session_id\":\"test-session\",", "");
    var imported = RouteFinder.Core.TunnelProfile.Parse(EzConnectClient.ImportConfiguration(json, route.Name, () => privateKey));
    Assert(imported.Endpoint == "192.0.2.1:51820" && imported.Interface["PrivateKey"] == privateKey, "Offline import lost the endpoint or private key");
}));
await Check("Provision request and response create validated profile", async () =>
{
    using var client = new EzConnectClient(new FakeHandler(async request =>
    {
        Assert(request.RequestUri!.Host == "desktop-windows-app.ezconnect.ir", "Wrong pool origin");
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert(payload.RootElement.GetProperty("client_public_key").GetString() == publicKey && !payload.RootElement.TryGetProperty("private_key", out _), "Private key leaked");
        return Reply(GoodResponse());
    }));
    var session = await client.ConnectAsync(route, publicKey, privateKey, "token"); Assert(session.Profile.Endpoint == "192.0.2.1:51820" && session.Profile.GetInterface("PrivateKey") == privateKey && !session.ToString().Contains(privateKey), "Profile invalid");
});
await Check("Malformed service config cleans allocated session", async () =>
{
    var count = 0;
    using var client = new EzConnectClient(new FakeHandler(request => { count++; return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/disconnect") ? Reply("{\"success\":true}") : Reply("{\"session_id\":\"s\",\"config\":{}}")); }));
    try { await client.ConnectAsync(route, publicKey, privateKey, "token"); throw new Exception("Bad config accepted"); }
    catch (EzConnectServiceException error)
    {
        Assert(count == 2 && error.Message.Contains("temporary session was released"), "No confirmed cleanup");
    }
});
await Check("Stalled cleanup response body leaves a retryable allocation", async () =>
{
    using var client = new EzConnectClient(new FakeHandler(request => Task.FromResult(
        request.RequestUri!.AbsolutePath.EndsWith("/disconnect")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) }
            : Reply("""{"session_id":"stalled","config":{}}"""))), requestTimeout: TimeSpan.FromMilliseconds(250));
    try
    {
        await client.ConnectAsync(route, publicKey, privateKey, "token").WaitAsync(TimeSpan.FromSeconds(3));
        throw new Exception("Malformed profile was accepted.");
    }
    catch (PendingSessionCleanupException error)
    {
        Assert(error.SessionId == "stalled" && error.InnerException is OperationCanceledException,
            "The cleanup timeout did not preserve the allocation ID for retry.");
    }
});
await Check("Entire PC preserves provisioned destination ranges", async () =>
{
    const string restricted = "192.0.2.0/24";
    using var connection = new ConnectionSession(new EzConnectClient(new FakeHandler(request => Task.FromResult(
        Reply(request.RequestUri!.AbsolutePath switch
        {
            "/api/desktop/auth/refresh" => """{"access_token":"access","refresh_token":"refresh"}""",
            "/api/desktop/bootstrap" => "{}",
            "/api/desktop/sessions/connect" => GoodResponse().Replace("0.0.0.0/0", restricted + ",::/0"),
            "/api/desktop/sessions/disconnect" => "{}",
            _ => throw new InvalidOperationException("Unexpected service request.")
        })))));
    await connection.RestoreAsync("refresh");
    var prepare = typeof(ConnectionSession).GetMethod("PrepareAsync",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    try
    {
        var profile = await (Task<RouteFinder.Core.TunnelProfile>)prepare.Invoke(connection,
            [route with { HasIpv6 = true }, null, true, CancellationToken.None])!;
        Assert(profile.GetPeer("AllowedIPs") == restricted, "Entire PC widened the provisioned IPv4 range or kept unusable IPv6 routes.");
    }
    finally { await connection.DisconnectAsync(); }
});
await Check("A restarted session retries recorded cleanup for its account", async () =>
{
    const string account = "fixture@example.test";
    string? persisted = "previous-session";
    using var connection = new ConnectionSession(new EzConnectClient(new FakeHandler(async request =>
    {
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/api/desktop/auth/refresh":
                return Reply("""{"access_token":"access","refresh_token":"refresh"}""");
            case "/api/desktop/bootstrap":
                return Reply("{}");
            case "/api/desktop/sessions/disconnect":
                Assert((await request.Content!.ReadAsStringAsync()).Contains("previous-session"), "Wrong session released.");
                return Reply("{}");
            default:
                throw new InvalidOperationException("Unexpected service request.");
        }
    })), email => email == account ? persisted : null, (email, id) =>
    {
        Assert(email == account, "Cleanup was saved under the wrong account.");
        persisted = id;
    });
    await connection.RestoreAsync("refresh", email: account);
    Assert(connection.NeedsCleanup, "The recorded session was lost on restart.");
    await connection.DisconnectAsync();
    Assert(!connection.NeedsCleanup && persisted is null, "Confirmed release did not clear the recorded session.");
});
await Check("Mismatched keypair fails before request", async () =>
{
    var count = 0; using var client = new EzConnectClient(new FakeHandler(_ => { count++; return Task.FromResult(Reply("{}")); }));
    try { await client.ConnectAsync(route, peerKey, privateKey, "token"); throw new Exception("Bad pair accepted"); } catch (ArgumentException) { Assert(count == 0, "Request sent"); }
});
await Check("Refusals expose safe code without raw body secrets", async () =>
{
    using var client = new EzConnectClient(new FakeHandler(_ => Task.FromResult(Reply("{\"error_code\":\"device_mismatch\",\"access_token\":\"do-not-log-this\"}", HttpStatusCode.Unauthorized))));
    try { await client.BootstrapAsync("token"); throw new Exception("Refusal accepted"); } catch (EzConnectServiceException e) { Assert(e.Code == "device_mismatch" && !e.Message.Contains("do-not-log"), "Unsafe error"); }
});
await Check("Redirects are refused without following unknown host", async () =>
{
    using var client = new EzConnectClient(new FakeHandler(_ => Task.FromResult(Reply("", HttpStatusCode.Found))));
    try { await client.BootstrapAsync("token"); throw new Exception("Redirect accepted"); } catch (EzConnectServiceException e) { Assert(e.StatusCode == 302, "Wrong redirect failure"); }
});
await Check("Missing endpoint fails closed", () => Sync(() =>
{
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { session_id = "s", config = new { Interface = new { Address = "10.0.0.2/32" }, Peer = new { PublicKey = peerKey } } }));
    try { EzConnectClient.ParseProvisionedSession(json.RootElement, route, privateKey); throw new Exception("Missing endpoint invented"); } catch (FormatException) { }
}));
Console.WriteLine($"{passed} passed; {failed} failed. All HTTP responses mocked; no service requests or tunnel changes.");
return failed == 0 ? 0 : 1;

static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
}

sealed class StalledBody : MemoryStream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}
