using System.Text;

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

    /// <summary>远端 dsh 是否就绪（HTTP 200 探测）。</summary>
    public static bool IsRemoteReady(SshRunner runner, int port)
    {
        try
        {
            var code = runner.Exec($"curl -s -o /dev/null -w '%{{http_code}}' --max-time 3 http://127.0.0.1:{port}/ || echo 000", 15);
            return code.Trim() == "200";
        }
        catch { return false; }
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

[Install]
WantedBy=default.target";
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(service));
            var setup = $"mkdir -p ~/.config/systemd/user && echo {b64} | base64 -d > ~/.config/systemd/user/{ServiceUnit} && systemctl --user daemon-reload && systemctl --user enable --now {ServiceUnit} 2>&1 && echo systemd-ok";
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
