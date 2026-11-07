using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RouteFinder.Service;

public static class DeviceIdentity
{
    public static DeviceDescriptor ReadCurrent()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var resource = typeof(DeviceIdentity).Assembly.GetManifestResourceStream("RouteFinder.HardwareQuery")
            ?? throw new InvalidOperationException("The device query is missing.");
        using var reader = new StreamReader(resource);
        var command = Convert.ToBase64String(Encoding.Unicode.GetBytes(reader.ReadToEnd()));
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", command }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the device query.");
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(20_000))
        {
            process.Kill();
            throw new TimeoutException("The device query timed out.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows could not read the device information.");
        using var document = JsonDocument.Parse(output.GetAwaiter().GetResult());
        string Read(string name) => document.RootElement.TryGetProperty(name, out var value) ? value.GetString()?.Trim() ?? "" : "";
        var cpu = Read("cpu_name");
        cpu = cpu.Length > 0 ? Regex.Replace(Regex.Replace(cpu, "@.*?GHz", ""), @"\s+", " ").Trim()
            : Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")?.Trim() ?? "Unknown";
        var manufacturer = Read("ram_manufacturer");
        var part = Read("ram_part_number");
        var ram = manufacturer.Length > 0 ? JoinManufacturer(manufacturer, part) :
            long.TryParse(Read("total_physical_bytes"), out var bytes) && bytes > 0 ? $"RAM_{Math.Round(bytes / 1073741824d):0}GB" : "Unknown";
        var board = JoinManufacturer(Read("board_manufacturer"), Read("board_product"));
        var uuid = Read("system_uuid");
        if (uuid.Length == 0 || uuid == "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")
        {
            uuid = (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string)?.Trim() ?? "";
            if (uuid.Length == 0)
            {
                if (cpu == "Unknown" && ram == "Unknown" && board == "Unknown") throw new InvalidOperationException("Windows returned no usable device information.");
                var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{cpu}_{ram}_{board}"))).ToLowerInvariant();
                uuid = $"{digest[..8]}-{digest[8..12]}-{digest[12..16]}-{digest[16..20]}-{digest[20..]}";
            }
        }
        var fingerprint = new DeviceFingerprint(cpu, board, ram, uuid);
        return new DeviceDescriptor(fingerprint.DeviceHash, "Windows", Environment.OSVersion.Version.ToString(3),
            Regex.Replace(Dns.GetHostName(), "[^A-Za-z0-9._-]", ""), cpu, "", board, uuid);
    }

    static string JoinManufacturer(string manufacturer, string part) =>
        manufacturer.Length == 0 ? "Unknown" : part.Length == 0 ? manufacturer : manufacturer + "_" + part;
}
