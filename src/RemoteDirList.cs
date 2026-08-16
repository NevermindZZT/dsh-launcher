using System.Drawing;
using System.Drawing.Drawing2D;

namespace DshLauncher;

/// <summary>
/// 自绘目录列表控件（远端目录浏览器用）：深色主题、自绘滚动条、行背景/选中高亮、
/// 显示目录 basename + 文件夹图标（完整路径在 Tag）。
/// </summary>
public sealed class RemoteDirList : Control
{
    private readonly List<string> _items = new();
    private int _scrollOffset;
    private int _selectedIndex = -1;
    private const int RowHeight = 28;
    private const int ScrollbarWidth = 10;

    /// <summary>双击目录（进入）。</summary>
    public event Action<string>? DirectoryActivated;
    /// <summary>单击选中目录。</summary>
    public event Action<string>? DirectorySelected;

    /// <summary>配色（由表单设置，跟随主题）。</summary>
    public Color Surface { get; set; } = Color.FromArgb(0x22, 0x24, 0x28);
    public Color Text { get; set; } = Color.FromArgb(0xE0, 0xE0, 0xE0);
    public Color Accent { get; set; } = Color.FromArgb(0x3B, 0x82, 0xF6);
    public Color Scrollbar { get; set; } = Color.FromArgb(0x4A, 0x4D, 0x52);

    public RemoteDirList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Surface;
    }

    public void SetItems(IEnumerable<string> paths)
    {
        _items.Clear();
        _items.AddRange(paths);
        _scrollOffset = 0;
        _selectedIndex = -1;
        Invalidate();
    }

    public string? SelectedPath => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;

    private int MaxOffset => Math.Max(0, _items.Count - VisibleRows);

    private int VisibleRows => Math.Max(1, ClientSize.Height / RowHeight);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);

        var listWidth = ClientSize.Width - ScrollbarWidth;
        var visible = VisibleRows;
        for (var i = 0; i < visible; i++)
        {
            var idx = _scrollOffset + i;
            if (idx >= _items.Count) break;
            var rect = new Rectangle(0, i * RowHeight, listWidth, RowHeight);
            if (idx == _selectedIndex)
            {
                using var sel = new SolidBrush(Color.FromArgb(80, Accent));
                g.FillRectangle(sel, rect);
            }
            var name = _items[idx].TrimEnd('/').Split('/').LastOrDefault() ?? _items[idx];
            var icon = "📁  ";
            TextRenderer.DrawText(g, icon + name, Font, new Rectangle(8, rect.Y, listWidth - 16, RowHeight), Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        // 自绘滚动条（深色）
        if (_items.Count > VisibleRows)
        {
            var track = new Rectangle(ClientSize.Width - ScrollbarWidth, 0, ScrollbarWidth, ClientSize.Height);
            using var trackBrush = new SolidBrush(Color.FromArgb(30, 30, 33));
            g.FillRectangle(trackBrush, track);
            var thumbHeight = Math.Max(24, (int)(ClientSize.Height * (double)VisibleRows / Math.Max(1, _items.Count)));
            var thumbY = (int)((ClientSize.Height - thumbHeight) * (double)_scrollOffset / Math.Max(1, MaxOffset));
            using var thumbBrush = new SolidBrush(Scrollbar);
            g.FillRectangle(thumbBrush, track.X + 2, thumbY, ScrollbarWidth - 4, thumbHeight);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scrollOffset = Math.Clamp(_scrollOffset - (e.Delta > 0 ? 1 : -1), 0, MaxOffset);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var row = e.Y / RowHeight;
        var idx = _scrollOffset + row;
        if (idx < _items.Count)
        {
            _selectedIndex = idx;
            Invalidate();
            DirectorySelected?.Invoke(_items[idx]);
        }
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
        {
            DirectoryActivated?.Invoke(_items[_selectedIndex]);
        }
    }
}
