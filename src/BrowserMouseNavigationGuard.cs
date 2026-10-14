namespace DshLauncher;

/// <summary>
/// Consumes browser back/forward input before WebView2 or the parent form can
/// reinterpret it as a window command. This launcher intentionally makes mouse
/// side-button navigation a no-op so a page cannot close its DSH host session.
/// </summary>
internal static class BrowserMouseNavigationGuard
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmXButtonDoubleClick = 0x020D;
    private const int WmAppCommand = 0x0319;
    private const int XButton1 = 0x0001;
    private const int XButton2 = 0x0002;
    private const int VkBrowserBack = 0xA6;
    private const int VkBrowserForward = 0xA7;
    private const int AppCommandBrowserBackward = 1;
    private const int AppCommandBrowserForward = 2;

    public static bool IsBrowserNavigationKey(int virtualKey)
        => virtualKey is VkBrowserBack or VkBrowserForward;

    /// <summary>
    /// Returns true only for browser back/forward inputs. Mouse and app-command
    /// messages return TRUE (1) as required by Win32, preventing re-dispatch;
    /// virtual-key messages are consumed without forwarding them to WebView2.
    /// </summary>
    public static bool TryHandle(int messageId, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;

        if (messageId is WmXButtonDown or WmXButtonUp or WmXButtonDoubleClick)
        {
            var button = (int)((wParam.ToInt64() >> 16) & 0xffff);
            if (button is XButton1 or XButton2)
            {
                result = new IntPtr(1);
                return true;
            }
        }
        else if (messageId == WmAppCommand)
        {
            var command = (int)((lParam.ToInt64() >> 16) & 0x7f);
            if (command is AppCommandBrowserBackward or AppCommandBrowserForward)
            {
                result = new IntPtr(1);
                return true;
            }
        }
        else if (messageId is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
        {
            var virtualKey = (int)(wParam.ToInt64() & 0xffff);
            if (IsBrowserNavigationKey(virtualKey))
                return true;
        }

        return false;
    }
}
