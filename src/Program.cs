using System.Threading;

namespace DshLauncher;

internal static class Program
{
    /// <summary>单实例互斥体：防止第二个实例并发 spawn dsh（会因 cordis.yml 重写而 EPERM 冲突）。</summary>
    private const string MutexName = "Local\\DshLauncher_SingleInstance";
    /// <summary>通知已有实例显示主窗口的事件名。</summary>
    private const string ShowEventName = "Local\\DshLauncher_ShowWindow";

    [STAThread]
    private static void Main()
    {
        Diag.Log("Program.Main start");
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        Diag.Log($"mutex createdNew={createdNew}");
        if (!createdNew)
        {
            // 已有实例在运行：通知它显示主窗口，然后本实例退出（不再弹提示）
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowEventName);
                evt.Set();
            }
            catch
            {
                // 事件不可用（老版本实例等），静默
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Diag.Log("ApplicationConfiguration.Initialize done");
        Application.Run(new MainForm());
        Diag.Log("Application.Run returned");
    }
}
