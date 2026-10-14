using System.Windows.Forms;

namespace DshLauncher;

/// <summary>
/// Filters queued browser back/forward inputs before they reach any Launcher
/// window or WebView child. WndProc guards remain in place for sent messages.
/// </summary>
internal sealed class BrowserMouseNavigationMessageFilter : IMessageFilter
{
    public bool PreFilterMessage(ref Message m)
        => BrowserMouseNavigationGuard.TryHandle(m.Msg, m.WParam, m.LParam, out _);
}
