using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DshLauncher;

/// <summary>
/// dsh 安装检测、版本检查与安装/更新（npm 全局安装 @deepseek-ai/dsh）。
/// 版本检查走 npm registry HTTP；安装/更新执行 `npm install -g @deepseek-ai/dsh@latest`。
/// </summary>
public sealed class DshUpdater
{
    public const string PackageName = "@deepseek-ai/dsh";
    private const string RegistryUrl = "https://registry.npmjs.org/@deepseek-ai/dsh/latest";

    /// <summary>dsh 是否已安装（bin.js 可解析）。</summary>
    public static bool IsInstalled() => HostSupervisor.ResolveDshPaths().BinJs != null;

    /// <summary>已安装版本（`dsh --version`，如 0.1.0-rc.6）。</summary>
    public static async Task<string?> GetInstalledVersionAsync()
    {
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
    public static async Task<string?> GetLatestVersionAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await client.GetStringAsync(RegistryUrl);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("version").GetString();
        }
        catch
        {
            return null;
        }
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
