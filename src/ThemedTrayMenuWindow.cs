using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DshLauncher;

internal sealed class TrayMenuEntry
{
    public TrayMenuEntry(string text, Action? action = null, bool separator = false)
    {
        Text = text;
        Action = action;
        IsSeparator = separator;
    }

    public string Text { get; set; }
    public Action? Action { get; }
    public bool IsSeparator { get; }
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A real WinForms popup for the tray menu. Using Button controls instead of
/// ToolStrip selection messages gives every row reliable hover feedback.
/// </summary>
internal sealed class ThemedTrayMenuWindow : Form
{
    private readonly IReadOnlyList<TrayMenuEntry> _entries;
    private readonly FlowLayoutPanel _list = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Margin = new Padding(0),
        Padding = new Padding(6),
    };
    private ThemeHelper.Palette _palette;
    private Point _anchor;

    public ThemedTrayMenuWindow(IReadOnlyList<TrayMenuEntry> entries, ThemeHelper.Palette palette)
    {
        _entries = entries;
        _palette = palette;
        FormBorderStyle = FormBorderStyle.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        TopMost = true;
        BackColor = palette.SurfaceAlt;
        DoubleBuffered = true;
        Controls.Add(_list);
        Deactivate += (_, _) => { if (!IsDisposed) Close(); };
        Resize += (_, _) => UpdateRegion();
        ThemeHelper.PagePaletteChanged += OnPagePaletteChanged;
        FormClosed += (_, _) => ThemeHelper.PagePaletteChanged -= OnPagePaletteChanged;
        RebuildLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateRegion();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) UpdateRegion();
    }

    public void ShowAt(Point point)
    {
        _anchor = point;
        RebuildLayout();
        var area = Screen.FromPoint(point).WorkingArea;
        var x = point.X;
        var y = point.Y - Height;
        if (y < area.Top) y = point.Y;
        Location = ClampLocation(new Point(x, y), area);
        Show();
        Activate();
    }

    public void RefreshMenu()
    {
        if (IsDisposed) return;
        RebuildLayout();
        if (Visible) Location = ClampLocation(_anchor, Screen.FromPoint(_anchor).WorkingArea);
    }

    private void OnPagePaletteChanged(ThemeHelper.Palette palette)
    {
        if (IsDisposed) return;
        try
        {
            if (IsHandleCreated && InvokeRequired)
            {
                BeginInvoke(() => OnPagePaletteChanged(palette));
                return;
            }
            _palette = palette;
            BackColor = palette.SurfaceAlt;
            RebuildLayout();
            Invalidate(true);
        }
        catch
        {
            // Theme changes can race with the tray popup closing.
        }
    }

    private void RebuildLayout()
    {
        _list.SuspendLayout();
        _list.Controls.Clear();
        var width = CalculateWidth();
        foreach (var entry in _entries)
        {
            if (entry.IsSeparator)
            {
                _list.Controls.Add(new Panel
                {
                    Width = width - 12,
                    Height = 1,
                    Margin = new Padding(6, 8, 6, 8),
                    BackColor = _palette.Border,
                });
                continue;
            }

            var button = new Button
            {
                Text = entry.Text,
                Tag = entry,
                Width = width,
                Height = 38,
                AutoSize = false,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 12, 0),
                Margin = new Padding(2, 2, 2, 2),
                TabStop = false,
                Enabled = entry.Enabled,
                BackColor = _palette.SurfaceAlt,
                ForeColor = entry.Enabled ? _palette.Text : _palette.MutedText,
                FlatAppearance = { BorderSize = 0 },
            };
            var normal = _palette.SurfaceAlt;
            var hover = _palette.WindowBack.GetBrightness() < 0.55f
                ? ThemeHelper.Lighten(_palette.Surface, 8)
                : ThemeHelper.Darken(_palette.Surface, 8);
            button.MouseEnter += (_, _) =>
            {
                if (button.Enabled) button.BackColor = hover;
            };
            button.MouseLeave += (_, _) => button.BackColor = normal;
            button.Click += (_, _) =>
            {
                if (!button.Enabled || entry.Action == null) return;
                Close();
                entry.Action();
            };
            _list.Controls.Add(button);
        }
        _list.ResumeLayout(true);
        PerformLayout();
        UpdateRegion();
    }

    private int CalculateWidth()
    {
        var max = 0;
        foreach (var entry in _entries)
        {
            if (!entry.IsSeparator)
                max = Math.Max(max, TextRenderer.MeasureText(entry.Text, Font).Width);
        }
        return Math.Max(250, max + 48);
    }

    private Point ClampLocation(Point point, Rectangle area)
    {
        return new Point(
            Math.Clamp(point.X, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(point.Y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    private void UpdateRegion()
    {
        if (!IsHandleCreated || Width <= 2 || Height <= 2) return;
        using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 10);
        var next = new Region(path);
        var previous = Region;
        Region = next;
        previous?.Dispose();
    }

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
