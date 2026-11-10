using System.Drawing;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace RouteFinder.WinUI;

/// <summary>
/// The tray menu is a classic Windows menu that ignores the system's dark or light setting. This gives it a renderer
/// that follows the setting and updates when the user switches theme.
/// </summary>
public sealed partial class MainWindow
{
    // The tray and taskbar follow the system theme, not the app theme.
    static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("SystemUsesLightTheme") is int system) return system == 0;
            return key?.GetValue("AppsUseLightTheme") is int apps && apps == 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    void ApplyTrayTheme(bool? forceDark = null)
    {
        if (trayMenu is null) return;
        bool dark = forceDark ?? SystemUsesDarkTheme();
        var colors = new TrayColors(dark);
        trayMenu.Renderer = new TrayRenderer(colors) { RoundedEdges = false };
        trayMenu.BackColor = colors.Menu;
        trayMenu.ForeColor = colors.Text;
        trayMenu.Font = new Font("Segoe UI", 9.5f);
        trayMenu.ShowImageMargin = false;
        foreach (Forms.ToolStripItem item in trayMenu.Items)
        {
            item.BackColor = colors.Menu;
            item.ForeColor = colors.Text;
        }
    }

    void WatchSystemTheme()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs args)
    {
        if (args.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            DispatcherQueue.TryEnqueue(() => ApplyTrayTheme());
    }

    sealed class TrayColors : Forms.ProfessionalColorTable
    {
        public TrayColors(bool dark)
        {
            UseSystemColors = false;
            Menu = dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
            Text = dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(27, 27, 27);
            Disabled = dark ? Color.FromArgb(140, 140, 140) : Color.FromArgb(150, 150, 150);
            Hover = dark ? Color.FromArgb(66, 66, 66) : Color.FromArgb(232, 232, 232);
            Border = dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(210, 210, 210);
        }

        public Color Menu { get; }
        public new Color Text { get; }
        public Color Disabled { get; }
        public Color Hover { get; }
        public Color Border { get; }

        public override Color ToolStripDropDownBackground => Menu;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientMiddle => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Menu;
        public override Color ImageMarginGradientMiddle => Menu;
        public override Color ImageMarginGradientEnd => Menu;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Menu;
    }

    sealed class TrayRenderer(TrayColors colors) : Forms.ToolStripProfessionalRenderer(colors)
    {
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? colors.Text : colors.Disabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(colors.Menu);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }
    }
}
