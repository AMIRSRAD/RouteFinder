using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace RouteFinder.App;

internal static class UpdateCheck
{
    static string UserFeedPath => Path.Combine(ProfileStore.DirectoryPath, "update-feed.txt");
    static string LegacyFeedPath => Path.Combine(AppContext.BaseDirectory, "update-feed.txt");

    public static string ReadFeed()
    {
        if (File.Exists(UserFeedPath)) return File.ReadAllText(UserFeedPath).Trim();
        return File.Exists(LegacyFeedPath) ? File.ReadAllText(LegacyFeedPath).Trim() : "";
    }

    public static void SaveFeed(string endpoint)
    {
        endpoint = endpoint.Trim();
        if (endpoint.Length > 0) ValidateFeed(endpoint);
        Directory.CreateDirectory(ProfileStore.DirectoryPath);
        File.WriteAllText(UserFeedPath, endpoint);
    }

    static Uri ValidateFeed(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
            throw new FormatException("The update feed must be an HTTPS URL without credentials.");
        return uri;
    }

    internal sealed record Result(string Message, bool Available, Uri? ReleasePage, Package? Download);
    internal sealed record Package(Uri Uri, string Sha256);

    public static async Task<Result?> CheckAsync()
    {
        string endpoint = ReadFeed();
        if (endpoint.Length == 0) return null;
        var uri = ValidateFeed(endpoint);

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 65536
        };
        using var manifest = JsonDocument.Parse(await client.GetStringAsync(uri));
        var root = manifest.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.String ||
            product.GetString() != "RouteFinder" ||
            !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
            !Version.TryParse(version.GetString(), out var latest))
            throw new FormatException("The feed did not return a valid RouteFinder version manifest.");

        var installed = Assembly.GetExecutingAssembly().GetName().Version!;
        latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0), Math.Max(latest.Revision, 0));
        Uri? ReadSameOriginUrl(string key) =>
            root.TryGetProperty(key, out var page) && page.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(page.GetString(), UriKind.Absolute, out var candidate) &&
            candidate.Scheme == Uri.UriSchemeHttps && candidate.UserInfo.Length == 0 &&
            candidate.GetLeftPart(UriPartial.Authority).Equals(uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
                ? candidate : null;
        Package? download = null;
        if (ReadSameOriginUrl("download_url") is { } downloadUrl &&
            root.TryGetProperty("sha256", out var checksum) && checksum.ValueKind == JsonValueKind.String &&
            checksum.GetString() is { Length: 64 } hash && hash.All(Uri.IsHexDigit))
            download = new(downloadUrl, hash);
        bool available = latest > installed;
        return new(available ? $"RouteFinder {latest} is available. Installed version {installed}."
            : $"RouteFinder {installed} is up to date.", available, ReadSameOriginUrl("release_page"), download);
    }

    public static async Task DownloadAsync(Package package, string destination)
    {
        const long maxBytes = 512L * 1024 * 1024;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var response = await client.GetAsync(package.Uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes) throw new IOException("The update exceeds the download size limit.");

        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".routefinder-{Guid.NewGuid():N}.partial");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, deadline.Token)) != 0)
                {
                    total += read;
                    if (total > maxBytes) throw new IOException("The update exceeds the download size limit.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                }
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The downloaded update does not match the release checksum.");
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
