using System.Net.NetworkInformation;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

/// <summary>
/// The default adapter, done the Windows way: instead of a filtering rule on every app (which also catches an app's
/// local traffic and left Riot Client with a blank window), RouteFinder lowers the chosen adapter's interface metric
/// so Windows itself prefers it, for apps and services alike. The original metric is restored when the default is
/// cleared, when RouteFinder exits, and at the next start after a crash. Only when another VPN holds the route with
/// split routes, which metrics cannot outrank, is the filtering catch-all still used.
/// </summary>
public sealed partial class MainWindow
{
    // Adapter whose metric RouteFinder lowered, or null.
    string? metricAdapterId;

    static string MetricRestorePath => Path.Combine(Path.GetDirectoryName(LogFolder)!, "metric-restore.txt");
    static string MetricAdapterPath => Path.Combine(Path.GetDirectoryName(LogFolder)!, "metric-adapter.txt");

    /// <summary>At startup: a kept default stays; otherwise a metric a crash left lowered is put back.</summary>
    void RecoverDefaultAdapterMetric()
    {
        if (preview || verifying || !File.Exists(MetricRestorePath)) return;
        if (preferences.KeepWindowsDefault && File.Exists(MetricAdapterPath))
        {
            metricAdapterId = File.ReadAllText(MetricAdapterPath).Trim();
            Trace("DEFAULT", $"Kept default adapter {metricAdapterId} from an earlier session.");
        }
        else RestoreDefaultAdapterMetric();
    }

    /// <summary>On exit: put Windows' own choice back, unless the user keeps this adapter as Windows' default.</summary>
    void RestoreDefaultAdapterMetricOnExit()
    {
        if (preferences.KeepWindowsDefault && metricAdapterId is not null)
        {
            DiagnosticLog.Write("DEFAULT", "Keeping the default adapter as Windows' preferred adapter after exit.");
            return;
        }
        RestoreDefaultAdapterMetric();
    }

    // Split routes from another VPN (0.0.0.0/1 and 128.0.0.0/1) beat any metric, so only then is a catch-all rule needed.
    bool DefaultNeedsCatchAll => foreignVpnName is not null;

    async Task SyncDefaultAdapterMetric(string? fallback)
    {
        if (preview || verifying) return;
        string? wanted = fallback is not null && !DefaultNeedsCatchAll ? fallback : null;
        if (string.Equals(wanted, metricAdapterId, StringComparison.OrdinalIgnoreCase)) return;
        if (metricAdapterId is not null) await Task.Run(RestoreDefaultAdapterMetric);
        if (wanted is null) return;
        if (wanted.Equals(windowsDefaultAdapterId, StringComparison.OrdinalIgnoreCase))
        {
            Trace("DEFAULT", $"Default adapter {AdapterName(wanted)} is already Windows' own choice; no rule or metric change needed.");
            return;
        }
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item => item.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        int? index = adapter?.Supports(NetworkInterfaceComponent.IPv4) == true ? adapter.GetIPProperties().GetIPv4Properties()?.Index : null;
        if (adapter is null || index is null) { Trace("DEFAULT", $"Default adapter {AdapterName(wanted)} has no IPv4 interface; metric unchanged."); return; }
        string result = await Task.Run(() => RunPowerShell(
            $"$out = foreach ($f in 'IPv4','IPv6') {{ $i = Get-NetIPInterface -InterfaceIndex {index} -AddressFamily $f -ErrorAction SilentlyContinue; " +
            "if ($i) { \"{0} $f {1} {2}\" -f " + index + ", $i.AutomaticMetric, $i.InterfaceMetric } }; " +
            $"Set-Content -LiteralPath '{MetricRestorePath}' -Value $out -Encoding ASCII; " +
            $"foreach ($f in 'IPv4','IPv6') {{ Set-NetIPInterface -InterfaceIndex {index} -AddressFamily $f -AutomaticMetric Disabled -InterfaceMetric 1 -ErrorAction SilentlyContinue }}; " +
            "Get-Content -LiteralPath '" + MetricRestorePath + "' | ForEach-Object { \"was: $_\" }; 'ok'"));
        metricAdapterId = wanted;
        try { File.WriteAllText(MetricAdapterPath, wanted); } catch (IOException) { }
        Trace("DEFAULT", $"Made {adapter.Name} Windows' preferred adapter (interface metric 1) so every app and service uses it.\n{result}");
        AddActivity($"{adapter.Name} is now the default adapter.");
    }

    /// <summary>Puts back the metric saved before RouteFinder changed it. Safe to call when nothing was changed.</summary>
    void RestoreDefaultAdapterMetric()
    {
        if (!File.Exists(MetricRestorePath)) { metricAdapterId = null; return; }
        string result = RunPowerShell(
            $"Get-Content -LiteralPath '{MetricRestorePath}' | ForEach-Object {{ $p = $_ -split ' '; " +
            "if ($p.Count -ge 4) { if ($p[2] -eq 'Enabled') { Set-NetIPInterface -InterfaceIndex $p[0] -AddressFamily $p[1] -AutomaticMetric Enabled -ErrorAction SilentlyContinue } " +
            "else { Set-NetIPInterface -InterfaceIndex $p[0] -AddressFamily $p[1] -AutomaticMetric Disabled -InterfaceMetric $p[3] -ErrorAction SilentlyContinue }; \"restored $_\" } }; " +
            $"Remove-Item -LiteralPath '{MetricRestorePath}' -Force; Remove-Item -LiteralPath '{MetricAdapterPath}' -Force -ErrorAction SilentlyContinue; 'ok'");
        metricAdapterId = null;
        DiagnosticLog.Write("DEFAULT", $"Restored the original adapter metric.\n{result}");
    }
}
