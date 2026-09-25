using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DshLauncher;

/// <summary>
/// 远端 dsh 生命周期管理：通过系统 ssh 在服务器上启动/停止 dsh。
/// 分级策略：systemd --user（服务化，首选）→ nohup 后台进程（覆盖无 systemd 的 Linux/容器/macOS）。
/// 就绪检测由 SshConnection 通过本地转发端口完成（无需远端往返探测）。
/// </summary>
public static class RemoteDshManager
{
    private const string ServiceUnit = "dsh-launcher.service";
    private const string LaunchAgentPath = "~/Library/LaunchAgents/com.dshlauncher.dsh.plist";
    private const string NohupDir = "~/.dsh-launcher";

    /// <summary>在远端启动 dsh（systemd 优先，nohup 降级），返回使用的方式。调用方随后等待本地转发端口就绪。</summary>
    public static string StartRemote(SshRunner runner, SshConnectionConfig cfg, int remotePort, Action<string> log)
    {
        // 0) 远端 dsh 是否已在运行（防多实例并发启动：隧道探测慢时误判未运行会重复启动 → 端口/配置冲突）
        if (IsRemoteReady(runner, remotePort))
        {
            log($"远端 dsh 已在运行 (端口 {remotePort})，直接复用");
            return "ready";
        }

        // 探测 node / dsh 路径
        var node = !string.IsNullOrEmpty(cfg.RemoteNode) ? cfg.RemoteNode : runner.Exec("command -v node || which node").Trim();
        log($"探测 node => [{node}]");
        var dshBin = cfg.RemoteDshBin;
        if (string.IsNullOrEmpty(dshBin))
        {
            var npmRoot = runner.Exec("npm root -g 2>/dev/null || echo").Trim();
            log($"探测 npmRoot => [{npmRoot}]");
            dshBin = $"{npmRoot}/@deepseek-ai/dsh/lib/bin.js";
            if (!FileExists(runner, dshBin))
            {
                var link = runner.Exec("readlink -f $(command -v dsh) 2>/dev/null || echo").Trim();
                if (!string.IsNullOrEmpty(link) && !link.Contains("[")) dshBin = link;
            }
        }
        log($"dshBin => [{dshBin}], 存在 => {FileExists(runner, dshBin)}");
        if (string.IsNullOrEmpty(node) || !FileExists(runner, dshBin))
        {
            throw new InvalidOperationException("远端未检测到 dsh（需安装 Node.js 并执行 npm install -g @deepseek-ai/dsh）");
        }
        log($"远端 dsh 路径: {node} {dshBin}");
        ResetStartupLog(runner);

        // 分级：systemd（Linux）→ launchctl（macOS）→ nohup（兜底）
        if (TrySystemd(runner, cfg, remotePort, node, dshBin, log)) return "systemd";
        if (TryLaunchctl(runner, cfg, remotePort, node, dshBin, log)) return "launchctl";
        // nohup 前再确认（systemd/launchctl 可能刚启动成功但探测未及，避免多实例）
        if (IsRemoteReady(runner, remotePort))
        {
            log("远端 dsh 已就绪，跳过 nohup");
            return "ready";
        }
        StartNohup(runner, cfg, remotePort, node, dshBin, log);
        return "nohup";
    }

    /// <summary>返回 200/401 仅当响应正文确认为 DSH；其他 HTTP 服务不视为可复用实例。</summary>
    public static int ProbeRemoteStatus(SshRunner runner, int port)
    {
        if (port is <= 0 or >= 65536) return 0;
        try
        {
            var response = runner.Exec(
                $"curl -sS --max-time 3 -w '\\n__DSH_HTTP__%{{http_code}}' http://127.0.0.1:{port}/ 2>/dev/null || true",
                15);
            const string marker = "\n__DSH_HTTP__";
            var markerIndex = response.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0) return 0;
            var body = response[..markerIndex];
            var statusText = response[(markerIndex + marker.Length)..].Trim();
            if (!int.TryParse(statusText, out var status)) return 0;
            return DshStartupUrl.IsDshResponse((HttpStatusCode)status, body) ? status : 0;
        }
        catch { return 0; }
    }

    public static bool IsRemoteReady(SshRunner runner, int port)
    {
        var status = ProbeRemoteStatus(runner, port);
        return status == (int)HttpStatusCode.OK || status == (int)HttpStatusCode.Unauthorized;
    }

    public static string? TryReadStartupUrl(SshRunner runner, int remotePort)
    {
        try
        {
            var raw = runner.Exec("grep 'dsh web:' ~/.dsh-launcher/dsh-web.log 2>/dev/null | tail -1 | sed -n 's/.*dsh web: \\(http[^ ]*\\).*/\\1/p' || true", 15).Trim();
            return ParseStartupUrl(raw, remotePort);
        }
        catch { return null; }
    }

    public static string? WaitForStartupUrl(
        SshRunner runner,
        int remotePort,
        Action<string> log,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var parsed = TryReadStartupUrl(runner, remotePort);
            if (parsed != null)
            {
                log(DshStartupUrl.HasToken(parsed)
                    ? "已从远端 dsh 启动日志捕获 startup URL（token 已隐藏）"
                    : "远端 dsh 启动日志没有 token，将尝试复用现有 WebView 会话");
                return parsed;
            }
            Thread.Sleep(1000);
        }
        log("等待远端 startup URL 超时；不会再次启动同一 DSH 实例");
        return null;
    }

    private static string? ParseStartupUrl(string raw, int remotePort)
    {
        var candidate = raw.Trim().TrimEnd(',', ')', ';');
        var match = Regex.Match(candidate, @"^https?://127\.0\.0\.1:(?<port>\d+)(?:/[^\s]*)?$", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["port"].Value, out var port) || port != remotePort) return null;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && uri.Scheme == "http"
            && uri.Host == "127.0.0.1"
            && uri.Port == remotePort
            ? candidate
            : null;
    }

    private static void ResetStartupLog(SshRunner runner)
    {
        // Startup URLs contain a process-local bearer token. Keep one private
        // previous log for diagnostics, but never mistake an old token for the
        // URL of a newly launched DSH process.
        var result = runner.Exec(
            "umask 077; mkdir -p ~/.dsh-launcher && mv -f ~/.dsh-launcher/dsh-web.log ~/.dsh-launcher/dsh-web.log.1 2>/dev/null; : > ~/.dsh-launcher/dsh-web.log && chmod 600 ~/.dsh-launcher/dsh-web.log && echo startup-log-ready",
            20);
        if (!result.Contains("startup-log-ready", StringComparison.Ordinal))
            throw new InvalidOperationException("无法准备远端 DSH startup URL 日志；为避免错误复用旧 token，已取消启动。");
        try { runner.Exec("chmod 600 ~/.dsh-launcher/dsh-web.log.1 2>/dev/null || true", 15); } catch { }
    }

    /// <summary>尝试 launchctl（macOS）启动；非 Darwin 或失败返回 false。</summary>
    private static bool TryLaunchctl(SshRunner runner, SshConnectionConfig cfg, int remotePort, string node, string dshBin, Action<string> log)
    {
        var uname = runner.Exec("uname -s 2>/dev/null || echo unknown").Trim();
        if (uname != "Darwin") return false;
        try
        {
            var home = runner.Exec("echo $HOME").Trim();
            if (string.IsNullOrEmpty(home)) return false;
            var plistPath = $"{home}/Library/LaunchAgents/com.dshlauncher.dsh.plist";
            var plist = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key><string>com.dshlauncher.dsh</string>
    <key>ProgramArguments</key>
    <array>
        <string>{node}</string>
        <string>{dshBin}</string>
        <string>web</string>
        <string>--host</string><string>127.0.0.1</string>
        <string>--port</string><string>{remotePort}</string>
        <string>--no-open</string>
    </array>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><true/>
    <key>StandardOutPath</key><string>{home}/.dsh-launcher/dsh-web.log</string>
    <key>StandardErrorPath</key><string>{home}/.dsh-launcher/dsh-web.log</string>
</dict>
</plist>";
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(plist));
            var setup = $"mkdir -p {home}/Library/LaunchAgents {home}/.dsh-launcher && echo {b64} | base64 -d > {plistPath} && launchctl unload {plistPath} 2>/dev/null; launchctl load {plistPath} && echo launchctl-ok";
            var result = runner.Exec(setup);
            if (result.Contains("launchctl-ok"))
            {
                log("远端 dsh 已通过 launchctl（macOS）启动");
                return true;
            }
            log("launchctl 启动失败（" + result.Trim().Replace("\n", " ") + "），降级 nohup");
        }
        catch (Exception ex)
        {
            log("launchctl 异常（" + ex.Message + "），降级 nohup");
        }
        return false;
    }

    private static bool TrySystemd(SshRunner runner, SshConnectionConfig cfg, int remotePort, string node, string dshBin, Action<string> log)
    {
        var systemdOk = runner.Exec("systemctl --user show-environment >/dev/null 2>&1 && echo ok || echo no").Trim() == "ok";
        if (!systemdOk) return false;
        try
        {
            var service = $@"[Unit]
Description=DshLauncher - DeepSeek Harness (dsh)
After=network.target

[Service]
Type=simple
ExecStart={node} {dshBin} web --host 127.0.0.1 --port {remotePort} --no-open
Restart=on-failure
RestartSec=3
StandardOutput=append:%h/.dsh-launcher/dsh-web.log
StandardError=append:%h/.dsh-launcher/dsh-web.log

[Install]
WantedBy=default.target";
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(service));
            var setup = $"mkdir -p ~/.config/systemd/user ~/.dsh-launcher && echo {b64} | base64 -d > ~/.config/systemd/user/{ServiceUnit} && systemctl --user daemon-reload && systemctl --user enable --now {ServiceUnit} 2>&1 && systemctl --user restart {ServiceUnit} 2>&1 && echo systemd-ok";
            var result = runner.Exec(setup);
            if (result.Contains("systemd-ok"))
            {
                log("远端 dsh 已通过 systemd --user 启动");
                return true;
            }
            log("systemd 启动失败（" + result.Trim().Replace("\n", " ") + "），降级 nohup");
        }
        catch (Exception ex)
        {
            log("systemd 异常（" + ex.Message + "），降级 nohup");
        }
        return false;
    }

    private static void StartNohup(SshRunner runner, SshConnectionConfig cfg, int remotePort, string node, string dshBin, Action<string> log)
    {
        var cmd = $"mkdir -p {NohupDir} && nohup {node} {dshBin} web --host 127.0.0.1 --port {remotePort} --no-open > {NohupDir}/dsh-web.log 2>&1 & echo $! > {NohupDir}/dsh.pid && echo nohup-started";
        var result = runner.Exec(cmd);
        if (!result.Contains("nohup-started"))
        {
            throw new InvalidOperationException("远端 dsh 启动失败（nohup）：" + result.Trim().Replace("\n", " "));
        }
        log("远端 dsh 已通过 nohup 启动（日志: " + NohupDir + "/dsh-web.log）");
    }

    /// <summary>停止远端 dsh（systemd 优先，回退 nohup kill）。</summary>
    public static void StopRemote(SshRunner runner, SshConnectionConfig cfg)
    {
        try { runner.Exec($"systemctl --user stop {ServiceUnit} 2>/dev/null; rm -f ~/.config/systemd/user/{ServiceUnit}; systemctl --user daemon-reload 2>/dev/null", 30); } catch { }
        try { runner.Exec($"launchctl unload {LaunchAgentPath} 2>/dev/null; rm -f {LaunchAgentPath}", 30); } catch { }
        try { runner.Exec($"kill $(cat {NohupDir}/dsh.pid 2>/dev/null) 2>/dev/null; rm -f {NohupDir}/dsh.pid", 30); } catch { }
    }

    private static bool FileExists(SshRunner runner, string path)
    {
        var r = runner.Exec($"test -f \"{path}\" && echo yes || echo no", 30).Trim();
        return r == "yes";
    }
}
