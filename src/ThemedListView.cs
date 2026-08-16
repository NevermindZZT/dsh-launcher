using System.Drawing;

namespace DshLauncher;

/// <summary>列表行数据（多列单元格 + 可选 Tag）。</summary>
public sealed record ListViewRow(string[] Cells, object? Tag = null);

/// <summary>
/// 自绘多列列表控件（替代 WinForms ListView —— ListView OwnerDraw 的焦点/选中交互不可控，
/// 导致焦点乱跳/失焦无响应）。深色主题：列头全宽、行选中高亮、自绘滚动条、滚轮/点击/双击。
/// </summary>
public sealed class ThemedListView : Control
{
    private readonly List<string> _headers = new();
    private readonly List<float> _weights = new();
    private readonly List<ListViewRow> _rows = new();
    private int _scrollOffset;
    private int _selectedIndex = -1;
    private const int RowHeight = 36;
    private const int HeaderHeight = 34;
    private const int ScrollbarWidth = 10;

    public event Action<int>? SelectionChanged;
    public event Action<int>? ItemActivated; // 双击

    /// <summary>配色（跟随主题）。</summary>
    public Color Surface { get; set; } = Color.FromArgb(0x2D, 0x2D, 0x30);
    public Color SurfaceAlt { get; set; } = Color.FromArgb(0x25, 0x25, 0x26);
    public Color Text { get; set; } = Color.FromArgb(0xF0, 0xF0, 0xF0);
    public Color Accent { get; set; } = Color.FromArgb(0x4C, 0xC2, 0xFF);
    public Color Scrollbar { get; set; } = Color.FromArgb(0x4A, 0x4D, 0x52);

    public int SelectedIndex => _selectedIndex;
    public ListViewRow? SelectedRow => _selectedIndex >= 0 && _selectedIndex < _rows.Count ? _rows[_selectedIndex] : null;
    public IReadOnlyList<ListViewRow> Rows => _rows;

    public ThemedListView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    /// <summary>设置列（标题 + 宽度比例，比例和约等于 1）。</summary>
    public void SetColumns(params (string Title, float Weight)[] columns)
    {
        _headers.Clear();
        _weights.Clear();
        foreach (var (title, weight) in columns) { _headers.Add(title); _weights.Add(weight); }
        Invalidate();
    }

    /// <summary>设置行数据。</summary>
    public void SetRows(IEnumerable<ListViewRow> rows)
    {
        _rows.Clear();
        _rows.AddRange(rows);
        _scrollOffset = 0;
        _selectedIndex = -1;
        Invalidate();
    }

    public void ClearRows() => SetRows(Array.Empty<ListViewRow>());

    private int VisibleRows => Math.Max(1, (ClientSize.Height - HeaderHeight) / RowHeight);
    private int MaxOffset => Math.Max(0, _rows.Count - VisibleRows);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        var listWidth = ClientSize.Width - ScrollbarWidth;

        // 列头（全宽背景）
        using var headerBrush = new SolidBrush(SurfaceAlt);
        g.FillRectangle(headerBrush, 0, 0, listWidth, HeaderHeight);
        var x = 0f;
        for (var c = 0; c < _headers.Count && c < _weights.Count; c++)
        {
            var w = _weights[c] * listWidth;
            TextRenderer.DrawText(g, _headers[c], Font, new Rectangle((int)x + 12, 0, (int)w - 12, HeaderHeight), Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            x += w;
        }

        // 行
        var visible = VisibleRows;
        for (var i = 0; i < visible; i++)
        {
            var idx = _scrollOffset + i;
            if (idx >= _rows.Count) break;
            var rowRect = new Rectangle(0, HeaderHeight + i * RowHeight, listWidth, RowHeight);
            if (idx == _selectedIndex)
            {
                using var sel = new SolidBrush(Color.FromArgb(80, Accent));
                g.FillRectangle(sel, rowRect);
            }
            // 各列文字（按比例）
            var cells = _rows[idx].Cells;
            var cx = 0f;
            for (var c = 0; c < _headers.Count && c < _weights.Count; c++)
            {
                var w = _weights[c] * listWidth;
                var cellText = c < cells.Length ? cells[c] : "";
                TextRenderer.DrawText(g, cellText, Font, new Rectangle((int)cx + 12, rowRect.Y, (int)w - 12, RowHeight), Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                cx += w;
            }
        }

        // 自绘滚动条
        if (_rows.Count > VisibleRows)
        {
            var track = new Rectangle(ClientSize.Width - ScrollbarWidth, HeaderHeight, ScrollbarWidth, ClientSize.Height - HeaderHeight);
            using var trackBrush = new SolidBrush(Color.FromArgb(30, 30, 33));
            g.FillRectangle(trackBrush, track);
            var thumbHeight = Math.Max(24, (int)(track.Height * (double)VisibleRows / Math.Max(1, _rows.Count)));
            var thumbY = track.Y + (int)((track.Height - thumbHeight) * (double)_scrollOffset / Math.Max(1, MaxOffset));
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
        if (e.Y < HeaderHeight) return;
        var row = (e.Y - HeaderHeight) / RowHeight;
        var idx = _scrollOffset + row;
        if (idx < _rows.Count && idx != _selectedIndex)
        {
            _selectedIndex = idx;
            Invalidate();
            SelectionChanged?.Invoke(idx);
        }
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        if (_selectedIndex >= 0 && _selectedIndex < _rows.Count) ItemActivated?.Invoke(_selectedIndex);
    }
}
