using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshLauncher;

internal sealed class ResizeGripPanel : Panel
{
    private readonly int _hit;
    public ResizeGripPanel(int hit, Cursor cursor)
    {
        _hit = hit;
        Cursor = cursor;
        BackColor = Color.Transparent;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00080000;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetLayeredWindowAttributes(Handle, 0, 1, 0x2);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && FindForm() is Form form) WebShellBridge.BeginResize(form, _hit);
    }
}
