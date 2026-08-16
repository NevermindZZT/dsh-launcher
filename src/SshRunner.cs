using System.Diagnostics;
using System.Text;

namespace DshLauncher;

/// <summary>
/// 系统 OpenSSH 客户端封装：使用本机 ssh（Windows 10/11 自带、Linux/macOS 自带），
/// 不集成任何 SSH 协议库。支持：端口转发（ssh -N -L）、远端命令执行、密钥/密码（SSH_ASKPASS）认证。
/// </summary>
public sealed class SshRunner
{
    private readonly SshConnectionConfig _cfg;
    private string? _askPassScript;

    public SshRunner(SshConnectionConfig cfg)
    {
        _cfg = cfg;
    }

    /// <summary>ssh 可执行文件（Windows 用 ssh.exe，由 PATH 解析；可被 SSH_BIN 覆盖）。</summary>
    private static string SshExecutable => Environment.GetEnvironmentVariable("SSH_BIN") ?? "ssh";

    // ── 认证参数 ──
    private List<string> BaseArgs()
    {
        var a = new List<string>
        {
            "-p", _cfg.Port.ToString(),
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "ConnectTimeout=10",
            "-o", "ServerAliveInterval=30",
        };
        if (_cfg.AuthMethod == "key" && !string.IsNullOrEmpty(_cfg.KeyPath) && File.Exists(_cfg.KeyPath))
        {
            a.Add("-i");
            a.Add(_cfg.KeyPath);
        }
        else if (_cfg.AuthMethod == "password")
        {
            // 密码认证：SSH_ASKPASS 提供密码（无 TTY 场景）
            a.Add("-o");
            a.Add("BatchMode=no");
            a.Add("-o");
            a.Add("PreferredAuthentications=password");
        }
        a.Add($"{_cfg.User}@{_cfg.Host}");
        return a;
    }

    /// <summary>为密码认证准备 SSH_ASKPASS 脚本（临时文件）。</summary>
    private string? PrepareAskPass()
    {
        if (_cfg.AuthMethod != "password" || string.IsNullOrEmpty(_cfg.Password)) return null;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "DshLauncher");
            Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, "ssh-askpass" + (OperatingSystem.IsWindows() ? ".cmd" : ".sh"));
            var content = OperatingSystem.IsWindows()
                ? $"@echo {_cfg.Password.Replace("%", "%%")}\r\n"
                : $"#!/bin/sh\necho '{_cfg.Password}'\n";
            File.WriteAllText(script, content);
            if (!OperatingSystem.IsWindows())
            {
                // 赋可执行权限
                try { Process.Start(new ProcessStartInfo("chmod", $"+x \"{script}\"") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(3000); } catch { }
            }
            return script;
        }
        catch { return null; }
    }

    /// <summary>构建 ssh 进程的环境（密码认证时注入 SSH_ASKPASS）。</summary>
    private void ApplyEnv(ProcessStartInfo psi)
    {
        if (_cfg.AuthMethod == "password" && !string.IsNullOrEmpty(_cfg.Password))
        {
            var script = _askPassScript ??= PrepareAskPass();
            if (script != null)
            {
                psi.Environment["SSH_ASKPASS"] = script;
                psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
                psi.Environment["DISPLAY"] = "dsh-launcher:0";
            }
        }
    }

    /// <summary>
    /// 建立常驻端口转发隧道：ssh -N -L localPort:127.0.0.1:remotePort user@host
    /// 返回持有隧道连接的进程（调用方负责停止）。
    /// </summary>
    public Process StartTunnel(int localPort, int remotePort)
    {
        var args = new List<string> { "-N", "-L", $"{localPort}:127.0.0.1:{remotePort}" };
        args.AddRange(BaseArgs());
        var psi = new ProcessStartInfo
        {
            FileName = SshExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        ApplyEnv(psi);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ssh（检查 OpenSSH 是否可用）");
        return p;
    }

    /// <summary>执行远端命令（阻塞到完成），返回输出。onError 可选接收 stderr。</summary>
    public string Exec(string command, int timeoutSeconds = 180, Action<string>? onError = null)
    {
        var args = BaseArgs();
        args.Add("bash -lc \"export PATH=\\\"$HOME/.npm-global/bin:$PATH\\\"; source ~/.profile 2>/dev/null; source ~/.bashrc 2>/dev/null; " + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
        var psi = new ProcessStartInfo
        {
            FileName = SshExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        ApplyEnv(psi);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ssh");
        var outText = p.StandardOutput.ReadToEnd();
        var errText = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutSeconds * 1000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"SSH 命令超时（{timeoutSeconds}s）");
        }
        if (!string.IsNullOrWhiteSpace(errText) && p.ExitCode != 0) onError?.Invoke(errText);
        return outText + (p.ExitCode != 0 ? $"\n[exit {p.ExitCode}]" : "");
    }

    /// <summary>执行远端命令（异步 + 输出实时回传），返回退出码。</summary>
    public async Task<int> ExecAsync(string command, Action<string>? onOutput = null, Action<string>? onError = null, CancellationToken ct = default)
    {
        var args = BaseArgs();
        args.Add("bash -lc \"export PATH=\\\"$HOME/.npm-global/bin:$PATH\\\"; source ~/.profile 2>/dev/null; source ~/.bashrc 2>/dev/null; " + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
        var psi = new ProcessStartInfo
        {
            FileName = SshExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        ApplyEnv(psi);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ssh");
        var outTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await p.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                onOutput?.Invoke(line);
            }
        }, ct);
        var errTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await p.StandardError.ReadLineAsync(ct);
                if (line == null) break;
                onError?.Invoke(line);
            }
        }, ct);
        await Task.WhenAll(outTask, errTask);
        return p.ExitCode;
    }

    /// <summary>测试 SSH 连接是否可用（执行 echo，返回是否成功）。</summary>
    public bool TestConnection(out string detail)
    {
        try
        {
            var r = Exec("echo ssh-ok", 20);
            if (r.Contains("ssh-ok")) { detail = "SSH 连接正常"; return true; }
            detail = r.Trim();
            return false;
        }
        catch (Exception ex) { detail = ex.Message; return false; }
    }
}
