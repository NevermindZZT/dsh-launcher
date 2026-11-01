namespace DshLauncher;

/// <summary>启动命令行参数与运行选项解析。</summary>
public sealed class LauncherStartupOptions
{
    public const string ModeLocal = "local";
    public const string ModeSsh = "ssh";
    public const string ModePicker = "picker";
    public const string ModeTray = "tray";

    /// <summary>命令行覆盖的启动模式（"local", "ssh", "picker", "tray"；null 表示遵从 AppSettings）。</summary>
    public string? ModeOverride { get; set; }

    /// <summary>命令行指定的 SSH 远程连接目标（如主机名、显示名；null 表示遵从 AppSettings）。</summary>
    public string? TargetConnectionName { get; set; }

    public static LauncherStartupOptions Parse(string[]? args)
    {
        var options = new LauncherStartupOptions();
        if (args == null || args.Length == 0) return options;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            if (arg.Equals("--local", StringComparison.OrdinalIgnoreCase))
            {
                options.ModeOverride = ModeLocal;
            }
            else if (arg.Equals("--picker", StringComparison.OrdinalIgnoreCase))
            {
                options.ModeOverride = ModePicker;
            }
            else if (arg.Equals("--tray", StringComparison.OrdinalIgnoreCase))
            {
                options.ModeOverride = ModeTray;
            }
            else if (arg.Equals("--ssh", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("--remote", StringComparison.OrdinalIgnoreCase))
            {
                options.ModeOverride = ModeSsh;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                {
                    options.TargetConnectionName = args[++i].Trim('"', '\'');
                }
            }
            else if (arg.StartsWith("--ssh=", StringComparison.OrdinalIgnoreCase) ||
                     arg.StartsWith("--remote=", StringComparison.OrdinalIgnoreCase))
            {
                options.ModeOverride = ModeSsh;
                var eq = arg.IndexOf('=');
                if (eq >= 0 && eq < arg.Length - 1)
                {
                    options.TargetConnectionName = arg[(eq + 1)..].Trim('"', '\'');
                }
            }
        }
        return options;
    }
}
