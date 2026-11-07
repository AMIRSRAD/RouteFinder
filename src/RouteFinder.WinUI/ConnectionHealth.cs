using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    (ulong Received, ulong Sent)? healthTraffic;
    long? sendsWithoutReplySince;
    bool reconnectFailed;

    void UpdateTrafficHealth(ulong received, ulong sent, bool peerResponded)
    {
        if (!peerResponded || healthTraffic is not { } previous ||
            received != previous.Received || sent < previous.Sent)
        {
            sendsWithoutReplySince = null;
        }
        else if (sent > previous.Sent)
        {
            sendsWithoutReplySince ??= Stopwatch.GetTimestamp();
        }
        healthTraffic = (received, sent);
        if (sendsWithoutReplySince is long start && Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(20))
            SetHealthAlert("Traffic was sent, but no incoming tunnel bytes were reported for 20 seconds.");
        else SetHealthAlert("");
    }

    void ResetTrafficHealth()
    {
        healthTraffic = null;
        sendsWithoutReplySince = null;
    }

    void SetHealthAlert(string message)
    {
        if (HealthMessage.Text == message) return;
        if (message.Length > 0) AddActivity(message);
        HealthMessage.Text = message;
        HealthAlert.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(HealthAlert, $"{message} Open Activity");
    }
}
