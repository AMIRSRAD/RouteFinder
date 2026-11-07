using System.Net;
using System.Net.Sockets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;
using RouteFinder.Core;
using RouteFinder.Service;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    static bool IsProbeAddress(IPAddress address) =>
        !IPAddress.IsLoopback(address) && (address.AddressFamily == AddressFamily.InterNetwork
            ? address.GetAddressBytes()[0] is > 0 and < 224
            : !address.IsIPv6Multicast && !address.IsIPv6LinkLocal && !address.IsIPv4MappedToIPv6 &&
              !address.Equals(IPAddress.IPv6Any) && address.ScopeId == 0);

    async void TestCustomDestination(object sender, RoutedEventArgs args)
    {
        var input = new TextBox { Header = "Hostname or IP address", MinWidth = 290 };
        var dialog = MakeDialog("Test destination", input, "Continue");
        dialog.IsPrimaryButtonEnabled = false;
        input.TextChanged += (_, _) =>
        {
            string value = input.Text.Trim();
            dialog.IsPrimaryButtonEnabled = IPAddress.TryParse(value, out var address)
                ? IsProbeAddress(address) : Uri.CheckHostName(value) == UriHostNameType.Dns;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!IPAddress.TryParse(input.Text.Trim(), out var destination))
        {
            if (preview) destination = IPAddress.Parse("192.0.2.10");
            else await Run("Resolving destination…", async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var addresses = await Dns.GetHostAddressesAsync(input.Text.Trim(), timeout.Token);
                destination = addresses.Where(IsProbeAddress)
                    .OrderBy(address => address.AddressFamily != AddressFamily.InterNetwork).FirstOrDefault()
                    ?? throw new InvalidOperationException("The hostname has no usable IP address.");
            });
        }
        if (destination is not null && IsProbeAddress(destination)) await ConfigureDestination(destination);
    }

    async Task ConfigureDestination(IPAddress address)
    {
        var protocol = new RadioButtons { Header = "Protocol", MaxColumns = 2 };
        protocol.Items.Add("ICMP ping · Windows route only");
        protocol.Items.Add("TCP connect");
        protocol.SelectedIndex = 1;
        var port = Field("TCP port", 443);
        var count = Field("ICMP probe count", 4);
        var interval = Field("ICMP interval in milliseconds", 250);
        var size = Field("ICMP payload bytes", 32);
        var timeout = Field("Timeout in milliseconds", 1500);
        var ttl = Field("ICMP TTL", 128);
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 8, MinWidth = 305 };
        foreach (UIElement element in new UIElement[] { protocol, port, count, interval, size, timeout, ttl, validation })
            content.Children.Add(element);
        var eligible = Routes().Where(choice => choice.Service?.IsDownload != true).ToArray();
        var dialog = MakeDialog($"Test {address}", new ScrollViewer { Content = content, MaxHeight = 390 }, "Choose routes");
        void UpdateProtocol()
        {
            bool tcp = protocol.SelectedIndex == 1;
            dialog.IsPrimaryButtonEnabled = tcp && eligible.Length > 0;
            port.Visibility = tcp ? Visibility.Visible : Visibility.Collapsed;
            foreach (var field in new[] { count, interval, size, ttl })
                field.Visibility = tcp ? Visibility.Collapsed : Visibility.Visible;
        }
        protocol.SelectionChanged += (_, _) => UpdateProtocol();
        UpdateProtocol();
        dialog.SecondaryButtonText = "Windows route only";
        DestinationOptions? options = null;
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !Validate();
        dialog.SecondaryButtonClick += (_, args) => args.Cancel = !Validate();
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None || options is null) return;
        var chosen = result == ContentDialogResult.Primary ? await ChooseRoutes("Routes to compare", eligible, 1) : [];
        if (result == ContentDialogResult.Primary && chosen.Length == 0) return;
        await RunDestinationTests(address, options, chosen);

        bool Validate()
        {
            try
            {
                bool tcp = protocol.SelectedIndex == 1;
                options = new DestinationOptions(tcp ? Number(port, "TCP port", 1, 65535) : null,
                    tcp ? 4 : Number(count, "Probe count", 1, 20)!.Value,
                    tcp ? 0 : Number(interval, "Interval", 0, 5000)!.Value,
                    tcp ? 32 : Number(size, "Payload", 1, 1400)!.Value,
                    Number(timeout, "Timeout", 250, 10000)!.Value,
                    tcp ? 128 : Number(ttl, "TTL", 1, 255)!.Value);
                return true;
            }
            catch (FormatException error) { validation.Text = error.Message; return false; }
        }

        static TextBox Field(string label, int value) => new() { Header = label, Text = value.ToString() };
    }

    sealed record DestinationOptions(int? Port, int Count, int Interval, int Size, int Timeout, int Ttl);

    async Task RunDestinationTests(IPAddress address, DestinationOptions options, RouteChoice[] chosen)
    {
        if (busy || Connected || session.NeedsCleanup || (chosen.Length > 0 && (options.Port is null || !preview && !EnsureAdministrator()))) return;
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        activeTest = cancellation;
        var reports = new List<string>();
        try
        {
            await Run("Testing destination…", async () =>
            {
                try
                {
                    ConnectionDetail.Text = $"Measuring the Windows route to {address}";
                    PingReport baseline;
                    if (preview) baseline = new PingReport("Simulated Windows baseline", 4, 4, 52, 2);
                    else if (options.Port is int port)
                    {
                        var tcp = await Diagnostics.MeasureTcp(address.ToString(), port, token, options.Timeout);
                        baseline = new PingReport(tcp.Detail, Tcp: tcp);
                    }
                    else baseline = await Diagnostics.MeasurePing(address.ToString(), options.Count, cancellation: token,
                        intervalMilliseconds: options.Interval, payloadBytes: options.Size, timeoutMilliseconds: options.Timeout, ttl: options.Ttl);
                    Record("Windows baseline", baseline);
                    foreach (var choice in chosen)
                    {
                        token.ThrowIfCancellationRequested();
                        ConnectionDetail.Text = $"{choice.Title} · {address}";
                        try
                        {
                            var report = preview ? new PingReport("Simulated route probe", 4, 4, 43, 1)
                                : await ProbeThroughRoute(choice, address, options.Port!.Value, options.Timeout, token);
                            Record(choice.Title, report, baseline);
                        }
                        catch (EzConnectServiceException error) when (error.StatusCode == 429)
                        {
                            string message = $"Stopped at {choice.Title} · {error.Message} Completed results were kept.";
                            reports.Add(message);
                            AddActivity(message);
                            break;
                        }
                        catch (Exception error) when (error is not OperationCanceledException && !session.NeedsCleanup)
                        {
                            reports.Add($"{choice.Title}\n{error.Message}");
                            AddActivity($"{choice.Title} · {error.Message}");
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { reports.Add("Tests canceled."); }
            });
        }
        finally { activeTest = null; Refresh(); }
        if (exitRequested || reports.Count == 0) return;
        string output = string.Join("\n\n", reports);
        var text = new TextBox { Text = output, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 220, MaxHeight = 390 };
        var dialog = MakeDialog($"Results · {address}", text, preview ? "" : "Export results");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ExportTestReport("RouteFinder-destination-test", output);

        void Record(string name, PingReport report, PingReport? baseline = null)
        {
            string summary = $"{name} · {report.Summary}" + (baseline is null ? "" : report.CompareWith(baseline));
            reports.Add($"{summary}\n{report.Detail}");
            AddActivity(summary);
        }
    }

    async Task<PingReport> ProbeThroughRoute(RouteChoice choice, IPAddress address, int port, int timeout, CancellationToken token)
    {
        var profile = choice.Profile is { } saved ? TunnelProfile.Parse(saved.Configuration, saved.Name) : null;
        Task<PingReport> Probe() => session.ProbeDestinationAsync(choice.Service, profile, address, port, preferences.PortableEngine, token, timeout,
            preferences.AutoMtu, preferences.TunnelMtu, preferences.KeepaliveSeconds, ConfiguredProxy);
        return await RunPacedProbe(choice, Probe, token);
    }
}
