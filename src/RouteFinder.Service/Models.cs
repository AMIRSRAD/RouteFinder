using System.Text.Json;
using RouteFinder.Core;

namespace RouteFinder.Service;

// Values are caller-supplied. This client does not invent or bypass the service's device identity.
public sealed record DeviceDescriptor(string DeviceHash, string OsName, string OsVersion, string Hostname,
    string? CpuModel = null, string? GpuModel = "", string? MotherboardModel = null, string? SystemUuid = null,
    string AppVersion = "6.8.1", string WiresockVersion = "3.4.8");

public sealed record AuthTokens(string AccessToken, string RefreshToken, int? ExpiresIn, int? RefreshExpiresIn)
{
    public override string ToString() => "AuthTokens([redacted])";
}
public sealed record ServiceRoute(string Name, string RegionCode, string CityName, bool HasIpv6)
{
    public bool IsDownload => RegionCode.Equals("DL", StringComparison.OrdinalIgnoreCase);
    public bool OverIpv6 { get; init; }
    public override string ToString() => $"{Name} • {RegionCode}";
}
public sealed record GameServer(string Country, string City, System.Net.IPAddress Address)
{
    public override string ToString() => string.Join(" • ", new[] { Country, City, Address.ToString() }.Where(value => value.Length > 0));
}

public sealed record CatalogGame(string Name, string[] ExecutableNames, string[] GlobalDisallowedIps, JsonElement Raw)
{
    public bool IsApplication { get; init; }
    public long? CatalogId { get; init; }
    public IReadOnlyList<GameServer> Servers { get; init; } = [];
    public string RecommendedRegion { get; init; } = "";
    public string[] LinkedAppNames { get; init; } = [];
    public string[] IncompatibleAppNames { get; init; } = [];
    public string DisplayName => $"{Name} ({(IsApplication ? "app" : "game")})";
}
public sealed record BootstrapData(IReadOnlyList<ServiceRoute> Routes, IReadOnlyList<CatalogGame> Games, string News, JsonElement Raw);
public sealed record ProvisionedSession(string SessionId, string Configuration, TunnelProfile Profile)
{
    public override string ToString() => "ProvisionedSession([configuration redacted])";
}
public sealed class PendingSessionCleanupException(string sessionId, Exception cause)
    : Exception("EZConnect returned an unusable profile and did not confirm release. Use Retry cleanup before connecting again.", cause)
{
    public string SessionId { get; } = sessionId;
}

public sealed class EzConnectServiceException(string message, int? statusCode = null, string? code = null, TimeSpan? retryAfter = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Verified hash composition from the original native fingerprint function; hardware normalization is separate.</summary>
public sealed record DeviceFingerprint(string CpuModel, string MotherboardModel, string RamModel, string SystemUuid)
{
    public string HashInput => string.Join("|", new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["cpu_model"] = CpuModel, ["motherboard_model"] = MotherboardModel, ["ram_model"] = RamModel, ["system_uuid"] = SystemUuid
    }.Select(x => x.Key + ":" + x.Value));
    public string DeviceHash => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(HashInput))).ToLowerInvariant();
}
