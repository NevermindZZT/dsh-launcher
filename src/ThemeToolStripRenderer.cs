using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DshLauncher;

/// <summary>WinUI 3 风格菜单渲染器：留白、圆角、悬停层级和文字跟随当前页面调色板。</summary>
public sealed class ThemeToolStripRenderer : ToolStripProfessionalRenderer
{
    private readonly ThemeHelper.Palette _palette;

    public ThemeToolStripRenderer(ThemeHelper.Palette? palette = null)
        : base(new ThemeColorTable(palette ?? ThemeHelper.GetPalette(ThemeHelper.IsSystemDarkMode())))
    {
        _palette = palette ?? ThemeHelper.GetPalette(ThemeHelper.IsSystemDarkMode());
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var width = e.ToolStrip.ClientSize.Width;
        var height = e.ToolStrip.ClientSize.Height;
        if (width <= 1 || height <= 1) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(new Rectangle(0, 0, width - 1, height - 1), 9);
        using var brush = new SolidBrush(_palette.SurfaceAlt);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var width = e.ToolStrip.ClientSize.Width;
        var height = e.ToolStrip.ClientSize.Height;
        if (width <= 1 || height <= 1) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(new Rectangle(0, 0, width - 1, height - 1), 9);
        using var pen = new Pen(_palette.Border);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Enabled || !e.Item.Selected) return;
        var bounds = e.Item.Bounds;
        bounds.Inflate(-1, -1);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(bounds, 6);
        using var brush = new SolidBrush(_palette.Surface);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var bounds = e.Item.Bounds;
        var y = bounds.Top + bounds.Height / 2;
        using var pen = new Pen(_palette.Border);
        e.Graphics.DrawLine(pen, bounds.Left + 8, y, bounds.Right - 8, y);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = _palette.Text;
        base.OnRenderItemText(e);
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

    private sealed class ThemeColorTable : ProfessionalColorTable
    {
        private readonly ThemeHelper.Palette _p;

        public ThemeColorTable(ThemeHelper.Palette palette) => _p = palette;

        public override Color ToolStripDropDownBackground => _p.SurfaceAlt;
        public override Color ImageMarginGradientBegin => _p.SurfaceAlt;
        public override Color ImageMarginGradientMiddle => _p.SurfaceAlt;
        public override Color ImageMarginGradientEnd => _p.SurfaceAlt;
        public override Color MenuBorder => _p.Border;
        public override Color MenuItemBorder => _p.Border;
        public override Color MenuItemSelected => _p.Surface;
        public override Color MenuItemSelectedGradientBegin => _p.Surface;
        public override Color MenuItemSelectedGradientEnd => _p.Surface;
        public override Color MenuItemPressedGradientBegin => _p.Surface;
        public override Color MenuItemPressedGradientMiddle => _p.Surface;
        public override Color MenuItemPressedGradientEnd => _p.Surface;
        public override Color SeparatorDark => _p.Border;
        public override Color SeparatorLight => _p.Border;
        public override Color CheckBackground => _p.Accent;
        public override Color CheckSelectedBackground => _p.Accent;
    }
}
