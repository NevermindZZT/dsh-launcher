using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DshLauncher;

public enum HostState
{
    Stopped,   // 未运行
    Starting,  // 启动中（等待就绪行）
    Running,   // 就绪（URL 已知）
    Stopping,  // 停止中
    Failed,    // 启动失败或异常退出
}

/// <summary>
/// dsh web 宿主进程管理器。
/// 两种模式：spawn（node &lt;bin.js&gt; web --host 127.0.0.1 --port 0，解析 stdout 就绪行）
/// 与 attach（已有 dsh 实例在默认端口健康时直接连接，不拥有进程）。
/// 终止使用 Job Object（KILL_ON_JOB_CLOSE + TerminateJobObject），回退 Process.Kill(树)。
/// stdout/stderr 实时追加到 %LOCALAPPDATA%\DshLauncher\logs\dsh-web.log 并广播 LogLine。
/// </summary>
public sealed class HostSupervisor : IDisposable
{
    public const int DefaultPort = 3080;
    public const string ReadinessPrefix = "dsh web: ";
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(90);

    // 就绪行解析不再使用正则（历史上 \d 转义曾丢失导致端口永不匹配），
    // 改用 PumpStdout 里的 StartsWith + Uri 解析。

    private readonly object _gate = new();
    private Process? _process;
    private IntPtr _job = IntPtr.Zero;
    private bool _disposed;

    public event Action<HostState>? StateChanged;
    public event Action<string>? LogLine;
    public event Action<string>? Ready;          // 就绪 URL（origin）
    public event Action<string>? UnexpectedExit; // 参数: 诊断文本

    public HostState State { get; private set; } = HostState.Stopped;
    public string? CurrentUrl { get; private set; }
    public bool IsAttached { get; private set; }
    public string LogFile { get; }

    public HostSupervisor()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DshLauncher", "logs");
        Directory.CreateDirectory(dir);
        LogFile = Path.Combine(dir, "dsh-web.log");
        Diag.Log("HostSupervisor ctor, LogFile=" + LogFile);
        // 日志轮转：超过 1MB 时把旧日志滚动为 .1
        try
        {
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 1_000_000)
            {
                File.Move(LogFile, LogFile + ".1", overwrite: true);
            }
        }
        catch
        {
            // 轮转失败不致命
        }
    }

    // ─────────────────────────── 公共 API ───────────────────────────

    /// <summary>启动宿主：优先 attach 已有实例，否则 spawn 新进程。返回就绪 URL。</summary>
    public async Task<string> StartAsync(CancellationToken ct = default)
    {
        Diag.Log("StartAsync begin");
        lock (_gate)
        {
            if (State is HostState.Starting or HostState.Running or HostState.Stopping)
                return CurrentUrl ?? throw new InvalidOperationException("宿主已在运行");
        }

        var attached = await TryAttachExistingAsync(ct);
        if (attached != null)
        {
            IsAttached = true;
            CurrentUrl = attached;
            SetState(HostState.Running);
            Log($"attach 到已有 dsh 实例: {attached}");
            Ready?.Invoke(attached);
            return attached;
        }

        IsAttached = false;
        return await SpawnAsync(ct);
    }

    /// <summary>停止宿主：attach 模式仅断开；spawn 模式终止整棵进程树。</summary>
    public async Task StopAsync()
    {
        Process? p;
        IntPtr job;
        lock (_gate)
        {
            if (IsAttached)
            {
                IsAttached = false;
                CurrentUrl = null;
                SetState(HostState.Stopped);
                Log("已断开 attach 的实例（未终止外部进程）");
                return;
            }
            p = _process;
            job = _job;
            if (p == null && job == IntPtr.Zero)
            {
                SetState(HostState.Stopped);
                return;
            }
            SetState(HostState.Stopping);
        }

        try
        {
            if (job != IntPtr.Zero)
            {
                TerminateJobObject(job, 0);
                Log("已终止宿主进程树 (Job Object)");
            }
            else if (p != null)
            {
                p.Kill(entireProcessTree: true);
                Log("已终止宿主进程树 (Process.Kill)");
            }
            if (p != null)
            {
                await Task.Run(() => p.WaitForExit(5000));
            }
        }
        catch (Exception ex)
        {
            Log("停止宿主时异常: " + ex.Message);
        }
        finally
        {
            if (job != IntPtr.Zero) { CloseHandle(job); _job = IntPtr.Zero; }
            _process = null;
            SetState(HostState.Stopped);
            Log("宿主已停止");
        }
    }

    /// <summary>重启宿主（Stop 后重新 Start）。</summary>
    public async Task<string> RestartAsync(CancellationToken ct = default)
    {
        await StopAsync();
        return await StartAsync(ct);
    }

    // ─────────────────────────── spawn 路径 ───────────────────────────

    private async Task<string> SpawnAsync(CancellationToken ct)
    {
        var (nodeExe, binJs) = ResolveDshPaths();
        if (nodeExe == null || binJs == null)
        {
            SetState(HostState.Failed);
            throw new InvalidOperationException(
                "未找到 dsh 安装。\n请先安装 Node.js，然后执行：npm install -g @deepseek-ai/dsh");
        }

        SetState(HostState.Starting);
        Log($"spawn: {nodeExe} --expose-internals \"{binJs}\" web --host 127.0.0.1 --port 0");

        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--expose-internals");
        psi.ArgumentList.Add(binJs);
        psi.ArgumentList.Add("web");
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add("0");

        _process = Process.Start(psi);
        if (_process == null)
        {
            SetState(HostState.Failed);
            throw new InvalidOperationException("无法启动 dsh 进程");
        }
        Log($"PID = {_process.Id}");

        // Job Object：进程退出时整棵树被清理（KILL_ON_JOB_CLOSE）
        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job != IntPtr.Zero)
        {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info,
                (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
            if (!AssignProcessToJobObject(_job, _process.Handle))
            {
                Log("AssignProcessToJobObject 失败（进程已在其它 Job），回退 Process.Kill 树终止");
                CloseHandle(_job);
                _job = IntPtr.Zero;
            }
        }

        var readyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            lock (_gate)
            {
                if (State is HostState.Starting or HostState.Running)
                {
                    var code = _process?.ExitCode;
                    SetState(HostState.Failed);
                    Log($"宿主进程异常退出 (code={code})");
                    UnexpectedExit?.Invoke($"宿主进程异常退出 (code={code})");
                }
            }
            readyTcs.TrySetException(new IOException($"宿主进程在就绪前退出 (code={_process?.ExitCode})"));
        };

        _ = Task.Run(() => PumpStdout(_process, readyTcs), CancellationToken.None);
        _ = Task.Run(() => PumpStderr(_process), CancellationToken.None);

        var timeout = Task.Delay(ReadyTimeout, ct);
        var done = await Task.WhenAny(readyTcs.Task, timeout);
        if (done == timeout)
        {
            _ = StopAsync();
            SetState(HostState.Failed);
            throw new TimeoutException($"dsh 在 {ReadyTimeout.TotalSeconds}s 内未就绪");
        }

        var url = await readyTcs.Task; // 失败会在此抛出
        CurrentUrl = url;
        SetState(HostState.Running);
        Log($"就绪: {url}");
        Ready?.Invoke(url);
        return url;
    }

    private async Task PumpStdout(Process p, TaskCompletionSource<string> ready)
    {
        try
        {
            using var reader = p.StandardOutput;
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break; // EOF
                line = line.TrimEnd('\r');
                Log("[out] " + line);
                // 就绪行解析：前缀匹配 + URL 解析（不依赖正则转义，更鲁棒）
                if (!ready.Task.IsCompleted && line.StartsWith(ReadinessPrefix, StringComparison.Ordinal))
                {
                    var urlPart = line.Substring(ReadinessPrefix.Length).Trim();
                    var url = urlPart.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]; // 去掉 " (LAN: ...)" 后缀
                    if (Uri.TryCreate(url, UriKind.Absolute, out var u)
                        && u.Scheme == "http" && u.Host == "127.0.0.1" && u.Port > 0)
                    {
                        ready.TrySetResult(url);
                        Log("就绪行解析成功: " + url);
                    }
                }
            }
            if (!ready.Task.IsCompleted)
                ready.TrySetException(new IOException("宿主进程未输出就绪行即退出"));
        }
        catch (Exception ex)
        {
            if (!ready.Task.IsCompleted) ready.TrySetException(ex);
        }
    }

    private async Task PumpStderr(Process p)
    {
        try
        {
            using var reader = p.StandardError;
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                Log("[err] " + line);
            }
        }
        catch
        {
            // 日志通道失败不致命
        }
    }

    // ─────────────────────────── attach 路径 ───────────────────────────

    private int _attachPort = DefaultPort;

    /// <summary>attach 探测端口（默认 3080；环境变量 DSHLAUNCHER_ATTACH_PORT 优先，用于测试/多实例）。</summary>
    public int AttachPort
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DSHLAUNCHER_ATTACH_PORT");
            if (int.TryParse(v, out var p) && p is > 0 and < 65536) return p;
            return _attachPort;
        }
        set => _attachPort = value is > 0 and < 65536 ? value : DefaultPort;
    }

    /// <summary>宿主进程工作目录（默认用户主目录）。</summary>
    public string WorkingDirectory { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>探测 attach 端口是否已有 dsh 实例在服务（HTTP 200 + 精确 dsh 标记 + 非空响应）。</summary>
    private async Task<string?> TryAttachExistingAsync(CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{AttachPort}";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await client.GetAsync(url + "/", ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"attach 探测 {url}: HTTP {(int)resp.StatusCode} -> 启动新实例");
                return null;
            }
            var html = await resp.Content.ReadAsStringAsync(ct);
            // 收紧判定：仅 dsh 专属标记（__DSH_BOOT__ 由宿主注入；DeepSeek Harness 为页面标题），
            // 且响应非空 —— 避免把端口残留 / 半死服务 / 其他应用的空页面误判为 dsh
            var hit = html.Length > 0
                && (html.Contains("__DSH_BOOT__", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("DeepSeek Harness", StringComparison.OrdinalIgnoreCase));
            Log(hit
                ? $"attach 探测 {url}: 发现 dsh 标记（{html.Length} 字节）-> 连接已有实例"
                : $"attach 探测 {url}: 无 dsh 标记 -> 启动新实例");
            return hit ? url : null;
        }
        catch (Exception ex)
        {
            Log($"attach 探测 {url}: 不可达（{ex.GetType().Name}）-> 启动新实例");
            return null; // 不可达/超时 → 无实例
        }
    }

    // ─────────────────────────── 路径解析 ───────────────────────────

    /// <summary>定位 node.exe 与 dsh 的 bin.js（npm 全局安装目录）。</summary>
    public static (string? NodeExe, string? BinJs) ResolveDshPaths()
    {
        string? nodeExe = null;
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cand = Path.Combine(dir.Trim('"'), "node.exe");
            if (File.Exists(cand)) { nodeExe = cand; break; }
        }

        string? binJs = null;
        // 1) 环境变量覆盖优先（测试/多实例场景）
        var envBin = Environment.GetEnvironmentVariable("DSH_BIN");
        if (!string.IsNullOrEmpty(envBin) && File.Exists(envBin))
        {
            binJs = envBin;
        }
        else
        {
            // 2) npm 全局默认目录（%APPDATA%\npm）
            var npmRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
            var cand1 = Path.Combine(npmRoot, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            if (File.Exists(cand1)) binJs = cand1;
        }

        // 3) where dsh 解析 shim（dsh.cmd 与 node_modules 同级）
        if (string.IsNullOrEmpty(binJs))
        {
            try
            {
                var psi = new ProcessStartInfo("where", "dsh")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var first = p.StandardOutput.ReadLine();
                    p.WaitForExit(3000);
                    if (!string.IsNullOrEmpty(first))
                    {
                        var shimDir = Path.GetDirectoryName(first);
                        var cand2 = Path.Combine(shimDir ?? "", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                        if (File.Exists(cand2)) binJs = cand2;
                    }
                }
            }
            catch { /* 忽略 */ }
        }

        return (nodeExe, binJs);
    }

    // ─────────────────────────── 状态 / 日志 ───────────────────────────

    private void SetState(HostState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>外部日志（如 npm 安装/更新输出）写入宿主日志文件并广播 LogLine（LogForm 实时显示）。</summary>
    public void AppendLog(string line) => Log(line);

    private void Log(string line)
    {
        var full = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
        try { File.AppendAllText(LogFile, full + Environment.NewLine); } catch { }
        LogLine?.Invoke(full);
    }

    // ─────────────────────────── Job Object P/Invoke ───────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, uint JobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_job != IntPtr.Zero) { TerminateJobObject(_job, 0); CloseHandle(_job); _job = IntPtr.Zero; }
        _process?.Dispose();
    }
}
