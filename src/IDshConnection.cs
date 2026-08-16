namespace DshLauncher;

/// <summary>
/// dsh 连接抽象：本地（LocalConnection / HostSupervisor）或 SSH 远端（SshConnection）。
/// WebView 渲染、托盘、日志、插件管理、更新都只依赖此接口，按连接模式自动切换实现。
/// </summary>
public interface IDshConnection
{
    /// <summary>是否为 SSH 远程连接。</summary>
    bool IsRemote { get; }

    /// <summary>连接显示名（如 user@host）。</summary>
    string DisplayName { get; }

    /// <summary>当前可访问的 URL（本地为 http://127.0.0.1:端口；SSH 为本地转发端口）。</summary>
    string? CurrentUrl { get; }

    /// <summary>连接状态。</summary>
    HostState State { get; }

    /// <summary>日志文件路径（本地日志；SSH 模式也写入本地文件）。</summary>
    string LogFile { get; }

    event Action<HostState>? StateChanged;
    event Action<string>? LogLine;
    event Action<string>? Ready;
    event Action<string>? UnexpectedExit;

    /// <summary>启动连接并返回可加载的 URL。SSH 模式会连接服务器、启动远端 dsh、建立端口转发。</summary>
    Task<string> StartAsync(CancellationToken ct = default);

    /// <summary>停止：本地杀进程树；SSH 按配置停止远端 dsh 并断开连接。</summary>
    Task StopAsync();

    /// <summary>重启连接（SSH 模式重启远端 dsh）。</summary>
    Task<string> RestartAsync(CancellationToken ct = default);

    /// <summary>追加一行日志（写入日志文件并广播 LogLine，供 LogForm 显示）。</summary>
    void AppendLog(string line);

    /// <summary>执行 dsh plugin（本地或远端）—— add/remove/update 等，输出实时回传。</summary>
    Task<int> RunPluginAsync(string[] args, Action<string>? onOutput = null, CancellationToken ct = default);

    /// <summary>获取已安装的 dsh 版本（本地或远端）。</summary>
    Task<string?> GetInstalledVersionAsync();

    /// <summary>安装/更新 dsh（本地或远端 npm install -g），输出实时回传。</summary>
    Task<int> UpdateDshAsync(Action<string>? onOutput = null, CancellationToken ct = default);

    /// <summary>测试连接是否可用（SSH 模式：连接 + 探测远端 dsh；本地：探测本地端口）。</summary>
    Task<string?> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>把本地的 dsh 配置与已装插件同步到此连接（SSH：写入远端并安装插件；本地：无操作）。输出实时回传。</summary>
    Task<string> SyncFromLocalAsync(Action<string>? onOutput = null, CancellationToken ct = default);
}
