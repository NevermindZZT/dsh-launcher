using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DshLauncher;

/// <summary>Win11 风格的圆角输入框：圆角 Surface 背景容器 + 内嵌无边框 TextBox（垂直居中）。</summary>
public sealed class InputBox : Panel
{
    private int _cornerRadius = 6;
    private Color _windowBack = Color.FromArgb(0x1E, 0x1E, 0x1E);

    public TextBox Inner { get; }

    /// <summary>设置控件所在窗口的背景色（圆角外区域填充，避免下层内容透出虚影）。</summary>
    public void SetWindowBack(Color c) { _windowBack = c; Invalidate(); }

    public int CornerRadius
    {
        get => _cornerRadius;
        set { _cornerRadius = value; Invalidate(); }
    }

    public InputBox(int height = 34, string? placeholder = null)
    {
        Height = height;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Inner = new TextBox { BorderStyle = BorderStyle.None };
        Inner.Left = 12;
        Inner.Top = Math.Max(0, (Height - Inner.PreferredHeight) / 2);
        if (placeholder != null) Inner.PlaceholderText = placeholder;
        Controls.Add(Inner);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Inner == null) return;
        Inner.Width = Math.Max(40, ClientSize.Width - 24);
        Inner.Top = Math.Max(0, (ClientSize.Height - Inner.PreferredHeight) / 2);
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
        using var path = RoundedButton.RoundedRect(rect, _cornerRadius);
        using var brush = new SolidBrush(BackColor);
        g.FillPath(brush, path);
    }
}
