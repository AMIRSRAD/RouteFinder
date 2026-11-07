using System.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    void ToggleRoutingVerification(object sender, RoutedEventArgs args)
    {
        bool open = VerificationScroller.Visibility != Visibility.Visible;
        if (open && VerificationScroller.Content is null) VerificationScroller.Content = CreateVerificationPanel();
        VerificationScroller.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        VerifyRoutingButton.Content = open ? "Show apps" : "Verify routing";
        Refresh();
    }

    StackPanel CreateVerificationPanel()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Verify selected-app routing", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Select one of two different browsers. Check both public IPs before and after connecting, using fresh browser connections and the same IP family. In Selected apps only mode, enter blocked for the unselected browser if it cannot reach the IP-check page.",
            TextWrapping = TextWrapping.Wrap
        });
        var selectedBefore = new TextBox { Header = "Selected browser before", PlaceholderText = "Public IP" };
        var controlBefore = new TextBox { Header = "Unselected browser before", PlaceholderText = "Public IP" };
        var selectedAfter = new TextBox { Header = "Selected browser connected", PlaceholderText = "Public IP" };
        var controlAfter = new TextBox { Header = "Unselected browser connected", PlaceholderText = "Public IP or blocked" };
        AddPair(selectedBefore, controlBefore);
        AddPair(selectedAfter, controlAfter);
        var evaluate = new Button { Content = "Compare results" };
        var reset = new Button { Content = "Clear readings" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(evaluate);
        actions.Children.Add(reset);
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(actions);
        panel.Children.Add(result);
        evaluate.Click += (_, _) =>
        {
            if (!BlocksOtherApps)
                result.Text = ComparePublicIps(selectedBefore.Text, controlBefore.Text, selectedAfter.Text, controlAfter.Text);
            else if (IPAddress.TryParse(controlAfter.Text.Trim(), out _))
                result.Text = "The unselected browser could still reach the IP-check page. Verify that the strict mode is connected and retry with a fresh browser connection.";
            else if (!controlAfter.Text.Trim().Equals("blocked", StringComparison.OrdinalIgnoreCase))
                result.Text = "Enter blocked if the unselected browser could not reach the IP-check page.";
            else
                result.Text = ComparePublicIps(selectedBefore.Text, controlBefore.Text, selectedAfter.Text, controlBefore.Text,
                    success: "The selected browser’s IP changed and the unselected browser could not reach the check page. Disconnect and confirm both browsers can reach it again.");
        };
        reset.Click += (_, _) =>
        {
            foreach (var input in new[] { selectedBefore, controlBefore, selectedAfter, controlAfter }) input.Text = "";
            result.Text = "";
        };
        return panel;

        void AddPair(TextBox left, TextBox right)
        {
            var pair = new Grid { ColumnSpacing = 8 };
            pair.ColumnDefinitions.Add(new ColumnDefinition());
            pair.ColumnDefinitions.Add(new ColumnDefinition());
            pair.Children.Add(left);
            Grid.SetColumn(right, 1);
            pair.Children.Add(right);
            panel.Children.Add(pair);
        }
    }

    static string ComparePublicIps(string selectedBefore, string controlBefore, string selectedAfter, string controlAfter, string? success = null)
    {
        string[] readings = [selectedBefore, controlBefore, selectedAfter, controlAfter];
        var addresses = readings.Select(value => IPAddress.TryParse(value.Trim(), out var ip) ? ip : null).ToArray();
        if (addresses.Any(ip => ip is null)) return "Enter four valid IP addresses.";
        if (addresses.Select(ip => ip!.AddressFamily).Distinct().Count() != 1)
            return "Compare readings from the same IP family. Use all IPv4 or all IPv6.";
        if (!Equals(addresses[1], addresses[3])) return "The unselected browser’s IP changed too. Check the selected apps and any other VPN or proxy.";
        if (Equals(addresses[0], addresses[2])) return "The selected browser’s IP stayed the same. Try a route with a distinct exit IP and open a fresh browser connection.";
        return success ?? "The selected browser’s IP changed and the unselected browser’s IP stayed the same. Disconnect and confirm both return to their baseline IPs.";
    }
}
