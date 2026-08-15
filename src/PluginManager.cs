using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace DshLauncher;

/// <summary>
/// dsh 插件管理：读取 $DSH_HOME/profiles/web 的 package.json 列装已装插件；
/// 通过 `dsh plugin --profile web <pnpm args>` 执行安装/卸载/更新（官方 pnpm 转发器）。
/// 命令输出实时回传，成功后调用方负责重启宿主使插件生效。
/// </summary>
public sealed class PluginManager
{
    private const string ProfileName = "web";

    public string ProfileDir { get; }

    public PluginManager()
    {
        var dshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrEmpty(dshHome))
        {
            dshHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }
        ProfileDir = Path.Combine(dshHome, "profiles", ProfileName);
    }

    /// <summary>一个已装插件条目。</summary>
    public sealed record PluginInfo(string Package, string? Spec, bool IsBundle, bool IsTemplate);

    /// <summary>列出已装插件：profile package.json 的 dependencies（安装态），标记 bundle 与模板内置。</summary>
    public List<PluginInfo> ListPlugins()
    {
        var result = new List<PluginInfo>();
        var pkgPath = Path.Combine(ProfileDir, "package.json");
        if (!File.Exists(pkgPath)) return result;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(pkgPath))?.AsObject();
            var deps = root?["dependencies"]?.AsObject();
            var bundles = root?["dsh"]?["profile"]?["bundles"]?.AsArray();
            var bundleSet = new HashSet<string>();
            if (bundles != null)
            {
                foreach (var b in bundles) bundleSet.Add(b?.GetValue<string>() ?? "");
            }
            var templateSet = new HashSet<string> { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" };

            if (deps != null)
            {
                foreach (var kv in deps)
                {
                    var spec = kv.Value?.GetValue<string>();
                    result.Add(new PluginInfo(kv.Key, spec, bundleSet.Contains(kv.Key), templateSet.Contains(kv.Key)));
                }
            }
            // bundle 中非依赖的内置项（模板）也展示
            foreach (var b in bundleSet)
            {
                if (!string.IsNullOrEmpty(b) && !result.Any(p => p.Package == b))
                {
                    result.Add(new PluginInfo(b, null, true, true));
                }
            }
            result.Sort((a, b) => string.Compare(a.Package, b.Package, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // 解析失败返回空列表
        }
        return result;
    }

    /// <summary>pnpm 是否在 PATH（dsh plugin 依赖 pnpm）。</summary>
    public static bool PnpmAvailable()
    {
        return FindOnPath("pnpm.exe") != null || FindOnPath("pnpm.cmd") != null || FindOnPath("pnpm") != null;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cand = Path.Combine(dir.Trim('"'), fileName);
            if (File.Exists(cand)) return cand;
        }
        return null;
    }

    /// <summary>执行 `dsh plugin --profile web <args>`（如 add/remove/update）。输出实时回传。</summary>
    public async Task<int> RunAsync(string[] args, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var (node, binJs) = HostSupervisor.ResolveDshPaths();
        if (binJs == null)
        {
            throw new InvalidOperationException("未找到 dsh 安装（npm install -g @deepseek-ai/dsh）");
        }
        if (!PnpmAvailable())
        {
            throw new InvalidOperationException(
                "未找到 pnpm。请先安装 pnpm（npm install -g pnpm），插件管理需要它。");
        }

        var psi = new ProcessStartInfo
        {
            FileName = node!,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(binJs);
        psi.ArgumentList.Add("plugin");
        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add(ProfileName);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 dsh plugin");
        var outTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                onOutput?.Invoke("[out] " + line);
            }
        }, ct);
        var errTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await proc.StandardError.ReadLineAsync(ct);
                if (line == null) break;
                onOutput?.Invoke("[err] " + line);
            }
        }, ct);
        await Task.WhenAll(outTask, errTask);
        return proc.ExitCode;
    }
}
