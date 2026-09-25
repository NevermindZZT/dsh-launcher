using System.Diagnostics;
using System.Net;
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
public sealed class HostSupervisor : IDshConnection, IDisposable
{
    public const int DefaultPort = 3080;
    public const string ReadinessPrefix = "dsh web: ";
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(90);

    // 就绪行解析不再使用正则（历史上 \d 转义曾丢失导致端口永不匹配），
    // 改用 PumpStdout 里的 StartsWith + Uri 解析。

    private const int StartupDiagnosticMaxChars = 12_000;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly StringBuilder _startupDiagnostics = new();
    private Process? _process;
    private IntPtr _job = IntPtr.Zero;
    private bool _disposed;
    private string? _lastFailureDetails;

    public event Action<HostState>? StateChanged;
    public event Action<string>? LogLine;
    public event Action<string>? Ready;          // 就绪 URL（origin）
    public event Action<string>? UnexpectedExit; // 参数: 诊断文本

    public HostState State { get; private set; } = HostState.Stopped;
    public string? CurrentUrl { get; private set; }
    public bool IsAttached { get; private set; }

    public void SetStartupUrl(string url)
    {
        if (!DshStartupUrl.HasToken(url)) throw new ArgumentException("A valid DSH startup token URL is required.", nameof(url));
        lock (_gate)
        {
            if (State != HostState.Running) throw new InvalidOperationException("DSH is not running.");
            CurrentUrl = url;
        }
    }
    public string LogFile { get; }

    /// <summary>最近一次 dsh 启动/异常退出的详细诊断（包含 stdout/stderr 尾部）。</summary>
    public string? LastFailureDetails
    {
        get { lock (_gate) return _lastFailureDetails; }
    }

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
        await _startGate.WaitAsync(ct);
        try
        {
            Diag.Log("StartAsync begin");
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(HostSupervisor));
                if (State == HostState.Running && !string.IsNullOrWhiteSpace(CurrentUrl)) return CurrentUrl;
                if (State == HostState.Stopping) throw new InvalidOperationException("宿主正在停止，请稍后重试");
                CurrentUrl = null;
                IsAttached = false;
            }
            ResetStartupDiagnostics();
            SetState(HostState.Starting);

            var attached = await TryAttachExistingAsync(ct);
            if (attached != null)
            {
                IsAttached = true;
                CurrentUrl = attached;
                SetState(HostState.Running);
                Log("attach 到已有 dsh 实例: " + DshStartupUrl.Redact(attached));
                Ready?.Invoke(attached);
                return attached;
            }

            return await SpawnAsync(ct);
        }
        catch (OperationCanceledException)
        {
            if (State == HostState.Starting) SetState(HostState.Stopped);
            throw;
        }
        catch
        {
            if (State == HostState.Starting) SetState(HostState.Failed);
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    // ── IDshConnection（本地实现）──

    public bool IsRemote => false;

    public string DisplayName => "本地";

    /// <summary>执行 dsh plugin（本地）：复用 PluginManager 的命令转发（node + dsh plugin --profile web）。</summary>
    public Task<int> RunPluginAsync(string[] args, Action<string>? onOutput = null, CancellationToken ct = default)
        => new PluginManager().RunAsync(args, onOutput, ct);

    /// <summary>本地已安装 dsh 版本。</summary>
    public Task<string?> GetInstalledVersionAsync() => DshUpdater.GetInstalledVersionAsync();

    /// <summary>本地更新 dsh（npm install -g）。</summary>
    public Task<int> UpdateDshAsync(Action<string>? onOutput = null, CancellationToken ct = default)
        => DshUpdater.InstallOrUpdateAsync(onOutput, ct);

    /// <summary>测试本地连接：返回当前 URL 或默认端口探测结果。</summary>
    public async Task<string?> TestConnectionAsync(CancellationToken ct = default)
    {
        if (CurrentUrl != null) return CurrentUrl;
        return await TryAttachExistingAsync(ct) ?? "本地连接可用（尚未启动 dsh）";
    }

    /// <summary>本地是配置/插件的源头，无需同步。</summary>
    public Task<string> SyncFromLocalAsync(Action<string>? onOutput = null, CancellationToken ct = default)
        => Task.FromResult("本地连接无需同步（本地即配置/插件源头）");

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
            var message = "未找到 dsh 安装。\n请先安装 Node.js，然后执行：npm install -g @deepseek-ai/dsh";
            SetFailureDetails(message);
            SetState(HostState.Failed);
            throw new InvalidOperationException(message);
        }

        Log($"spawn: {nodeExe} --expose-internals \"{binJs}\" web --host 127.0.0.1 --port 0 --no-open");

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
        psi.ArgumentList.Add("--no-open");

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            var message = $"无法启动 dsh 进程：{ex.Message}";
            SetFailureDetails(message);
            SetState(HostState.Failed);
            throw new InvalidOperationException(message, ex);
        }
        if (_process == null)
        {
            const string message = "无法启动 dsh 进程";
            SetFailureDetails(message);
            SetState(HostState.Failed);
            throw new InvalidOperationException(message);
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
        var process = _process;
        Task stdoutPump = Task.Run(() => PumpStdout(process, readyTcs), CancellationToken.None);
        Task stderrPump = Task.Run(() => PumpStderr(process), CancellationToken.None);

        // 先启动输出管道，再注册退出事件，确保 dsh 的最后几行 stderr 能够进入失败提示。
        process.Exited += (_, _) => _ = CompleteProcessExitAsync(process, readyTcs, stdoutPump, stderrPump);
        process.EnableRaisingEvents = true;

        var timeout = Task.Delay(ReadyTimeout, ct);
        var done = await Task.WhenAny(readyTcs.Task, timeout);
        if (done == timeout)
        {
            _ = StopAsync();
            SetState(HostState.Failed);
            var message = BuildFailureDetails($"dsh 在 {ReadyTimeout.TotalSeconds}s 内未就绪");
            SetFailureDetails(message);
            throw new TimeoutException(message);
        }

        var url = await readyTcs.Task; // 失败会在此抛出
        CurrentUrl = url;
        SetState(HostState.Running);
        Log($"就绪: {RedactSensitiveUrl(url)}");
        Ready?.Invoke(url);
        return url;
    }

    private async Task CompleteProcessExitAsync(
        Process process,
        TaskCompletionSource<string> ready,
        Task stdoutPump,
        Task stderrPump)
    {
        try
        {
            await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CaptureStartupLine("[launcher] 读取 dsh 输出失败: " + ex.Message);
        }

        int? exitCode = null;
        try { exitCode = process.ExitCode; } catch { }
        var unexpected = false;
        lock (_gate)
        {
            unexpected = State is HostState.Starting or HostState.Running;
            if (unexpected) SetState(HostState.Failed);
        }
        // 正常 StopAsync 会先把状态切到 Stopping；不要把主动停止记录成异常退出。
        if (!unexpected && ready.Task.IsCompleted) return;

        var message = BuildFailureDetails("dsh 进程异常退出", exitCode);
        SetFailureDetails(message);
        Log(message);
        if (unexpected)
        {
            try { UnexpectedExit?.Invoke(message); } catch { }
        }
        if (!ready.Task.IsCompleted)
            ready.TrySetException(new InvalidOperationException(message));
    }

    private void ResetStartupDiagnostics()
    {
        lock (_gate)
        {
            _startupDiagnostics.Clear();
            _lastFailureDetails = null;
        }
    }

    private void SetFailureDetails(string message)
    {
        lock (_gate) _lastFailureDetails = message;
    }

    private void CaptureStartupLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            _startupDiagnostics.AppendLine(line);
            if (_startupDiagnostics.Length > StartupDiagnosticMaxChars)
            {
                var text = _startupDiagnostics.ToString();
                _startupDiagnostics.Clear();
                _startupDiagnostics.Append("…（前面的启动输出已省略）…\n");
                _startupDiagnostics.Append(text[^Math.Min(text.Length, StartupDiagnosticMaxChars - 32)..]);
            }
        }
    }

    private string BuildFailureDetails(string reason, int? exitCode = null)
    {
        string output;
        lock (_gate) output = _startupDiagnostics.ToString().Trim();
        var message = reason + (exitCode.HasValue ? $"（exit code {exitCode.Value}）" : "");
        if (output.Length > 0)
            message += "\n\ndsh 启动输出：\n" + output;
        else
            message += "\n\n未收到 dsh 输出，请打开「日志」查看完整记录。";
        message += "\n\n完整日志：" + LogFile;
        return message;
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
                var safeLine = RedactSensitiveUrl(line);
                CaptureStartupLine("[out] " + safeLine);
                Log("[out] " + safeLine);
                // 就绪行解析：前缀匹配 + URL 解析（不依赖正则转义，更鲁棒）
                if (!ready.Task.IsCompleted && line.StartsWith(ReadinessPrefix, StringComparison.Ordinal))
                {
                    var urlPart = line.Substring(ReadinessPrefix.Length).Trim();
                    var url = urlPart.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]; // 去掉 " (LAN: ...)" 后缀
                    if (Uri.TryCreate(url, UriKind.Absolute, out var u)
                        && u.Scheme == "http" && u.Host == "127.0.0.1" && u.Port > 0)
                    {
                        if (!DshStartupUrl.HasToken(url))
                            Log("DSH ready URL 未包含 startup token；先尝试复用现有 WebView 会话，若未认证将提示用户");
                        ready.TrySetResult(url);
                        Log("就绪行解析成功: " + RedactSensitiveUrl(url));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CaptureStartupLine("[launcher] stdout 读取失败: " + ex.Message);
            Log("[launcher] stdout 读取失败: " + ex.Message);
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
                var safeLine = RedactSensitiveUrl(line);
                CaptureStartupLine("[err] " + safeLine);
                Log("[err] " + safeLine);
            }
        }
        catch (Exception ex)
        {
            CaptureStartupLine("[launcher] stderr 读取失败: " + ex.Message);
            Log("[launcher] stderr 读取失败: " + ex.Message);
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

    private IReadOnlyList<int> GetAttachPortCandidates()
    {
        var ports = new List<int>();
        var seen = new HashSet<int>();
        void Add(int port)
        {
            if (port is > 0 and < 65536 && seen.Add(port)) ports.Add(port);
        }

        Add(AttachPort);
        Add(DefaultPort);
        foreach (var path in new[] { LogFile, LogFile + ".1" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path).TakeLast(200).Reverse())
                {
                    if (!line.Contains("dsh web:", StringComparison.OrdinalIgnoreCase)
                        && !line.Contains("就绪", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (Match match in Regex.Matches(line, @"https?://127\.0\.0\.1:(?<port>\d+)", RegexOptions.IgnoreCase))
                    {
                        if (int.TryParse(match.Groups["port"].Value, out var port)) Add(port);
                    }
                }
            }
            catch { /* stale or unreadable logs are not an attach failure */ }
        }
        return ports;
    }

    /// <summary>探测常用端口和最近由 launcher 启动的端口；命中已认证/需 Cookie 的 DSH 时复用，不再 spawn 第二个实例。</summary>
    private async Task<string?> TryAttachExistingAsync(CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        foreach (var port in GetAttachPortCandidates())
        {
            var url = $"http://127.0.0.1:{port}";
            try
            {
                using var resp = await client.GetAsync(url + "/", ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!DshStartupUrl.IsDshResponse(resp.StatusCode, body))
                {
                    Log($"attach 探测 {url}: HTTP {(int)resp.StatusCode}，无 DSH 标记");
                    continue;
                }

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    Log($"attach 探测 {url}: 检测到需要浏览器认证的已有 DSH；尝试复用 WebView Cookie，不启动第二个实例");
                else
                    Log($"attach 探测 {url}: 已有 DSH 就绪，复用实例");
                return url;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"attach 探测 {url}: 不可达（{ex.GetType().Name}）");
            }
        }
        return null;
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

    private static string RedactSensitiveUrl(string value)
    {
        var start = value.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        if (start < 0) start = value.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        var candidate = start >= 0 ? value[start..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0] : value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query)) return value;
        var redacted = uri.GetLeftPart(UriPartial.Path) + "?[redacted]";
        return start >= 0 ? value[..start] + redacted : redacted;
    }

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
