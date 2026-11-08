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
        AddIcon(AddAppsButton, "\uE710");
        AddIcon(AddRouteButton, "\uE710");
        AddIcon(TestRouteButton, "\uE9D9");
        AddIcon(TestAllButton, "\uE8FD");
        AddIcon(ConnectButton, "\uE7E8");
        AddIcon(CopyActivityButton, "\uE8C8");
        AddIcon(SaveReportButton, "\uE74E");
        AddIcon(DiagnosticsButton, "\uE9F9");
        // The round connect button stacks a large power glyph above its label.
        if (ConnectButton.Content is StackPanel connect)
        {
            connect.Orientation = Orientation.Vertical;
            connect.Spacing = 2;
            connect.HorizontalAlignment = HorizontalAlignment.Center;
            ((FontIcon)connect.Children[0]).FontSize = 24;
        }
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
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
            if (item.Content is string caption) item.Content = UiLanguage.Text(caption);

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
                    if (button.Flyout is Flyout { Content: UIElement flyoutContent }) Translate(flyoutContent);
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

    void ShowRoutingTip()
    {
        if (!Notice.IsOpen)
            Report(UiLanguage.Text("Add apps, pick a route, then press Connect. You can test routes on the Routes page first."),
                InfoBarSeverity.Informational);
    }
}
