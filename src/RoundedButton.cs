using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DshLauncher;

/// <summary>Win11 风格的圆角扁平按钮：自绘圆角背景 + 悬停/按下/禁用状态。</summary>
public sealed class RoundedButton : Button
{
    public int CornerRadius { get; set; } = 6;

    private bool _hovered;
    private bool _pressed;
    private Color _windowBack = Color.FromArgb(0x1E, 0x1E, 0x1E);

    /// <summary>设置控件所在窗口的背景色（圆角外区域填充，避免下层内容透出虚影）。</summary>
    public void SetWindowBack(Color c) { _windowBack = c; Invalidate(); }

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        // 宽度按文字自适应（GrowOnly：显式设置的宽度只增不减），文字完整显示不使用省略号
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        MinimumSize = new Size(64, 34);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _hovered = false; _pressed = false; Invalidate(); };
        MouseDown += (_, _) => { _pressed = true; Invalidate(); };
        MouseUp += (_, _) => { _pressed = false; Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 先填充控件整个背景（圆角外区域不透出下层内容）
        using (var bg = new SolidBrush(_windowBack))
        {
            g.FillRectangle(bg, ClientRectangle);
        }

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(rect, CornerRadius);

        Color fill = BackColor;
        if (!Enabled) fill = Color.FromArgb(120, BackColor);
        else if (_pressed) fill = ThemeHelper.Darken(BackColor, 14);
        else if (_hovered) fill = ThemeHelper.Lighten(BackColor);

        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);

        var textColor = Enabled ? ForeColor : Color.FromArgb(120, ForeColor);
        TextRenderer.DrawText(g, Text, Font, rect, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
