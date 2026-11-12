using Microsoft.Win32;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

/// <summary>
/// Optional (off by default): pause F-Secure while RouteFinder runs. Its DNS filtering takes over every lookup on
/// port 53 and its VPN SDK can hold the default route, both of which break per-app routing. Only services that were
/// running are stopped, and exactly those are started again when RouteFinder exits or the option is turned off,
/// and at the next start after a crash.
/// </summary>
public sealed partial class MainWindow
{
    // Stopped in this order, started in reverse.
    static readonly string[] FSecureServices = ["fsvpnsvc", "fsvpnextsvc", "fsnethoster", "fshoster"];

    static string PausedServicesPath => Path.Combine(Path.GetDirectoryName(LogFolder)!, "paused-services.txt");

    static string[] InstalledFSecureServices() => FSecureServices.Where(name =>
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return key is not null;
    }).ToArray();

    static bool ServiceRunning(string name) => RunTool("sc.exe", "query", name).Contains("RUNNING", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops the running F-Secure services and remembers which ones, so exactly those come back later.</summary>
    async Task PauseFSecure()
    {
        if (preview || verifying) return;
        var installed = InstalledFSecureServices();
        if (installed.Length == 0) { Trace("SECURITY", "F-Secure is not installed; nothing to pause."); return; }
        var (stopped, failed) = await Task.Run(() =>
        {
            var running = installed.Where(ServiceRunning).ToArray();
            var already = File.Exists(PausedServicesPath) ? File.ReadAllLines(PausedServicesPath) : [];
            var paused = new List<string>(already);
            var problems = new List<string>();
            foreach (string name in running)
            {
                string result = RunTool("sc.exe", "stop", name);
                for (int wait = 0; wait < 20 && ServiceRunning(name); wait++) Thread.Sleep(250);
                if (ServiceRunning(name)) problems.Add($"{name}: {result}");
                else if (!paused.Contains(name)) paused.Add(name);
            }
            File.WriteAllLines(PausedServicesPath, paused);
            return (paused.ToArray(), problems.ToArray());
        });
        Trace("SECURITY", $"Paused F-Secure services: {(stopped.Length == 0 ? "none were running" : string.Join(", ", stopped))}" +
            (failed.Length > 0 ? $"\nCould not stop (tamper protection may block this):\n{string.Join("\n", failed)}" : ""));
        if (failed.Length > 0) AddActivity("F-Secure would not stop. Turn off its tamper protection or pause it from its own window.");
        else if (stopped.Length > 0) AddActivity("F-Secure is paused while RouteFinder runs.");
    }

    /// <summary>Starts again the services RouteFinder stopped. Safe to call when nothing was paused.</summary>
    void ResumeFSecure()
    {
        if (preview || verifying || !File.Exists(PausedServicesPath)) return;
        var paused = File.ReadAllLines(PausedServicesPath).Where(name => FSecureServices.Contains(name)).ToArray();
        var results = paused.Reverse().Select(name => $"{name}: {RunTool("sc.exe", "start", name)}").ToArray();
        try { File.Delete(PausedServicesPath); } catch (IOException) { }
        DiagnosticLog.Write("SECURITY", $"Started F-Secure services again:\n{string.Join("\n", results)}");
    }

    // At startup: honour the setting, and undo a pause a crash left behind when the setting is off.
    void ApplySecuritySoftwareSetting()
    {
        if (preview || verifying) return;
        if (preferences.PauseFSecure) _ = PauseFSecure();
        else if (File.Exists(PausedServicesPath)) _ = Task.Run(ResumeFSecure);
    }
}
