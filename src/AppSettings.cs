using System.Text.Json;

namespace DshLauncher;

/// <summary>启动器设置，JSON 持久化到 %LOCALAPPDATA%\DshLauncher\settings.json。</summary>
public sealed class AppSettings
{
    public ManagerSettings Manager { get; set; } = new();
    public int AttachPort { get; set; } = HostSupervisor.DefaultPort;
    public string? WorkingDirectory { get; set; }
    /// <summary>true = 关闭窗口即停止宿主退出；false = 关闭隐藏到托盘（默认）。</summary>
    public bool CloseExits { get; set; }
    /// <summary>开机自启（HKCU Run 键）。</summary>
    public bool AutoStart { get; set; }
    /// <summary>true = external links open in a new WebView2 window; false = system browser (default).</summary>
    public bool OpenLinksInWebView { get; set; }

    /// <summary>SSH 连接配置列表（多主机；本地连接始终存在，SSH 按 AutoConnect 并行连接）。</summary>
    public System.Collections.Generic.List<SshConnectionConfig> SshConnections { get; set; } = new();

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DshLauncher", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) return s;
            }
        }
        catch
        {
            // 损坏则回退默认
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存失败不致命
        }
    }

    /// <summary>同步开机自启注册表项。</summary>
    public void ApplyAutoStart()
    {
        try
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKey);
            if (key == null) return;
            if (AutoStart)
            {
                key.SetValue("DshLauncher", "\"" + Application.ExecutablePath + "\"");
            }
            else
            {
                key.DeleteValue("DshLauncher", false);
            }
        }
        catch
        {
            // 写入失败提示即可
        }
    }
}
