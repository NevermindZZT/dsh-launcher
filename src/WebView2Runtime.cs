using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace DshLauncher;

/// <summary>
/// WebView2 Runtime（Evergreen）检测与安装引导。
/// 用官方 CoreWebView2Environment API 检测（不依赖注册表），
/// Windows 11 已内置；Windows 10 多数设备已随 Edge 安装。缺失时引导官方 bootstrapper。
/// </summary>
internal static class WebView2Runtime
{
    private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>返回可用的 WebView2 Runtime 版本（如 "151.0.4129.78"），不可用返回 null。</summary>
    public static string? InstalledVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>确保 Runtime 可用；缺失时提示用户并引导安装。返回是否可用。</summary>
    public static bool EnsureInstalled(IWin32Window owner)
    {
        var version = InstalledVersion();
        if (version != null) return true;

        var res = MessageBox.Show(owner,
            "未检测到 Microsoft Edge WebView2 Runtime。\n\n" +
            "DshLauncher 需要 WebView2 Runtime 来渲染 dsh 界面（Windows 11 已内置，Windows 10 通常随 Edge 安装）。\n" +
            "是否打开微软官方下载页安装？（引导安装程序约 1.5 MB）",
            "需要 WebView2 Runtime", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (res == DialogResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo(BootstrapperUrl) { UseShellExecute = true });
            }
            catch
            {
                // 打开失败不影响退出
            }
        }
        return false;
    }
}
