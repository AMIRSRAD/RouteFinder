using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;
using Windows.Graphics;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    static readonly SizeInt32[] WindowSizes = [new(1120, 680), new(1050, 690), new(1440, 900), new(1600, 1000)];

    void ApplyWindowSize(bool center = false)
    {
        var size = WindowSizes[Math.Clamp(preferences.WindowPreset, 0, WindowSizes.Length - 1)];
        var pointer = System.Windows.Forms.Cursor.Position;
        var display = center ? DisplayArea.GetFromPoint(new PointInt32(pointer.X, pointer.Y), DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display is not null)
        {
            var work = display.WorkArea;
            size = new SizeInt32(Math.Min(size.Width, work.Width), Math.Min(size.Height, work.Height));
            if (center)
            {
                AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - size.Width) / 2,
                    work.Y + (work.Height - size.Height) / 2, size.Width, size.Height));
                return;
            }
        }
        AppWindow.Resize(size);
    }

    void ApplyActionIcons()
    {
        AddIcon(ActivityButton, "\uE9D9");
        AddIcon(SettingsButton, "\uE713");
        AddIcon(AdvancedButton, "\uE90F");
        AddIcon(AddAppsButton, "\uE710");
        AddIcon(AddRouteButton, "\uE710");
        AddIcon(TestRouteButton, "\uE9D9");
        AddIcon(TestAllButton, "\uE8FD");
        AddIcon(ConnectButton, "\uE7E8");
    }

    static void AddIcon(Button button, string glyph)
    {
        string caption = (string)button.Content;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        row.Children.Add(new TextBlock { Text = caption });
        button.Content = row;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, caption);
    }

    static void SetActionLabel(Button button, string caption, string? glyph = null)
    {
        var row = (StackPanel)button.Content;
        string label = UiLanguage.Text(caption);
        ((TextBlock)row.Children[1]).Text = label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        if (glyph is not null) ((FontIcon)row.Children[0]).Glyph = glyph;
    }

    void ApplyLanguage()
    {
        if (UiLanguage.Index == 0) return;
        Root.FlowDirection = UiLanguage.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        WindowHeader.FlowDirection = FlowDirection.LeftToRight;
        Translate(Root);
        for (int index = 0; index < Scope.Items.Count; index++)
            if (Scope.Items[index] is string label) Scope.Items[index] = UiLanguage.Text(label);

        static void Translate(UIElement element)
        {
            switch (element)
            {
                case Panel panel:
                    foreach (UIElement child in panel.Children) Translate(child);
                    break;
                case Border { Child: { } child }:
                    Translate(child);
                    break;
                case Button button:
                    if (button.Content is string caption) button.Content = UiLanguage.Text(caption);
                    if (button.Flyout is MenuFlyout flyout)
                        foreach (var item in flyout.Items.OfType<MenuFlyoutItem>()) item.Text = UiLanguage.Text(item.Text);
                    break;
                case ContentControl { Content: UIElement content }:
                    Translate(content);
                    break;
                case TextBlock text:
                    text.Text = UiLanguage.Text(text.Text);
                    break;
            }
        }
    }

    async Task OpenAppearance()
    {
        var languages = new ComboBox
        {
            Header = UiLanguage.Text("Language"), ItemsSource = new[] { "English", "فارسی", "العربية" },
            SelectedIndex = UiLanguage.Index
        };
        var sizes = new ComboBox
        {
            Header = UiLanguage.Text("Window resolution"),
            ItemsSource = WindowSizes.Select(size => $"{size.Width} × {size.Height}").ToArray(),
            SelectedIndex = Math.Clamp(preferences.WindowPreset, 0, WindowSizes.Length - 1)
        };
        var tips = new ToggleSwitch { Header = UiLanguage.Text("Show tutorials"), IsOn = preferences.ShowTutorials };
        var panel = new StackPanel { Spacing = 12, MinWidth = 270 };
        panel.Children.Add(languages);
        panel.Children.Add(sizes);
        panel.Children.Add(tips);
        var dialog = MakeDialog("Window and language preferences", panel, "Save");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        bool languageChanged = UiLanguage.Index != languages.SelectedIndex;
        var updated = preferences with { WindowPreset = sizes.SelectedIndex, ShowTutorials = tips.IsOn };
        WriteStore("preferences", () => ProfileStore.SavePreferences(updated));
        preferences = updated;
        if (languageChanged)
        {
            UiLanguage.Save(languages.SelectedIndex);
            Report(UiLanguage.Text("Restart RouteFinder to apply the language."), InfoBarSeverity.Informational);
        }
        ApplyWindowSize();
        if (!languageChanged && tips.IsOn) ShowRoutingTip();
    }

    void ShowRoutingTip()
    {
        if (!Notice.IsOpen)
            Report(UiLanguage.Text("Choose traffic, choose a route, then connect. You can test a route before connecting."),
                InfoBarSeverity.Informational);
    }
}
