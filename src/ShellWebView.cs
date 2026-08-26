using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

internal sealed class ShellWebView : WebView2
{
    protected override void WndProc(ref Message m)
    {
        // 标题栏拖动/双击由 WebShell 的专属拖动区域处理；这里仅保留原生边缘缩放命中测试，
        // 避免下拉菜单位于标题栏附近时被误判为拖动或双击最大化。
        var form = FindForm();
        if (form != null && m.Msg == 0x84)
        {
            var hit = WebShellBridge.ResizeHitTest(form, form.PointToClient(Cursor.Position));
            if (hit != 0)
            {
                m.Result = (IntPtr)hit;
                return;
            }
        }
        base.WndProc(ref m);
    }
}
