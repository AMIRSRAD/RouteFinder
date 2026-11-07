using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace RouteFinder.Core;

/// <summary>One WireGuard peer. Values originate in a user-supplied configuration.</summary>
public sealed class TunnelProfile
{
    public string Name { get; set; } = "Imported profile";
    public Dictionary<string, string> Interface { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Peer { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string GetInterface(string key) => Interface.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? "";
    public string GetPeer(string key) => Peer.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? "";
    public string Endpoint => GetPeer("Endpoint");
    public static readonly string[] AmneziaKeys = ["Jc", "Jmin", "Jmax", "Jd", "Id", "Ip", "Ib", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4"];
    internal static readonly HashSet<string> InterfaceKeys = new(new[] { "PrivateKey", "Address", "DNS", "MTU", "ListenPort", "AllowedApps", "DisallowedApps", "DisallowedIPs", "IgnoredIPs", "Socks5Proxy", "Socks5ProxyUsername", "Socks5ProxyPassword", "Socks5ProxyAllTraffic" }.Concat(AmneziaKeys), StringComparer.OrdinalIgnoreCase);
    internal static readonly HashSet<string> PeerKeys = new(["PublicKey", "PresharedKey", "AllowedIPs", "Endpoint", "PersistentKeepalive"], StringComparer.OrdinalIgnoreCase);

    public static TunnelProfile Parse(string text, string? name = null)
    {
        var result = new TunnelProfile { Name = string.IsNullOrWhiteSpace(name) ? "Imported profile" : name };
        Dictionary<string, string>? section = null;
        bool seenInterface = false, seenPeer = false;
        int lineNumber = 0;
        foreach (var original in text.Split('\n'))
        {
            lineNumber++;
            var line = original.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('['))
            {
                if (line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase) && !seenInterface) { section = result.Interface; seenInterface = true; }
                else if (line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase) && !seenPeer) { section = result.Peer; seenPeer = true; }
                else throw new FormatException($"Unsupported or repeated section at line {lineNumber}. Exactly one Interface and one Peer are supported.");
                continue;
            }
            int equals = line.IndexOf('=');
            if (section is null || equals < 1) throw new FormatException($"Invalid configuration line {lineNumber}.");
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (key.Equals("PreUp", StringComparison.OrdinalIgnoreCase) || key.Equals("PostUp", StringComparison.OrdinalIgnoreCase) || key.Equals("PreDown", StringComparison.OrdinalIgnoreCase) || key.Equals("PostDown", StringComparison.OrdinalIgnoreCase) || key.Equals("ScriptTimeout", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Configurations containing scripts are not supported. Remove the script directives before import.");
            var accepted = ReferenceEquals(section, result.Interface) ? InterfaceKeys : PeerKeys;
            if (!accepted.Contains(key)) throw new FormatException($"Unsupported option '{key}' at line {lineNumber}; it was not silently discarded.");
            if (!section.TryAdd(key, value)) throw new FormatException($"Repeated option '{key}' at line {lineNumber}.");
        }
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (Interface.Any(x => !InterfaceKeys.Contains(x.Key)) || Peer.Any(x => !PeerKeys.Contains(x.Key))) throw new FormatException("Profile contains unsupported options.");
        ValidateKey(GetInterface("PrivateKey"), "PrivateKey", false);
        ValidateKey(GetPeer("PublicKey"), "PublicKey", false);
        ValidateKey(GetPeer("PresharedKey"), "PresharedKey", true);
        ValidateCidrs(GetInterface("Address"), "Address");
        ValidateCidrs(GetPeer("AllowedIPs"), "AllowedIPs");
        if (GetInterface("DNS") is { Length: > 0 } dns && dns.Split(',').Any(x => !IPAddress.TryParse(x.Trim(), out _))) throw new FormatException("DNS must contain IP addresses separated by commas.");
        SplitEndpoint(Endpoint);
        ValidateNumber(GetInterface("MTU"), "MTU", 576, 65535);
        ValidateNumber(GetInterface("ListenPort"), "ListenPort", 0, 65535);
        ValidateNumber(GetPeer("PersistentKeepalive"), "PersistentKeepalive", 0, 65535);
        if (GetInterface("IgnoredIPs") is { Length: > 0 } ignored) ValidateCidrs(ignored, "IgnoredIPs");
        if (GetInterface("DisallowedIPs") is { Length: > 0 } disallowed) ValidateCidrs(disallowed, "DisallowedIPs");
        foreach (var value in Interface.Values.Concat(Peer.Values))
            if (value.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new FormatException("Profile values cannot contain control characters.");
        if (GetInterface("AllowedApps").Length > 0 && GetInterface("DisallowedApps").Length > 0) throw new FormatException("AllowedApps and DisallowedApps cannot both be set.");
    }

    public static (string Host, int Port) SplitEndpoint(string value)
    {
        var match = Regex.Match(value, @"^(?:\[(?<host>[^\]]+)\]|(?<host>[^:\s]+)):(?<port>\d+)$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["port"].Value, out int port) || port < 1 || port > 65535) throw new FormatException("Endpoint must be host:port or [IPv6]:port.");
        string host = match.Groups["host"].Value;
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new FormatException("Endpoint host is invalid.");
        return (host, port);
    }

    private static void ValidateKey(string value, string label, bool optional)
    {
        if (optional && value.Length == 0) return;
        try { if (Convert.FromBase64String(value).Length == 32) return; } catch (FormatException) { }
        throw new FormatException($"{label} must be a base64-encoded 32-byte key.");
    }
    private static void ValidateNumber(string value, string label, int min, int max)
    {
        if (value.Length > 0 && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min || n > max)) throw new FormatException($"{label} must be between {min} and {max}.");
    }
    private static void ValidateCidrs(string value, string label)
    {
        if (value.Length == 0) throw new FormatException($"{label} is required.");
        foreach (var item in value.Split(','))
        {
            var parts = item.Trim().Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)) throw new FormatException($"{label} contains an invalid CIDR range.");
        }
    }
}
