using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DshLauncher;

/// <summary>
/// dsh 安装检测、版本检查与安装/更新（npm 全局安装 @deepseek-ai/dsh）。
/// 版本检查走 npm registry（通过 Node HTTPS 以兼容部分 Windows Schannel 环境）；安装/更新执行 `npm install -g @deepseek-ai/dsh@latest`。
/// </summary>
public sealed class DshUpdater
{
    public const string PackageName = "@deepseek-ai/dsh";
    private const string RegistryUrl = "https://registry.npmjs.org/@deepseek-ai%2fdsh/latest";

    public sealed record UpdateCheckResult(
        string? InstalledVersion,
        string? LatestVersion,
        string? Error)
    {
        public bool IsUpdateAvailable => IsNewer(LatestVersion, InstalledVersion);
    }

    /// <summary>dsh 是否已安装（bin.js 可解析）。</summary>
    public static bool IsInstalled() => HostSupervisor.ResolveDshPaths().BinJs != null;

    /// <summary>已安装版本（如 0.1.0-rc.6）。优先使用已解析的 node + bin.js，避免 PATH 缺失导致误报未安装。</summary>
    public static async Task<string?> GetInstalledVersionAsync()
    {
        var (node, binJs) = HostSupervisor.ResolveDshPaths();
        if (node != null && binJs != null)
        {
            try
            {
                var direct = new ProcessStartInfo
                {
                    FileName = node,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                direct.ArgumentList.Add(binJs);
                direct.ArgumentList.Add("--version");
                using var process = Process.Start(direct);
                if (process != null)
                {
                    var text = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    var first = text.Trim().Split('\n')[0].Trim();
                    if (!string.IsNullOrEmpty(first)) return first;
                }
            }
            catch
            {
                // 绝对路径探测失败时继续使用 dsh shim 兜底。
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("dsh --version");
            using var p = Process.Start(psi);
            if (p == null) return null;
            var text = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var first = text.Trim().Split('\n')[0].Trim();
            return string.IsNullOrEmpty(first) ? null : first;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>npm registry 最新版本（如 0.1.0-rc.8）。</summary>
    public static async Task<string?> GetLatestVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await NodeHttpClient.GetStringAsync(
                RegistryUrl,
                "DshLauncher/" + VersionHelper.Current.TrimStart('v'),
                ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("version").GetString();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>同时读取当前 dsh 版本和 npm registry 最新版本。</summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var installedTask = GetInstalledVersionAsync();
        var latestTask = GetLatestVersionAsync(ct);
        string? installed = null;
        string? latest = null;
        try { installed = await installedTask; } catch (Exception ex) { return new UpdateCheckResult(null, null, "读取当前 dsh 版本失败：" + ex.Message); }
        try { latest = await latestTask; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { }
        return CreateCheckResult(installed, latest);
    }

    /// <summary>根据已读取的版本构造检查结果，供 SSH 远程 dsh 复用。</summary>
    public static UpdateCheckResult CreateCheckResult(string? installed, string? latest)
    {
        if (latest == null) return new UpdateCheckResult(installed, null, "无法从 npm registry 获取 dsh 最新版本");
        if (installed == null) return new UpdateCheckResult(null, latest, "未检测到当前 dsh 版本");
        return new UpdateCheckResult(installed, latest, null);
    }

    /// <summary>比较版本：latest 是否严格新于 installed（忽略预发布后缀，按数字段比较）。</summary>
    public static bool IsNewer(string? latest, string? installed)
    {
        if (string.IsNullOrEmpty(latest) || string.IsNullOrEmpty(installed)) return false;
        var a = ParseCore(latest);
        var b = ParseCore(installed);
        for (int i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i] > b[i];
        }
        return false;
    }

    private static int[] ParseCore(string v)
    {
        var core = v.Split('-')[0].Split('.');
        var r = new int[3];
        for (int i = 0; i < 3; i++)
        {
            int.TryParse(i < core.Length ? core[i] : "0", out r[i]);
        }
        return r;
    }

    /// <summary>执行 `npm install -g @deepseek-ai/dsh@latest`（安装或更新）。输出实时回传。</summary>
    public static async Task<int> InstallOrUpdateAsync(Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("npm install -g @deepseek-ai/dsh@latest");

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 npm");
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
                onOutput?.Invoke("[npm] " + line);
            }
        }, ct);
        await Task.WhenAll(outTask, errTask);
        return p.ExitCode;
    }
}
