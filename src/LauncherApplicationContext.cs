namespace DshLauncher;

/// <summary>
/// 应用程序级上下文：解耦应用生命周期与单一窗口。
/// 托盘图标和底层服务持续运行，本地窗口和 SSH 远程窗口均作为受管窗口。
/// </summary>
public sealed class LauncherApplicationContext : ApplicationContext
{
    private readonly MainForm _main;

    public MainForm MainFormInstance => _main;

    public LauncherApplicationContext(LauncherStartupOptions options)
    {
        _main = new MainForm(options, this);
    }

    public void RequestExit()
    {
        ExitThread();
    }
}
