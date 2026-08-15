using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DshLauncher;

/// <summary>旋转弧线加载动画（Win11 风格），深色/浅色主题自适配。</summary>
public sealed class LoadingSpinner : Control
{
    private readonly System.Windows.Forms.Timer _timer;
    private float _angle;
    private Color _accent = Color.FromArgb(0x4C, 0xC2, 0xFF);

    public LoadingSpinner()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _timer = new System.Windows.Forms.Timer { Interval = 30 };
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + 7) % 360;
            Invalidate();
        };
        _timer.Start();
    }

    /// <summary>设置强调色（跟随主题）。</summary>
    public void SetAccent(Color accent) { _accent = accent; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(4, 4, Math.Max(8, Width - 8), Math.Max(8, Height - 8));
        using var bg = new Pen(Color.FromArgb(46, 46, 52), 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(bg, rect);
        using var pen = new Pen(_accent, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, rect, _angle, 300);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
