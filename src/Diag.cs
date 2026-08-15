namespace DshLauncher;

/// <summary>启动诊断日志（环境变量 DSHLAUNCHER_DIAG=1 开启，写 %TEMP%\dshlauncher-diag.log）。</summary>
internal static class Diag
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("DSHLAUNCHER_DIAG"), "1", StringComparison.Ordinal);

    public static void Log(string msg)
    {
        if (!Enabled) return;
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "dshlauncher-diag.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志失败不致命
        }
    }
}
