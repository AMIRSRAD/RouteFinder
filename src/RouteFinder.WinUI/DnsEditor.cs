using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;

namespace RouteFinder.WinUI;

/// <summary>
/// DNS entry without comma lists: pick a well-known provider or type a primary and optional secondary address.
/// </summary>
public sealed class DnsEditor : StackPanel
{
    sealed record Provider(string Name, string Primary, string Secondary)
    {
        public override string ToString() => Name;
    }

    static readonly Provider Automatic = new("Automatic", "", "");
    static readonly Provider Custom = new("Custom", "", "");
    static readonly Provider[] Providers =
    [
        Automatic,
        new("Cloudflare", "1.1.1.1", "1.0.0.1"),
        new("Google", "8.8.8.8", "8.8.4.4"),
        new("Quad9", "9.9.9.9", "149.112.112.112"),
        new("OpenDNS", "208.67.222.222", "208.67.220.220"),
        Custom
    ];

    readonly ComboBox provider = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = Providers };
    readonly TextBox primary = new() { PlaceholderText = "e.g. 1.1.1.1" };
    readonly TextBox secondary = new() { PlaceholderText = "optional" };
    bool updating;

    public DnsEditor()
    {
        Spacing = 10;
        provider.Header = UiLanguage.Text("DNS provider");
        primary.Header = UiLanguage.Text("Primary DNS");
        secondary.Header = UiLanguage.Text("Secondary DNS");
        provider.SelectedItem = Automatic;
        var pair = new Grid { ColumnSpacing = 10 };
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(secondary, 1);
        pair.Children.Add(primary);
        pair.Children.Add(secondary);
        Children.Add(provider);
        Children.Add(pair);
        provider.SelectionChanged += (_, _) =>
        {
            if (updating || provider.SelectedItem is not Provider chosen || chosen == Custom) return;
            updating = true;
            primary.Text = chosen.Primary;
            secondary.Text = chosen.Secondary;
            updating = false;
            Changed?.Invoke();
        };
        primary.TextChanged += (_, _) => Edited();
        secondary.TextChanged += (_, _) => Edited();
    }

    public event Action? Changed;

    /// <summary>True when "Automatic" is chosen, which means: do not override DNS.</summary>
    public bool IsAutomatic => Servers.Length == 0;

    public string[] Servers
    {
        get => new[] { primary.Text.Trim(), secondary.Text.Trim() }.Where(value => value.Length > 0).ToArray();
        set
        {
            updating = true;
            primary.Text = value.ElementAtOrDefault(0) ?? "";
            secondary.Text = value.ElementAtOrDefault(1) ?? "";
            Match();
            updating = false;
        }
    }

    // Any extra servers beyond two cannot be shown, so say so rather than silently dropping them.
    public string? Extra { get; private set; }

    public void Load(IEnumerable<string> servers)
    {
        var list = servers.ToArray();
        Extra = list.Length > 2 ? $"{list.Length - 2} more" : null;
        Servers = list;
    }

    void Edited()
    {
        if (updating) return;
        updating = true;
        Match();
        updating = false;
        Changed?.Invoke();
    }

    void Match()
    {
        var found = Providers.FirstOrDefault(item => item != Custom && item.Primary == primary.Text.Trim() && item.Secondary == secondary.Text.Trim());
        provider.SelectedItem = found ?? (primary.Text.Trim().Length == 0 && secondary.Text.Trim().Length == 0 ? Automatic : Custom);
    }

    bool enabled = true;

    // A StackPanel has no IsEnabled of its own, so switch the three inputs together.
    public bool IsEnabled
    {
        get => enabled;
        set => provider.IsEnabled = primary.IsEnabled = secondary.IsEnabled = enabled = value;
    }
}
