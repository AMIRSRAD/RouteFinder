using System.Text;
using System.Text.Json;
using System.Security.Principal;
using RouteFinder.Core;
using RouteFinder.Service;

if (args.Length == 2 && args[0] == "--send-credentials")
{
    await CredentialPipe.SendAsync(args[1], ReadCredentials);
    return 0;
}

if (args.Length == 4 && args[0] == "--verify-firefox-via-pipe")
    return await FirefoxCheck.RunAsync(args[1], args[2], args[3]);

if (args.Length is 3 or 4 && args[0] == "--verify-desktop-via-pipe")
{
    return await DesktopCheck.RunAsync(args[1], args[2], args.ElementAtOrDefault(3) ?? "DE:1");
}

if (args.Length == 1 && args[0] == "--help")
{
    Console.WriteLine("Audit-EzRoutes.ps1 lists routes. Use -ProbeCount N or -ProbeAll to request temporary WireGuard sessions.");
    return 0;
}

bool resume = args.Length == 3 && args[2] == "--resume";
bool portable = args.Length == 3 && args[2] == "--verify-portable";
bool requireExpiredAccess = args.Length == 3 && args[2] == "--require-expired-access";
bool pipeLogin = args.Length >= 4 && args[2] == "--verify-via-pipe";
bool verifyTunnel = pipeLogin || portable || args.Length == 3 && args[2] == "--verify-tunnel";
if ((!resume && !verifyTunnel && !requireExpiredAccess && args.Length != 2) || !int.TryParse(args[0], out var count) || count < -1 || (requireExpiredAccess && count != 1) || (!pipeLogin && Console.IsInputRedirected))
{
    Console.Error.WriteLine("Run Audit-EzRoutes.ps1 from an interactive terminal.");
    return 2;
}

if (verifyTunnel)
{
    using var identity = WindowsIdentity.GetCurrent();
    if ((count != 1 && (!pipeLogin || count < 1 || count > 3 || args.Length != 4 + count)) ||
        (!portable && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)))
    {
        Console.Error.WriteLine("Run the single-route verification in an administrator PowerShell window.");
        return 2;
    }
    using var sdk = new WireSockTunnel(portable: portable);
}

var output = Path.GetFullPath(args[1]);
var report = new AuditReport();
var ownsReport = false;
using var client = new EzConnectClient();
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    if (resume)
    {
        report = JsonSerializer.Deserialize<AuditReport>(File.ReadAllText(output))
            ?? throw new FormatException("The audit report is empty.");
        if (report.RetryNotBefore is DateTimeOffset retryAt && retryAt > DateTimeOffset.UtcNow)
        {
            Console.Error.WriteLine($"EZConnect cooldown ends at {retryAt.ToLocalTime():HH:mm:ss}.");
            return 1;
        }
    }
    else
    {
        // Reserve the report before making any requests. Existing reports stay intact.
        using (File.Open(output, FileMode.CreateNew)) { }
    }
    ownsReport = true;
    var device = DeviceIdentity.ReadCurrent();
    var (email, password) = pipeLogin ? await CredentialPipe.ReceiveAsync(args[3], cancellation.Token) : ReadCredentials();
    AuthTokens auth;
    try { auth = await client.LoginAsync(email, password, device, cancellation.Token); }
    finally { password = ""; }

    var catalog = await client.BootstrapAsync(auth.AccessToken, cancellation.Token);
    report.Entitlement = ReadEntitlement(catalog.Raw);
    report.Routes = catalog.Routes.DistinctBy(route => (route.Name, route.RegionCode)).ToList();
    report.Failure = null;
    report.RetryNotBefore = null;
    Save();
    if (requireExpiredAccess && (report.Entitlement?.RemainingSeconds is not double remainingSeconds || remainingSeconds > 0))
    {
        RecordFailure("EZConnect did not report expired access. No provisioning request was made.");
        return 1;
    }
    foreach (var route in report.Routes)
        Console.WriteLine($"{route.RegionCode,-8} {route.Name} ({route.CityName})");

    var completed = report.Attempts.Where(attempt => attempt.Status == "Provisioned and released")
        .Select(attempt => (attempt.Name, attempt.RegionCode)).ToHashSet();
    var remaining = report.Routes.Where(route => !completed.Contains((route.Name, route.RegionCode)));
    var selected = count < 0 ? remaining : remaining.Take(count);
    if (pipeLogin && args.Length > 4)
        selected = args.Skip(4).Select(selector => report.Routes.Single(route =>
            $"{route.RegionCode}:{route.Name}".Equals(selector, StringComparison.OrdinalIgnoreCase))).ToArray();
    foreach (var route in selected)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        var attempt = new RouteResult(route.Name, route.RegionCode);
        report.Attempts.Add(attempt);
        Console.WriteLine($"Checking {route.Name} ({route.RegionCode})…");
        try
        {
            var keys = WireGuardKeys.Generate();
            // Let an in-flight allocation finish so Ctrl+C cannot hide its session ID.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var session = await client.ConnectAsync(route, keys.PublicKey, keys.PrivateKey, auth.AccessToken, deadline.Token);
            attempt.Endpoint = session.Profile.Endpoint;
            attempt.AllowedIps = session.Profile.GetPeer("AllowedIPs");
            attempt.HasProxy = session.Profile.GetInterface("Socks5Proxy").Length > 0;
            attempt.HasObfuscation = session.Profile.GetInterface("Jc").Length > 0;
            attempt.Status = "Provisioned";
            try
            {
                if (verifyTunnel)
                {
                    Console.WriteLine("Checking WireSock while the EZConnect session is active…");
                    attempt.WhileActive = await TunnelCheck.RunAsync(session.Profile, cancellation.Token, portable);
                }
            }
            finally
            {
                try
                {
                    using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    await client.DisconnectAsync(session.SessionId, auth.AccessToken, cleanupDeadline.Token);
                    attempt.Status = "Provisioned and released";
                }
                catch
                {
                    attempt.Status = "Release unconfirmed. Check the session in EZConnect.";
                    throw;
                }
            }
            if (attempt.WhileActive is { HandshakeAgeMilliseconds: >= 0 } or { ReceivedBytes: > 0 })
            {
                Console.WriteLine("Checking a fresh WireSock tunnel with the same key after session release…");
                await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
                attempt.AfterRelease = await TunnelCheck.RunAsync(session.Profile, cancellation.Token, portable);
                if (pipeLogin)
                {
                    Console.WriteLine("Checking the same relay with a fresh, unregistered WireGuard key…");
                    var unregistered = new TunnelProfile
                    {
                        Name = "RouteFinder unregistered peer check",
                        Interface = new(session.Profile.Interface, StringComparer.OrdinalIgnoreCase),
                        Peer = new(session.Profile.Peer, StringComparer.OrdinalIgnoreCase)
                    };
                    unregistered.Interface["PrivateKey"] = WireGuardKeys.Generate().PrivateKey;
                    attempt.UnregisteredKey = await TunnelCheck.RunAsync(unregistered, cancellation.Token, portable);
                }
            }
        }
        catch (EzConnectServiceException error)
        {
            if (attempt.Status == "Pending") attempt.Status = error.Message;
            if (error.StatusCode == 429)
                report.RetryNotBefore = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(60, error.RetryAfter?.TotalSeconds ?? 0));
            throw;
        }
        catch
        {
            if (attempt.Status == "Pending") attempt.Status = "Provisioning unconfirmed.";
            throw;
        }
        finally { Save(); }
        await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
    }
    Console.WriteLine($"Report saved to {output}");
    return 0;
}
catch (OperationCanceledException)
{
    RecordFailure("Audit canceled or timed out. Check EZConnect if the last session could not be confirmed.");
    return 1;
}
catch (EzConnectServiceException error)
{
    RecordFailure(error.Message);
    return 1;
}
catch (Exception error)
{
    var reason = error is HttpRequestException request
        ? $"Could not reach EZConnect ({request.HttpRequestError})."
        : $"Audit stopped ({error.GetType().Name}).";
    RecordFailure(reason);
    return 1;
}
finally { Console.CancelKeyPress -= cancel; }

void Save() => File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

void RecordFailure(string message)
{
    Console.Error.WriteLine(message);
    if (!ownsReport) return;
    report.Failure = message;
    try { Save(); Console.Error.WriteLine($"Report: {output}"); }
    catch (IOException) { Console.Error.WriteLine("The report could not be updated."); }
    catch (UnauthorizedAccessException) { Console.Error.WriteLine("The report could not be updated."); }
}

static (string Email, string Password) ReadCredentials()
{
    Console.Write("EZConnect email: ");
    var email = Console.ReadLine() ?? "";
    Console.Write("EZConnect password: ");
    var input = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (input.Length > 0) input.Length--;
        }
        else if (!char.IsControl(key.KeyChar)) input.Append(key.KeyChar);
    }
    Console.WriteLine();
    var result = input.ToString();
    for (var i = 0; i < input.Length; i++) input[i] = '\0';
    return (email, result);
}

static EntitlementSummary? ReadEntitlement(JsonElement bootstrap)
{
    if (!bootstrap.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object)
        return null;
    double? seconds = null;
    if (account.TryGetProperty("remaining_seconds", out var remaining))
    {
        if (remaining.ValueKind == JsonValueKind.Number && remaining.TryGetDouble(out var number) && double.IsFinite(number))
            seconds = number;
        else if (remaining.ValueKind == JsonValueKind.String &&
            double.TryParse(remaining.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) &&
            double.IsFinite(number))
            seconds = number;
    }
    bool? unlimited = account.TryGetProperty("is_unlimited", out var cap) && cap.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? cap.GetBoolean() : null;
    string? status = account.TryGetProperty("user_status", out var statusField) && statusField.ValueKind == JsonValueKind.String
        ? statusField.GetString() : null;
    if (status?.Length > 32 || status?.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != '-') == true)
        status = null;
    return new(seconds, unlimited, status);
}

sealed record EntitlementSummary(double? RemainingSeconds, bool? UnlimitedTraffic, string? AccountStatus);

sealed class AuditReport
{
    public EntitlementSummary? Entitlement { get; set; }
    public string? Failure { get; set; }
    public DateTimeOffset? RetryNotBefore { get; set; }
    public List<ServiceRoute> Routes { get; set; } = [];
    public List<RouteResult> Attempts { get; set; } = [];
}

sealed record RouteResult(string Name, string RegionCode)
{
    public string Status { get; set; } = "Pending";
    public string? Endpoint { get; set; }
    public string? AllowedIps { get; set; }
    public bool? HasProxy { get; set; }
    public bool? HasObfuscation { get; set; }
    public TunnelCheckResult? WhileActive { get; set; }
    public TunnelCheckResult? AfterRelease { get; set; }
    public TunnelCheckResult? UnregisteredKey { get; set; }
}
