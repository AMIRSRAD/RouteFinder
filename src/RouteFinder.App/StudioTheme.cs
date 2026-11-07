using System.Drawing.Drawing2D;

namespace RouteFinder.App;

internal static class StudioTheme
{
    public static readonly Color Base = Color.FromArgb(14, 20, 25);
    public static readonly Color Sidebar = Color.FromArgb(19, 27, 33);
    public static readonly Color Card = Color.FromArgb(26, 36, 43);
    public static readonly Color Raised = Color.FromArgb(38, 51, 60);
    public static readonly Color Ink = Color.FromArgb(239, 246, 245);
    public static readonly Color Muted = Color.FromArgb(172, 191, 195);
    public static readonly Color Accent = Color.FromArgb(121, 242, 196);
    public static readonly Color AccentInk = Color.FromArgb(8, 38, 34);
    public static readonly Color Warning = Color.FromArgb(253, 184, 109);

    public static Label Label(string text, float size = 10, bool strong = false, Color? color = null) =>
        new() { Text = UiLanguage.Text(text), Font = new("Segoe UI", size, strong ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = color ?? Ink, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

    public static Button Button(string text, bool primary = false) => new StudioButton
    {
        Text = UiLanguage.Text(text), Height = 38, AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
        BackColor = primary ? Accent : Raised, ForeColor = primary ? AccentInk : Ink,
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Padding = new Padding(11, 0, 11, 0),
        Margin = new Padding(0, 0, 8, 0), UseVisualStyleBackColor = false,
        FlatAppearance = { BorderSize = 0, MouseOverBackColor = primary ? Color.FromArgb(162, 250, 220) : Color.FromArgb(53, 70, 79) }
    };

    public static TextBox Input(string hint = "") => new()
    {
        PlaceholderText = UiLanguage.Text(hint), BackColor = Raised, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 11), Margin = new Padding(0, 4, 8, 0)
    };

    public static void StylePicker(ComboBox picker)
    {
        picker.BackColor = Raised;
        picker.ForeColor = Ink;
        picker.FlatStyle = FlatStyle.Flat;
        picker.DrawMode = DrawMode.OwnerDrawFixed;
        picker.DrawItem += (_, e) =>
        {
            bool selected = e.State.HasFlag(DrawItemState.Selected);
            using var background = new SolidBrush(selected ? Accent : Raised);
            e.Graphics.FillRectangle(background, e.Bounds);
            if (e.Index >= 0)
            {
                var textBounds = Rectangle.Inflate(e.Bounds, -4, 0);
                TextRenderer.DrawText(e.Graphics, picker.GetItemText(picker.Items[e.Index]), picker.Font, textBounds,
                    selected ? AccentInk : Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    (picker.RightToLeft == RightToLeft.Yes ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left));
            }
            e.DrawFocusRectangle();
        };
    }

    public static TableLayoutPanel Stack(params int[] heights)
    {
        var panel = new TableLayoutPanel { ColumnCount = 1, RowCount = heights.Length, Dock = DockStyle.Fill, Margin = Padding.Empty };
        foreach (int height in heights)
            panel.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
        return panel;
    }

    public static FlowLayoutPanel Actions() => new()
    {
        Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false, Padding = new Padding(0, 4, 0, 0)
    };

    public static TableLayoutPanel Split(int rightWidth = 0)
    {
        var panel = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, rightWidth == 0 ? 50 : 100));
        panel.ColumnStyles.Add(new ColumnStyle(rightWidth == 0 ? SizeType.Percent : SizeType.Absolute, rightWidth == 0 ? 50 : rightWidth));
        return panel;
    }
}

internal sealed class StudioButton : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled)
        {
            base.OnPaint(e);
            return;
        }

        using var background = new SolidBrush(StudioTheme.Raised);
        e.Graphics.FillRectangle(background, ClientRectangle);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, StudioTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class NetworkArt : Control
{
    public NetworkArt()
    {
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new LinearGradientBrush(ClientRectangle, Color.FromArgb(24, 40, 65), Color.FromArgb(18, 66, 75), 25);
        g.FillRectangle(background, ClientRectangle);
        using var grid = new SolidBrush(Color.FromArgb(48, 130, 188, 208));
        for (int x = 14; x < Width; x += 18)
            for (int y = 14; y < Height; y += 18)
                g.FillEllipse(grid, x, y, 2, 2);
        PointF[] points = [new(Width * .12f, Height * .73f), new(Width * .37f, Height * .41f),
            new(Width * .61f, Height * .56f), new(Width * .86f, Height * .26f)];
        using var line = new Pen(StudioTheme.Accent, 2);
        using var glow = new Pen(Color.FromArgb(32, StudioTheme.Accent), 12);
        g.DrawLines(glow, points);
        g.DrawLines(line, points);
        using var node = new SolidBrush(StudioTheme.Accent);
        foreach (var p in points)
        {
            g.FillEllipse(node, p.X - 4, p.Y - 4, 8, 8);
            g.DrawEllipse(line, p.X - 9, p.Y - 9, 18, 18);
        }
    }
}
