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

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
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
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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
