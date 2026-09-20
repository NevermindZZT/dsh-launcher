using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

/// <summary>Blocks browser back/forward mouse commands so dsh cannot navigate away or close its host session.</summary>
internal static class BrowserMouseNavigationGuard
{
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmXButtonDoubleClick = 0x020D;
    private const int WmAppCommand = 0x0319;
    private const int XButton1 = 0x0001;
    private const int XButton2 = 0x0002;
    private const int AppCommandBrowserBackward = 1;
    private const int AppCommandBrowserForward = 2;

    public static bool TryHandle(ref Message message)
    {
        if (message.Msg is WmXButtonDown or WmXButtonUp or WmXButtonDoubleClick)
        {
            var button = (int)((message.WParam.ToInt64() >> 16) & 0xffff);
            if (button is XButton1 or XButton2)
            {
                message.Result = IntPtr.Zero;
                return true;
            }
        }
        else if (message.Msg == WmAppCommand)
        {
            var command = (int)((message.LParam.ToInt64() >> 16) & 0x7f);
            if (command is AppCommandBrowserBackward or AppCommandBrowserForward)
            {
                message.Result = IntPtr.Zero;
                return true;
            }
        }

        return false;
    }
}

/// <summary>WebView2 content host; native window framing owns all drag and resize behavior.</summary>
internal sealed class ShellWebView : WebView2
{
    protected override void WndProc(ref Message message)
    {
        if (BrowserMouseNavigationGuard.TryHandle(ref message)) return;
        base.WndProc(ref message);
    }
}
