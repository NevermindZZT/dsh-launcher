using System.Drawing;

namespace DshLauncher;

/// <summary>深色主题自绘垂直滚动条（替代系统浅色滚动条，用于日志/列表等 WinForms 控件）。</summary>
public sealed class ThemedScrollBar : Control
{
    private int _maximum = 0;
    private int _largeChange = 1;
    private int _value = 0;
    private bool _dragging;
    private int _dragStartY, _dragStartValue;

    public int Maximum { get => _maximum; set { _maximum = Math.Max(0, value); ClampValue(); Invalidate(); } }
    public int LargeChange { get => _largeChange; set { _largeChange = Math.Max(1, value); ClampValue(); Invalidate(); } }
    public int Value
    {
        get => _value;
        set { var nv = Math.Clamp(value, 0, MaxOffset); if (nv != _value) { _value = nv; Invalidate(); ValueChanged?.Invoke(_value); } }
    }
    public event Action<int>? ValueChanged;

    private int MaxOffset => Math.Max(0, _maximum - _largeChange);

    /// <summary>配色（跟随主题）。</summary>
    public Color TrackColor { get; set; } = Color.FromArgb(0x1E, 0x20, 0x24);
    public Color ThumbColor { get; set; } = Color.FromArgb(0x4A, 0x4D, 0x52);

    public ThemedScrollBar()
    {
        Width = 10;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    private void ClampValue()
    {
        var nv = Math.Clamp(_value, 0, MaxOffset);
        if (nv != _value) { _value = nv; ValueChanged?.Invoke(_value); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var trackBrush = new SolidBrush(TrackColor);
        g.FillRectangle(trackBrush, ClientRectangle);
        if (MaxOffset <= 0) return;
        var thumbHeight = Math.Max(20, (int)(ClientSize.Height * (double)_largeChange / Math.Max(1, _maximum)));
        var thumbY = (int)((ClientSize.Height - thumbHeight) * (double)_value / MaxOffset);
        using var thumbBrush = new SolidBrush(ThumbColor);
        g.FillRectangle(thumbBrush, 2, thumbY, Width - 4, thumbHeight);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Value -= (e.Delta > 0 ? 1 : -1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _dragging = true;
        _dragStartY = e.Y;
        _dragStartValue = _value;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || MaxOffset <= 0 || ClientSize.Height <= 0) return;
        var thumbHeight = Math.Max(20, (int)(ClientSize.Height * (double)_largeChange / Math.Max(1, _maximum)));
        var travel = Math.Max(1, ClientSize.Height - thumbHeight);
        Value = _dragStartValue + (int)((e.Y - _dragStartY) * (double)MaxOffset / travel);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }
}
