using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

/// <summary>WebView2 content host; native window framing owns all drag and resize behavior.</summary>
internal sealed class ShellWebView : WebView2
{
    protected override void WndProc(ref Message message)
    {
        if (BrowserMouseNavigationGuard.TryHandle(message.Msg, message.WParam, message.LParam, out var result))
        {
            message.Result = result;
            return;
        }
        base.WndProc(ref message);
    }
}
