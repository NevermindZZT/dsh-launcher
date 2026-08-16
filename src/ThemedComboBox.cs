using System.Drawing;
using System.Drawing.Drawing2D;

namespace DshLauncher;

/// <summary>
/// 自绘深色下拉框（替代 WinForms ComboBox —— 系统 ComboBox 下拉按钮/边框无法主题化）。
/// 圆角外填充窗口背景（无浅色残留）、圆角背景 + 边框、Label 垂直居中、按钮内缩圆角内。
/// </summary>
public sealed class ThemedComboBox : Control
{
    private readonly List<object> _items = new();
    private int _selectedIndex = -1;
    private readonly Label _display = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, Cursor = Cursors.Hand, Padding = new Padding(12, 0, 0, 0) };
    private readonly Panel _btn = new() { Cursor = Cursors.Hand };

    public Color Surface { get; set; } = Color.FromArgb(0x2D, 0x2D, 0x30);
    public Color Text { get; set; } = Color.FromArgb(0xF0, 0xF0, 0xF0);
    public Color Border { get; set; } = Color.FromArgb(0x3F, 0x3F, 0x46);
    public Color ButtonColor { get; set; } = Color.FromArgb(0x25, 0x25, 0x26);
    public Color WindowBack { get; set; } = Color.FromArgb(0x1E, 0x1E, 0x1E);

    public int SelectedIndex
    {
        get => _selectedIndex;
        set { _selectedIndex = value; UpdateDisplay(); Invalidate(); }
    }

    public object? SelectedItem => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;
    public int ItemCount => _items.Count;
    public event EventHandler? SelectedIndexChanged;

    public ThemedComboBox()
    {
        Height = 40;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _display.Dock = DockStyle.Fill;
        _display.Click += (_, _) => ShowPopup();
        _btn.Click += (_, _) => ShowPopup();
        _btn.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(ButtonColor);
            g.FillRectangle(bg, _btn.ClientRectangle);
            var cx = _btn.Width / 2;
            var cy = _btn.Height / 2;
            using var pen = new Pen(Text, 1.6f);
            g.DrawLine(pen, cx - 5, cy - 2, cx, cy + 3);
            g.DrawLine(pen, cx, cy + 3, cx + 5, cy - 2);
        };
        Controls.Add(_btn);
        Controls.Add(_display);
        Resize += (_, _) => PositionButton();
        PositionButton();
    }

    /// <summary>下拉按钮内缩到圆角内（不覆盖圆角边框）。</summary>
    private void PositionButton()
    {
        _btn.Location = new Point(Width - 31, 1);
        _btn.Size = new Size(30, Math.Max(1, Height - 2));
    }

    public void SetItems(IEnumerable<object> items)
    {
        _items.Clear();
        _items.AddRange(items);
        _selectedIndex = _items.Count > 0 ? 0 : -1;
        UpdateDisplay();
        Invalidate();
    }

    private void UpdateDisplay()
    {
        _display.Text = SelectedItem?.ToString() ?? "";
        _display.ForeColor = Text;
    }

    /// <summary>弹出深色下拉列表（ContextMenuStrip + ThemeToolStripRenderer）。</summary>
    private void ShowPopup()
    {
        if (_items.Count == 0) return;
        var menu = new ContextMenuStrip { Renderer = new ThemeToolStripRenderer() };
        for (var i = 0; i < _items.Count; i++)
        {
            var idx = i;
            var mi = new ToolStripMenuItem(_items[i].ToString()) { Checked = i == _selectedIndex };
            mi.Click += (_, _) =>
            {
                if (idx != _selectedIndex) { _selectedIndex = idx; UpdateDisplay(); Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
            };
            menu.Items.Add(mi);
        }
        menu.Show(this, new Point(0, Height + 1));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 圆角外填窗口背景（无浅色残留）
        using var wb = new SolidBrush(WindowBack);
        g.FillRectangle(wb, ClientRectangle);
        // 圆角背景
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedButton.RoundedRect(r, 4);
        using var bg = new SolidBrush(Surface);
        g.FillPath(bg, path);
        // 圆角边框
        using var pen = new Pen(Border);
        g.DrawPath(pen, path);
    }
}
