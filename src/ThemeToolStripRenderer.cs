using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DshLauncher;

/// <summary>WinUI 3 风格菜单渲染器：留白、圆角、悬停层级和文字跟随当前页面调色板。</summary>
public sealed class ThemeToolStripRenderer : ToolStripProfessionalRenderer
{
    private ThemeHelper.Palette _palette;
    private readonly ThemeColorTable _colorTable;
    private ToolStripItem? _hoveredItem;

    public ThemeToolStripRenderer(ThemeHelper.Palette? palette = null)
        : base(new ThemeColorTable(palette ?? InitialPalette()))
    {
        _palette = palette ?? InitialPalette();
        _colorTable = (ThemeColorTable)ColorTable;
    }

    private static ThemeHelper.Palette InitialPalette() =>
        ThemeHelper.HasPagePalette ? ThemeHelper.CurrentPagePalette : ThemeHelper.GetPalette(ThemeHelper.IsSystemDarkMode());

    internal void ApplyPalette(ThemeHelper.Palette palette)
    {
        _palette = palette;
        _colorTable.SetPalette(palette);
    }

    internal void SetHoveredItem(ToolStripItem? item)
    {
        if (ReferenceEquals(_hoveredItem, item)) return;
        _hoveredItem = item;
        item?.Owner?.Invalidate(true);
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
        var bounds = e.Item.Bounds;
        bounds.Inflate(-1, -1);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var hovered = e.Item.Enabled &&
            (e.Item.Selected || e.Item.Pressed || ReferenceEquals(e.Item, _hoveredItem));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(bounds, 6);
        using var brush = new SolidBrush(hovered ? _palette.Surface : _palette.SurfaceAlt);
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

    internal static GraphicsPath RoundedPath(Rectangle bounds, int radius)
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
        private ThemeHelper.Palette _p;

        public ThemeColorTable(ThemeHelper.Palette palette) => _p = palette;

        public void SetPalette(ThemeHelper.Palette palette) => _p = palette;

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

/// <summary>ContextMenuStrip with explicit hover tracking and a clipped rounded window region.</summary>
internal sealed class ThemedContextMenuStrip : ContextMenuStrip
{
    private readonly ThemeToolStripRenderer _themeRenderer;
    private ToolStripItem? _hoveredItem;

    public ThemedContextMenuStrip(ThemeHelper.Palette? palette = null)
    {
        var initial = palette ?? (ThemeHelper.HasPagePalette
            ? ThemeHelper.CurrentPagePalette
            : ThemeHelper.GetPalette(ThemeHelper.IsSystemDarkMode()));
        _themeRenderer = new ThemeToolStripRenderer(initial);
        Renderer = _themeRenderer;
        BackColor = initial.SurfaceAlt;
        ForeColor = initial.Text;
        Padding = new Padding(4);
        ShowImageMargin = false;
        ItemAdded += OnItemAdded;
        SizeChanged += (_, _) => UpdateRoundedRegion();
        Opening += (_, _) => UpdateRoundedRegion();
        ThemeHelper.PagePaletteChanged += OnPagePaletteChanged;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateRoundedRegion();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) UpdateRoundedRegion();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ThemeHelper.PagePaletteChanged -= OnPagePaletteChanged;
        base.Dispose(disposing);
    }

    private void OnItemAdded(object? sender, ToolStripItemEventArgs e)
    {
        if (e.Item != null) AttachItem(e.Item);
    }

    private void AttachItem(ToolStripItem item)
    {
        if (item is not ToolStripMenuItem menuItem) return;
        menuItem.MouseEnter += OnItemMouseEnter;
        menuItem.MouseLeave += OnItemMouseLeave;
        menuItem.DropDown.ItemAdded += OnItemAdded;
        foreach (ToolStripItem child in menuItem.DropDownItems) AttachItem(child);
    }

    private void OnItemMouseEnter(object? sender, EventArgs e)
    {
        if (sender is ToolStripItem item)
        {
            _hoveredItem = item;
            _themeRenderer.SetHoveredItem(item);
        }
        Invalidate(true);
    }

    private void OnItemMouseLeave(object? sender, EventArgs e)
    {
        if (sender is ToolStripItem item && ReferenceEquals(item, _hoveredItem))
        {
            _hoveredItem = null;
            _themeRenderer.SetHoveredItem(null);
        }
        Invalidate(true);
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
            _themeRenderer.ApplyPalette(palette);
            BackColor = palette.SurfaceAlt;
            ForeColor = palette.Text;
            Invalidate(true);
            UpdateRoundedRegion();
        }
        catch
        {
            // The menu can be closing while dsh reports a theme update.
        }
    }

    private void UpdateRoundedRegion()
    {
        if (!IsHandleCreated || Width <= 2 || Height <= 2) return;
        using var path = ThemeToolStripRenderer.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 9);
        var next = new Region(path);
        var previous = Region;
        Region = next;
        previous?.Dispose();
    }
}
