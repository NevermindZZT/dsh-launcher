using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DshLauncher;

/// <summary>
/// SSH 远程连接（基于系统 OpenSSH，不集成 SSH 协议库）：
/// 通过 ssh -N -L 建立端口转发（远端 dsh 127.0.0.1:3080 → 本地），
/// 用 ssh 命令执行远端 dsh 启动（systemd/nohup）、插件管理、更新、重启。
/// </summary>
public sealed class SshConnection : IDshConnection, IDisposable
{
    private readonly SshConnectionConfig _config;
    private readonly SshRunner _runner;
    private Process? _tunnel;
    private string? _localUrl;
    private int _localPort;
    private bool _stopping;
    private bool _disposed;
    // 操作互斥锁：防止并发 Start/Restart（重复按键或重复触发导致多隧道/多启动）
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    public bool IsRemote => true;
    public SshConnectionConfig Config => _config;
    public string DisplayName => _config.DisplayName;
    public string? CurrentUrl => _localUrl;
    public HostState State { get; private set; }
    public string LogFile { get; }

    public event Action<HostState>? StateChanged;
    public event Action<string>? LogLine;
    public event Action<string>? Ready;
    public event Action<string>? UnexpectedExit;

    public SshConnection(SshConnectionConfig config)
    {
        _config = config;
        _runner = new SshRunner(config);
        LogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DshLauncher", "logs", "ssh-web.log");
    }

    /// <summary>本地端口是否空闲（可绑定）。</summary>
    private static bool IsPortFree(int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch { return false; }
    }

    /// <summary>找一个空闲的本地端口。</summary>
    private static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>本地探测转发端口是否已是可用的 dsh（HTTP 200 + dsh 标记）。</summary>
    private static async Task<bool> IsLocalReadyAsync(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await client.GetAsync($"http://127.0.0.1:{port}/");
            if (!resp.IsSuccessStatusCode) return false;
            var html = await resp.Content.ReadAsStringAsync();
            return html.Length > 0
                && (html.Contains("__DSH_BOOT__", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("DeepSeek Harness", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public async Task<string> StartAsync(CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct);
        try { return await StartCoreAsync(ct); }
        finally { _opLock.Release(); }
    }

    private async Task<string> StartCoreAsync(CancellationToken ct = default)
    {
        SetState(HostState.Starting);
        Log($"正在连接 {DisplayName} …");

        // 1) 测试 SSH 连接
        if (!_runner.TestConnection(out var detail))
        {
            SetState(HostState.Failed);
            Log($"SSH 连接失败: {detail}");
            throw new InvalidOperationException($"SSH 连接失败: {detail}");
        }
        Log($"SSH 已连接 {_config.Host}:{_config.Port}");

        // 2) 选本地端口并启动隧道：0 = 自动分配空闲端口；指定端口被占用也自动换（避免冲突）
        var localPort = _config.LocalPort > 0 ? _config.LocalPort : FindFreePort();
        if (!IsPortFree(localPort)) localPort = FindFreePort();
        _localPort = localPort;
        _tunnel = _runner.StartTunnel(localPort, _config.RemotePort);
        _tunnel.EnableRaisingEvents = true;
        _tunnel.Exited += (_, _) => _ = OnTunnelExitedAsync();
        Log($"隧道已建立: 127.0.0.1:{localPort} <- 远端 127.0.0.1:{_config.RemotePort} (pid {_tunnel.Id})");

        // 3) 等待本地转发端口就绪；未就绪则启动远端 dsh
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !await IsLocalReadyAsync(localPort))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct);
        }
        if (!await IsLocalReadyAsync(localPort))
        {
            Log("远端 dsh 未运行，正在启动…");
            try
            {
                var method = await Task.Run(() => RemoteDshManager.StartRemote(_runner, _config, Log), ct);
                Log($"远端启动方式: {method}");
            }
            catch (Exception ex)
            {
                Log("远端启动失败: " + ex.Message);
                _tunnel?.Kill(entireProcessTree: true);
                _tunnel = null;
                SetState(HostState.Failed);
                throw;
            }
        }

        // 4) 等待就绪（轮询本地转发端口）
        deadline = DateTime.UtcNow + ReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsLocalReadyAsync(localPort))
            {
                _localUrl = $"http://127.0.0.1:{localPort}";
                SetState(HostState.Running);
                Log($"远端 dsh 就绪: {_localUrl}");
                Ready?.Invoke(_localUrl);
                return _localUrl;
            }
            await Task.Delay(1000, ct);
        }

        _tunnel?.Kill(entireProcessTree: true);
        _tunnel = null;
        SetState(HostState.Failed);
        throw new TimeoutException($"远端 dsh 在 {ReadyTimeout.TotalSeconds}s 内未就绪");
    }

    /// <summary>隧道进程意外退出时的自动重连（最多 3 次，每次间隔递增）。</summary>
    private async Task OnTunnelExitedAsync()
    {
        if (_stopping || _disposed || _tunnel != null) return;
        Log("SSH 隧道断开，尝试自动重连…");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Task.Delay(2000 * attempt);
            if (_stopping || _disposed) return;
            try
            {
                _tunnel = _runner.StartTunnel(_localPort, _config.RemotePort);
                _tunnel.EnableRaisingEvents = true;
                _tunnel.Exited += (_, _) => _ = OnTunnelExitedAsync();
                if (await IsLocalReadyAsync(_localPort))
                {
                    Log("自动重连成功");
                    Ready?.Invoke(_localUrl ?? $"http://127.0.0.1:{_localPort}");
                    return;
                }
                Log($"重连第 {attempt} 次隧道已建但远端未就绪");
            }
            catch (Exception ex)
            {
                Log($"重连第 {attempt} 次失败: {ex.Message}");
            }
            try { _tunnel?.Kill(entireProcessTree: true); _tunnel?.Dispose(); } catch { }
            _tunnel = null;
        }
        Log("自动重连失败");
        if (!_stopping) UnexpectedExit?.Invoke("SSH 隧道断开且重连失败，请检查网络或服务器后手动重启");
    }

    public Task StopAsync()
    {
        _stopping = true;
        try
        {
            if (_config.StopRemoteOnClose)
            {
                Task.Run(() => RemoteDshManager.StopRemote(_runner, _config));
                Log("已请求停止远端 dsh");
            }
        }
        catch (Exception ex) { Log("停止远端 dsh 异常: " + ex.Message); }
        try { _tunnel?.Kill(entireProcessTree: true); } catch { }
        try { _tunnel?.Dispose(); } catch { }
        _tunnel = null;
        _localUrl = null;
        SetState(HostState.Stopped);
        Log("SSH 隧道已关闭");
        return Task.CompletedTask;
    }

    public async Task<string> RestartAsync(CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct);
        try
        {
            SetState(HostState.Stopping);
            Log("正在重启远端 dsh…");
            try { RemoteDshManager.StopRemote(_runner, _config); } catch (Exception ex) { Log("停止异常: " + ex.Message); }
            await Task.Delay(1000, ct);
            return await StartCoreAsync(ct);
        }
        finally { _opLock.Release(); }
    }

    // ── 远端能力（系统 ssh 执行）──
    /// <summary>
    /// 把本地的 dsh 配置（profiles/web 下的配置文件）与已装插件同步到服务器：
    /// 配置 base64 写入远端对应路径；插件逐一到远端 dsh plugin add（不用手动逐个安装）。
    /// 完成后需要重启远端 dsh 使插件生效（调用方处理）。
    /// </summary>

    /// <summary>列出远端目录的子目录（用于远端目录浏览器）。</summary>
    public List<string> ListRemoteDirectory(string path)
    {
        if (_tunnel == null) throw new InvalidOperationException("SSH 未连接");
        var expanded = string.IsNullOrWhiteSpace(path) ? "~" : path.Trim();
        var r = _runner.Exec($"ls -d {expanded}/*/ 2>/dev/null || echo", 30);
        var dirs = r.Split('\n')
            .Select(x => x.Trim().TrimEnd('/'))
            .Where(x => x.Length > 0 && !x.Contains("[exit") && x != "." && x != "..")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return dirs;
    }

    /// <summary>把选中的远端路径写入 dsh 工作区存储（~/.dsh/storages/workspace.json），刷新页面即可见。</summary>

    /// <summary>
    /// 通过 dsh RPC（HTTP POST /api/workspace.create）在远端创建 dsh 工作区 —— 走后端正常流程，无需重启。
    /// 协议（读 dsh-client-connection 源码）：{ type: client-request, rpcId, method: workspace.create, payload: { path } }
    /// 响应 { type: server-response, rpcId, result: { ok, value: { workspace, created } } }；loopback 信任免认证。
    /// </summary>
    public async Task<(bool ok, string? error)> CreateWorkspaceRpcAsync(string path, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        if (_localUrl == null) return (false, "SSH 未连接");
        try
        {
            var rpcId = Guid.NewGuid().ToString();
            var body = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "client-request",
                ["rpcId"] = rpcId,
                ["method"] = "workspace.create",
                ["payload"] = new System.Text.Json.Nodes.JsonObject { ["path"] = path },
            };
            onOutput?.Invoke($"RPC workspace.create → {path}");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var req = new HttpRequestMessage(HttpMethod.Post, _localUrl + "/api/workspace.create");
            req.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode}");
            var json = System.Text.Json.Nodes.JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var respId = json?["rpcId"]?.GetValue<string>();
            if (respId != rpcId) return (false, $"rpcId 不匹配（{respId}）");
            var ok = json?["result"]?["ok"]?.GetValue<bool>() ?? false;
            if (!ok)
            {
                var err = json?["result"]?["error"];
                return (false, err?.ToJsonString() ?? "RPC 返回失败");
            }
            onOutput?.Invoke("工作区已通过 RPC 创建");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
    public async Task<string> AddRemoteWorkspaceAsync(string path, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        if (_tunnel == null) throw new InvalidOperationException("SSH 未连接");
        var dshHome = _runner.Exec("echo ${DSH_HOME:-$HOME/.dsh}", 30).Trim();
        var wsFile = $"{dshHome}/storages/workspace.json";
        var raw = _runner.Exec($"cat {wsFile} 2>/dev/null || echo '{{}}'", 30);

        // 解析并追加工作区条目（对齐 dsh 格式：unit/global.workspaceIds/tables.workspaces）
        var root = (System.Text.Json.Nodes.JsonNode.Parse(raw) ?? new System.Text.Json.Nodes.JsonObject()).AsObject();
        var tables = root["tables"]?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
        var wsTable = tables["workspaces"]?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
        var global = root["global"]?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
        var ids = global["workspaceIds"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray();

        // 路径已存在则复用（不重复添加）
        foreach (var kv in wsTable)
        {
            if (kv.Value?["path"]?.GetValue<string>() == path)
            {
                onOutput?.Invoke($"工作区已存在: {path}");
                return path;
            }
        }

        var id = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var title = path.TrimEnd('/').Split('/').LastOrDefault() ?? path;
        wsTable[id] = new System.Text.Json.Nodes.JsonObject
        {
            ["path"] = path,
            ["title"] = title,
            ["sessionIds"] = new System.Text.Json.Nodes.JsonArray(),
            ["createdAt"] = now,
            ["updatedAt"] = now,
        };
        ids.Add(id);
        tables["workspaces"] = wsTable;
        root["tables"] = tables;
        global["workspaceIds"] = ids;
        root["global"] = global;
        if (root["unit"] == null)
        {
            root["unit"] = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "workspace",
                ["version"] = 2,
            };
        }

        // base64 写回远端
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));
        var cmd = $"mkdir -p {dshHome}/storages && echo {b64} | base64 -d > {wsFile} && echo ok";
        var r = _runner.Exec(cmd, 60);
        if (!r.Contains("ok")) throw new InvalidOperationException("写入工作区失败: " + r.Trim().Replace("\n", " "));
        onOutput?.Invoke($"已添加远端工作区: {path} ({title})");
        return path;
    }
    public async Task<string> SyncFromLocalAsync(Action<string>? onOutput = null, CancellationToken ct = default)
    {
        if (_tunnel == null)
            throw new InvalidOperationException("SSH 未连接，请先连接服务器");
        var sb = new System.Text.StringBuilder();
        var pm = new PluginManager();

        // 1) 配置同步：$DSH_HOME（~/.dsh）根下的 dsh 配置文件（settings.yaml / .credentials.yaml / pet.json），
        //    排除 storages（会话/工作区缓存，含本机绝对路径，同步会导致远端 dsh 启动异常）、
        //    profiles（插件区，由插件同步处理）、sessions / attachments（会话与附件数据）
        var localHome = pm.DshHome;
        var remoteHome = _runner.Exec("echo ${DSH_HOME:-$HOME/.dsh}", 30).Trim();
        var configFiles = new List<string>();
        if (Directory.Exists(localHome))
        {
            configFiles.AddRange(Directory.GetFiles(localHome));
        }
        onOutput?.Invoke($"同步配置 {localHome} → {remoteHome} …");
        var configCount = 0;
        foreach (var file in configFiles)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(localHome, file).Replace('\\', '/');
            if (rel.Contains("profiles", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("sessions", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("attachments", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("storages", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            var dir = Path.GetDirectoryName(rel) ?? "";
            var b64 = Convert.ToBase64String(File.ReadAllBytes(file));
            // 凭据文件必须 owner-only（600），否则远端 dsh 安全校验拒绝启动
            var mode = rel.EndsWith(".credentials.yaml", StringComparison.OrdinalIgnoreCase) ? "600" : "644";
            var cmd = $"mkdir -p {remoteHome}/{dir} && echo {b64} | base64 -d > {remoteHome}/{rel} && chmod {mode} {remoteHome}/{rel} && echo ok";
            var r = _runner.Exec(cmd, 60);
            if (r.Contains("ok")) { configCount++; onOutput?.Invoke($"  配置 {rel} ✓"); }
            else onOutput?.Invoke($"  配置 {rel} 失败: {r.Trim().Replace("\n", " ")}");
        }
        sb.AppendLine($"配置: {configCount} 个文件");

        // 2) 插件同步：本地 dependencies 里的插件（有 Spec 的）→ 远端逐个 dsh plugin add
        //    （注意：不能用 IsBundle 过滤 —— 用户装的插件也出现在 dsh 的 bundles 内置列表里）
        var plugins = pm.ListPlugins().Where(p => !string.IsNullOrEmpty(p.Spec)).ToList();
        onOutput?.Invoke(plugins.Count == 0
            ? "本地没有需要同步的插件"
            : $"同步插件: {plugins.Count} 个 → 远端 dsh plugin add…");
        var pluginCount = 0;
        foreach (var pl in plugins)
        {
            ct.ThrowIfCancellationRequested();
            var spec = $"{pl.Package}@{pl.Spec}";
            onOutput?.Invoke($">>> dsh plugin --profile web add {spec}");
            var code = await RunPluginAsync(new[] { "add", spec }, onOutput, ct);
            if (code == 0) { pluginCount++; onOutput?.Invoke($"  插件 {pl.Package} ✓"); }
            else onOutput?.Invoke($"  插件 {pl.Package} 失败（exit {code}）");
        }
        sb.AppendLine($"插件: {pluginCount}/{plugins.Count} 个");
        return sb.ToString().Trim();
    }


    public Task<int> RunPluginAsync(string[] args, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var cmd = "dsh plugin --profile web " + string.Join(" ", args.Select(a => $"\"{a.Replace("\"", "\\\"")}\""));
        Log(">>> " + cmd);
        return _runner.ExecAsync(cmd, onOutput, line => onOutput?.Invoke("[err] " + line), ct);
    }

    public async Task<string?> GetInstalledVersionAsync()
    {
        try
        {
            // 方式一：npm 全局定位 dsh 绝对路径（不依赖 dsh 在 PATH）
            var node = _runner.Exec("command -v node || which node || echo", 30).Trim();
            var npmRoot = _runner.Exec("npm root -g 2>/dev/null || echo", 30).Trim();
            if (!string.IsNullOrEmpty(node) && !string.IsNullOrEmpty(npmRoot))
            {
                var dshBin = $"{npmRoot}/@deepseek-ai/dsh/lib/bin.js";
                if (FileExistsRemote(dshBin))
                {
                    var r = _runner.Exec($"{node} {dshBin} --version", 30);
                    var first = r.Trim().Split('\n')[0].Trim();
                    if (!string.IsNullOrEmpty(first) && !first.StartsWith("["))
                    {
                        Log($"远端 dsh 版本: {first} ({dshBin})");
                        return first;
                    }
                    Log($"dsh bin 存在但版本探测失败: [{r.Trim()}]");
                }
            }
            // 方式二：dsh 在 PATH
            var r2 = _runner.Exec("dsh --version 2>/dev/null || echo", 30);
            var first2 = r2.Trim().Split('\n')[0].Trim();
            if (string.IsNullOrEmpty(first2) || first2.StartsWith("[")) return null;
            Log($"远端 dsh 版本(PATH): {first2}");
            return first2;
        }
        catch (Exception ex) { Log("版本探测异常: " + ex.Message); return null; }
    }

    private bool FileExistsRemote(string path)
        => _runner.Exec($"test -f \"{path}\" && echo yes || echo no", 30).Trim() == "yes";

    public Task<int> UpdateDshAsync(Action<string>? onOutput = null, CancellationToken ct = default)
    {
        Log(">>> npm install -g @deepseek-ai/dsh@latest");
        return _runner.ExecAsync("npm install -g @deepseek-ai/dsh@latest", onOutput, line => onOutput?.Invoke("[err] " + line), ct);
    }

    public async Task<string?> TestConnectionAsync(CancellationToken ct = default)
    {
        string detail = "";
        var ok = await Task.Run(() => _runner.TestConnection(out detail), ct);
        if (!ok) return "连接失败: " + detail;
        Log($"测试连接: SSH OK, 开始探测 dsh…");
        var version = await GetInstalledVersionAsync();
        Log($"测试连接结果: dsh={(version ?? "未安装")}");
        return $"SSH 连接正常 · 远端 dsh {(version ?? "未安装")}";
    }

    // ── 状态与日志 ──
    private void SetState(HostState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    private void Log(string line)
    {
        var full = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!); File.AppendAllText(LogFile, full + Environment.NewLine); } catch { }
        LogLine?.Invoke(full);
    }

    public void AppendLog(string line) => Log(line);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;
        try { _tunnel?.Kill(entireProcessTree: true); _tunnel?.Dispose(); } catch { }
    }
}
