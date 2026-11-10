using System.Net;
using System.Net.NetworkInformation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    NetworkInterface[] interfaces = [];
    string[] loadedDns = [];
    bool loadingInterfaces;

    // Fills the Interfaces and DNS card from the live Windows adapter list.
    void LoadInterfaces()
    {
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (NetworkInformationException error)
        {
            interfaces = [];
            Report(error.Message);
        }
        string? previous = (InterfacePicker.SelectedItem as string);
        loadingInterfaces = true;
        try
        {
            InterfacePicker.ItemsSource = interfaces.Select(item => item.Name).ToArray();
            int index = Math.Max(Array.FindIndex(interfaces, item => item.Name == previous), 0);
            InterfacePicker.SelectedIndex = interfaces.Length == 0 ? -1 : index;
        }
        finally { loadingInterfaces = false; }
        ShowInterface();
    }

    void InterfaceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!loadingInterfaces) ShowInterface();
    }

    void ShowInterface()
    {
        bool any = InterfacePicker.SelectedIndex >= 0 && InterfacePicker.SelectedIndex < interfaces.Length;
        InterfaceFacts.Visibility = DnsInput.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any)
        {
            InterfaceStatus.Text = UiLanguage.Text("No Windows interfaces are available.");
            return;
        }
        var adapter = interfaces[InterfacePicker.SelectedIndex];
        var ip = adapter.GetIPProperties();
        string Join(IEnumerable<object> values) => values.Any() ? string.Join(", ", values) : "—";
        InterfaceStatus.Text = $"{adapter.OperationalStatus} · {adapter.NetworkInterfaceType}";
        InterfaceGateway.Text = Join(ip.GatewayAddresses.Select(item => (object)item.Address));
        InterfaceAddresses.Text = Join(ip.UnicastAddresses.Select(item => (object)item.Address));
        InterfaceDns.Text = Join(ip.DnsAddresses.Select(item => (object)item));
        loadedDns = ip.DnsAddresses.Select(item => item.ToString()).ToArray();
        DnsInput.Load(loadedDns);
        UpdateDnsButtons();
    }

    void UpdateDnsButtons()
    {
        bool editable = !busy && !session.NeedsCleanup && !preview &&
            InterfacePicker.SelectedIndex >= 0;
        ApplyDnsButton.IsEnabled = AutoDnsButton.IsEnabled = DnsInput.IsEnabled = editable;
    }

    async void ApplyDnsClicked(object sender, RoutedEventArgs args) => await ChangeInterfaceDns(automatic: false);
    async void AutoDnsClicked(object sender, RoutedEventArgs args) => await ChangeInterfaceDns(automatic: true);

    async Task ChangeInterfaceDns(bool automatic)
    {
        if (busy || InterfacePicker.SelectedIndex < 0 ||
            InterfacePicker.SelectedIndex >= interfaces.Length) return;
        var servers = new List<IPAddress>();
        // "Automatic" in the provider list means the same as the Use automatic DNS button.
        if (!automatic && !DnsInput.IsAutomatic)
        {
            // Untouched fields mean "keep what is there", including servers past the two shown.
            var wanted = DnsInput.Extra is not null && DnsInput.Servers.SequenceEqual(loadedDns.Take(2)) ? loadedDns : DnsInput.Servers;
            foreach (string value in wanted)
            {
                if (!IPAddress.TryParse(value, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                {
                    Report($"Invalid DNS address: {value}");
                    return;
                }
                if (!servers.Contains(address)) servers.Add(address);
            }
        }
        if (!EnsureAdministrator()) return;
        string id = interfaces[InterfacePicker.SelectedIndex].Id;
        try
        {
            await Run("Updating interface DNS…", async () =>
            {
                await WindowsNetworkTools.SetAdapterDns(id, servers.ToArray());
                Report("Windows interface DNS updated.", InfoBarSeverity.Success);
            });
        }
        finally { LoadInterfaces(); }
    }
}
