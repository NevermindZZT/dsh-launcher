using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

internal sealed class ShellWebView : WebView2
{
    protected override void WndProc(ref Message m)
    {
        var form = FindForm();
        if (form != null)
        {
            if (m.Msg == 0x203 || m.Msg == 0x201)
            {
                var point = form.PointToClient(Cursor.Position);
                if (point.Y < 32 && point.X < form.ClientSize.Width - 300)
                {
                    if (m.Msg == 0x203) WebShellBridge.ToggleMaximize(form);
                    else WebShellBridge.BeginDrag(form);
                    return;
                }
            }
            if (m.Msg == 0x84)
            {
                var hit = WebShellBridge.ResizeHitTest(form, form.PointToClient(Cursor.Position));
                if (hit != 0)
                {
                    m.Result = (IntPtr)hit;
                    return;
                }
            }
        }
        base.WndProc(ref m);
    }
}
