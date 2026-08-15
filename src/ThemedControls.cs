using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DshLauncher;

/// <summary>WinUI 3 风格自绘复选框：圆角方块 + 强调色勾选，深色模式不再出现白色系统控件。</summary>
public sealed class ThemedCheckBox : CheckBox
{
    private ThemeHelper.Palette _p = ThemeHelper.GetPalette(ThemeHelper.IsSystemDarkMode());

    public ThemedCheckBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // AutoSize=true 按文字完整展开宽度（避免长文字截断）；背景用窗口背景色（防重绘残影）
        AutoSize = true;
    }

    public void ApplyPalette(ThemeHelper.Palette p) { _p = p; BackColor = p.WindowBack; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 先填充窗口背景色，清除重绘区域（UserPaint 控件必须自画背景，否则残影重叠）
        using (var bg = new SolidBrush(_p.WindowBack))
        {
            g.FillRectangle(bg, ClientRectangle);
        }
        var box = new Rectangle(0, (Height - 18) / 2, 18, 18);
        using (var path = RoundedButton.RoundedRect(box, 4))
        {
            using var fill = new SolidBrush(Checked ? _p.Accent : Color.Transparent);
            g.FillPath(fill, path);
            using var pen = new Pen(Checked ? _p.Accent : _p.Border);
            g.DrawPath(pen, path);
        }
        if (Checked)
        {
            using var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, new[] { new Point(box.X + 4, box.Y + 9), new Point(box.X + 8, box.Y + 13), new Point(box.X + 14, box.Y + 5) });
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(26, 0, Width - 26, Height), _p.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>WinUI 3 风格自绘单选按钮：圆形 + 强调色圆点，适配深色主题。</summary>
public sealed class ThemedRadioButton : RadioButton
{
    private ThemeHelper.Palette _p = ThemeHelper.GetPalette(ThemeHelper.IsSystemDarkMode());

    public ThemedRadioButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // AutoSize=true 按文字完整展开宽度（避免长文字截断）；背景用窗口背景色（防重绘残影）
        AutoSize = true;
    }

    public void ApplyPalette(ThemeHelper.Palette p) { _p = p; BackColor = p.WindowBack; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 先填充窗口背景色，清除重绘区域（UserPaint 控件必须自画背景，否则残影重叠）
        using (var bg = new SolidBrush(_p.WindowBack))
        {
            g.FillRectangle(bg, ClientRectangle);
        }
        var cx = 9;
        var cy = Height / 2;
        var r = 9;
        using (var pen = new Pen(Checked ? _p.Accent : _p.Border, 1.6f))
        {
            g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        }
        if (Checked)
        {
            using var fill = new SolidBrush(_p.Accent);
            g.FillEllipse(fill, cx - 5, cy - 5, 10, 10);
            using var dot = new SolidBrush(Checked ? Color.White : _p.Text);
            g.FillEllipse(dot, cx - 2, cy - 2, 4, 4);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(26, 0, Width - 26, Height), _p.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
