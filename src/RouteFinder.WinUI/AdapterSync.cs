using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;
using RouteFinder.Core;

namespace RouteFinder.WinUI;

/// <summary>
/// Keeps Windows adapter rules in step with the Apps and Network pages with no apply button: whenever the wanted rules
/// change, the running rules are replaced to match. Rules that cannot coexist with the current scope pause themselves.
/// </summary>
public sealed partial class MainWindow
{
    DirectAppRouting.Rule[] activeCombined = [];
    // True from pressing Connect until the tunnel is up or has failed.
    bool connectingTunnel;
    string lastAdapterSignature = "";
    bool adapterSyncRunning, adapterSyncAgain, verifying;
    string? lastAdapterError, failedAdapterSignature;
    DateTime adapterRetryAfter;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? adapterSyncTimer;

    // Entire PC and Selected apps only take over or block every app, so Direct apps and the default adapter stand
    // down while they are on. Tunnel pins still apply: WireSock only captures traffic on the tunnel's adapter.
    bool AdapterRulesPaused => Scope.SelectedIndex != 0;

    /// <summary>
    /// What Windows should enforce now. Selected apps: Direct apps, the default adapter, and pins that keep RouteFinder
    /// and Through VPN apps on the tunnel's adapter. Entire PC: while connecting or connected, every app is sent to the
    /// tunnel's adapter (WireSock only sees that adapter). Selected apps only: the Through VPN apps are pinned to it.
    /// </summary>
    (DirectAppRouting.Rule[] Rules, string? CatchAll) DesiredPlan()
    {
        switch (Scope.SelectedIndex)
        {
            case 1:
            {
                bool tunnelWanted = session.HasTunnel || connectingTunnel;
                string? tunnel = tunnelWanted && TunnelNeedsPinning(null) ? PinnedTunnelAdapter(null) : null;
                return (tunnel is null ? [] : VpnPins(null, []), tunnel);
            }
            case 2:
                return (VpnPins(null), null);
            default:
            {
                // The default adapter itself is set through the adapter metric; the catch-all rule is only for when
                // another VPN's split routes would outrank any metric.
                string? fallback = DesiredFallback();
                return (DesiredRules(fallback), DefaultNeedsCatchAll ? fallback : null);
            }
        }
    }

    // Disk checks happen on every refresh, so remember each answer for a few seconds; a slow drive then cannot stall the window.
    readonly Dictionary<string, (bool Exists, DateTime At)> existsCache = new(StringComparer.OrdinalIgnoreCase);

    bool ExistsCached(string path)
    {
        if (existsCache.TryGetValue(path, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(5)) return hit.Exists;
        bool exists = File.Exists(path);
        existsCache[path] = (exists, DateTime.UtcNow);
        return exists;
    }

    // The default adapter for apps without a rule. When the chosen one is offline, another working physical adapter
    // stands in, so those apps stay direct instead of falling through to Windows' route (which may be another VPN).
    string? DesiredFallback()
    {
        if (windowsFallbackDraft is not { } id || preferences.PortableEngine) return null;
        if (windowsAdapters.Any(adapter => adapter.HasGateway && adapter.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return id;
        return autoPhysicalUplink is { } standIn && windowsAdapters.Any(adapter => adapter.HasGateway &&
            adapter.Id.Equals(standIn, StringComparison.OrdinalIgnoreCase)) ? standIn : null;
    }

    DirectAppRouting.Rule[] DesiredAssignments() => windowsAppDraft is not { } draft ? [] :
        draft.Where(rule => checkedApps.Contains(rule.Key) && rule.Value.Length > 0 && !appVpnDraft.ContainsKey(rule.Key) &&
                ExistsCached(rule.Key) && windowsAdapters.Any(adapter => adapter.Available &&
                    adapter.Id.Equals(rule.Value, StringComparison.OrdinalIgnoreCase)))
            .Select(rule => new DirectAppRouting.Rule(rule.Key, rule.Value)).ToArray();

    // Everything Windows should enforce: pinned apps, plus RouteFinder and the VPN apps kept on the tunnel's adapter
    // so a default adapter cannot pull them away.
    DirectAppRouting.Rule[] DesiredRules(string? fallback)
    {
        var assignments = DesiredAssignments();
        var pins = VpnPins(fallback);
        return assignments.Concat(pins.Where(pin => !assignments.Any(rule =>
            rule.Executable.Equals(pin.Executable, StringComparison.OrdinalIgnoreCase)))).ToArray();
    }

    string AdapterSignature()
    {
        var (rules, catchAll) = DesiredPlan();
        return $"scope{Scope.SelectedIndex}|" + string.Join("|", rules.Select(rule => rule.Executable + ">" + rule.AdapterId)
            .Order(StringComparer.OrdinalIgnoreCase).Append("default:" + catchAll).Append("metric:" + (Scope.SelectedIndex == 0 || preferences.KeepWindowsDefault ? DesiredFallback() : null)));
    }

    // Called from Refresh: schedules a sync only when what is wanted has changed.
    void QueueAdapterSync()
    {
        if (preview || verifying || closing || windowsAppDraft is null) return;
        string signature = AdapterSignature();
        if (signature == lastAdapterSignature) return;
        if (signature == failedAdapterSignature && DateTime.UtcNow < adapterRetryAfter) return;
        lastAdapterSignature = signature;
        adapterSyncTimer ??= CreateAdapterSyncTimer();
        adapterSyncTimer.Stop();
        adapterSyncTimer.Start();
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer CreateAdapterSyncTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(600);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) => await SyncAdapterRules();
        return timer;
    }

    /// <summary>Makes the running Windows rules match the wanted ones. Safe to call at any time.</summary>
    internal async Task SyncAdapterRules()
    {
        if (preview || verifying || closing) return;
        adapterSyncTimer?.Stop();
        if (adapterSyncRunning) { adapterSyncAgain = true; return; }
        adapterSyncRunning = true;
        try
        {
            do
            {
                adapterSyncAgain = false;
                await ApplyAdapterRules();
            }
            while (adapterSyncAgain && !closing);
        }
        finally { adapterSyncRunning = false; }
        if (!closing) UpdateWindowsRoutingActions();
    }

    async Task ApplyAdapterRules()
    {
        await SyncDefaultAdapterMetric(Scope.SelectedIndex == 0 || preferences.KeepWindowsDefault ? DesiredFallback() : null);
        var (rules, fallback) = DesiredPlan();
        bool wanted = rules.Length > 0 || fallback is not null;
        if (!wanted && directRouting is null) return;
        if (wanted && directRouting is not null && SameRules(rules, activeCombined) && activeFallback == fallback) return;
        string[] kept = [];
        DiagnosticLog.Write("RULES", $"Applying adapter rules · wanted {wanted} · paused {AdapterRulesPaused} · default {AdapterName(fallback)} (chosen {AdapterName(windowsFallbackDraft)})" +
            string.Concat(rules.Select(rule => $"\n  {Path.GetFileName(rule.Executable)} -> {AdapterName(rule.AdapterId)}  ({rule.Executable})")));
        try
        {
            bool canStart = wanted && DirectAppRouting.IsSupported();
            if (canStart)
            {
                using var identity = WindowsIdentity.GetCurrent();
                canStart = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            if (directRouting is { } running)
            {
                // Swapping keeps Windows' route-policy setting on, so there is no gap and no netsh churn between rule sets.
                kept = canStart ? running.EnabledFamilies : [];
                await Task.Run(() => running.Release(restorePolicies: !canStart));
                directRouting = null;
                activeFallback = null;
                activeCombined = [];
                activeAssignments = [];
                activePins = [];
            }
            if (!canStart) return;
            directRouting = await Task.Run(() => DirectAppRouting.Start(rules, fallback, kept));
            kept = [];
            activeFallback = fallback;
            activeCombined = rules;
            activeAssignments = AdapterRulesPaused ? [] : DesiredAssignments();
            activePins = rules.Where(rule => !activeAssignments.Contains(rule)).ToArray();
            lastAdapterError = null;
            AddActivity($"Adapter rules applied: {rules.Length} app(s){(fallback is null ? "" : " + default adapter")}.");
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("RULES", "Adapter rules failed", error);
            // The kept setting has no owner now; turn it off so Windows is not left with it on.
            if (kept.Length > 0)
            {
                try { await Task.Run(() => DirectAppRouting.RestorePolicies(kept)); }
                catch (Exception restoreError) { AddActivity($"Could not restore Windows route policies. {restoreError.Message}"); }
            }
            // Retry after a pause rather than on every click; say it once.
            failedAdapterSignature = AdapterSignature();
            adapterRetryAfter = DateTime.UtcNow.AddSeconds(30);
            lastAdapterSignature = "";
            if (error.Message != lastAdapterError)
            {
                lastAdapterError = error.Message;
                Report($"Adapter rules could not be applied. {error.Message}");
            }
        }
    }

    // Short status chip on the Apps page; hidden when there are no adapter rules.
    void UpdateWindowsRoutingActions()
    {
        if (windowsAppDraft is not { } draft) return;
        int pinned = draft.Count(rule => checkedApps.Contains(rule.Key) && rule.Value.Length > 0 && !appVpnDraft.ContainsKey(rule.Key));
        bool hasRules = pinned > 0 || windowsFallbackDraft is not null;
        AdapterRulesBar.Visibility = hasRules ? Visibility.Visible : Visibility.Collapsed;
        bool on = directRouting is not null && !AdapterRulesPaused;
        WindowsRoutingStatus.Text = UiLanguage.Text(AdapterRulesPaused ? "Adapter rules paused"
            : on ? $"Adapter rules on · {pinned} app{(pinned == 1 ? "" : "s")}" : "Adapter rules off");
        WindowsFallbackAdapter.IsEnabled = !busy && !session.NeedsCleanup;
        RefreshWindowsAdaptersButton.IsEnabled = !busy;
        KeepWindowsDefaultToggle.IsEnabled = !busy && !session.NeedsCleanup && windowsFallbackDraft is not null;
        string reason = AdapterRulesPaused ? "Direct apps pause while Entire PC or Selected apps only is on; VPN traffic is still kept on the tunnel's adapter."
            : !windowsRoutingSupported ? "This Windows build does not support adapter rules."
            : pinned > 0 && DesiredAssignments().Length < pinned ? "An app's file or adapter is unavailable."
            : DesiredFallback() is { } standIn && standIn != windowsFallbackDraft
                ? $"Default adapter is offline; using {UplinkLabel(standIn)} until it is back." : "";
        ToolTipService.SetToolTip(AdapterRulesBar, reason.Length == 0 ? null : UiLanguage.Text(reason));
    }
}
