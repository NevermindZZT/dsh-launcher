using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DshLauncher;

/// <summary>
/// dsh 插件管理：读取 $DSH_HOME/profiles/web 的 package.json 列装已装插件；
/// 通过 dsh plugin --profile web <pnpm args> 执行安装/卸载/更新。
/// 插件列表可导出为带实际安装版本的 JSON，导入后按版本逐项安装。
/// </summary>
public sealed class PluginManager
{
    private const string ProfileName = "web";
    private const string ExportFormat = "dsh-launcher-plugin-list";
    private const int ExportSchemaVersion = 1;
    private const long MaxImportFileBytes = 2 * 1024 * 1024;
    private static readonly Regex PackageNamePattern = new(
        @"^(?:@[^/@\s]+/)?[^/@\s]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>dsh 数据目录（$DSH_HOME 或 ~/.dsh）。</summary>
    public string DshHome { get; }

    public string ProfileDir { get; }

    public PluginManager()
    {
        var dshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrEmpty(dshHome))
        {
            dshHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }
        DshHome = dshHome;
        ProfileDir = Path.Combine(dshHome, "profiles", ProfileName);
    }

    /// <summary>一个已装插件条目。</summary>
    public sealed record PluginInfo(string Package, string? Spec, bool IsBundle, bool IsTemplate)
    {
        public string? Version { get; init; }
    }

    /// <summary>插件列表导出文件的根对象。</summary>
    public sealed class PluginListDocument
    {
        public string Format { get; set; } = "";
        public int SchemaVersion { get; set; }
        public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
        public string LauncherVersion { get; set; } = VersionHelper.Current;
        public string? DshVersion { get; set; }
        public List<PluginExportEntry> Plugins { get; set; } = new();
    }

    /// <summary>导出文件中的单个插件，Version 优先于 Spec 用于导入。</summary>
    public sealed class PluginExportEntry
    {
        public string Package { get; set; } = "";
        public string? Version { get; set; }
        public string? Spec { get; set; }
        public bool IsBundle { get; set; }
        public bool IsTemplate { get; set; }
    }

    /// <summary>导入安装条目。</summary>
    public sealed record PluginImportItem(string Package, string? Version, string? Spec, bool IsTemplate)
    {
        public string? InstallSpecifier => BuildInstallSpecifier(Package, Version, Spec);
    }

    /// <summary>列出已装插件：profile package.json 的 dependencies，并读取 node_modules 中的实际版本。</summary>
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
            var bundleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (bundles != null)
            {
                foreach (var b in bundles)
                {
                    var packageName = ReadString(b);
                    if (!string.IsNullOrWhiteSpace(packageName)) bundleSet.Add(packageName);
                }
            }
            var templateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "@deepseek-ai/dsh-base",
                "@deepseek-ai/dsh-web-app",
            };

            if (deps != null)
            {
                foreach (var kv in deps)
                {
                    var spec = ReadString(kv.Value);
                    result.Add(new PluginInfo(
                        kv.Key,
                        spec,
                        bundleSet.Contains(kv.Key),
                        templateSet.Contains(kv.Key))
                    {
                        Version = ResolveInstalledVersion(kv.Key),
                    });
                }
            }

            // bundle 中非依赖的内置项（模板）也展示，便于用户知道 profile 的完整组成。
            foreach (var packageName in bundleSet)
            {
                if (!result.Any(p => string.Equals(p.Package, packageName, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(new PluginInfo(
                        packageName,
                        null,
                        true,
                        true)
                    {
                        Version = ResolveInstalledVersion(packageName),
                    });
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

    /// <summary>将当前插件列表导出为 JSON 文件。</summary>
    public void ExportToFile(string filePath, string? dshVersion = null)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("未指定导出文件", nameof(filePath));
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var document = new PluginListDocument
        {
            Format = ExportFormat,
            SchemaVersion = ExportSchemaVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            LauncherVersion = VersionHelper.Current,
            DshVersion = dshVersion,
            Plugins = ListPlugins().Select(p => new PluginExportEntry
            {
                Package = p.Package,
                Version = p.Version,
                Spec = p.Spec,
                IsBundle = p.IsBundle,
                IsTemplate = p.IsTemplate,
            }).ToList(),
        };
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        File.WriteAllText(fullPath, JsonSerializer.Serialize(document, options), new UTF8Encoding(false));
    }

    /// <summary>读取并校验插件列表导出文件。</summary>
    public static PluginListDocument ReadFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("未指定导入文件", nameof(filePath));
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到插件列表文件", fullPath);
        if (new FileInfo(fullPath).Length > MaxImportFileBytes)
            throw new InvalidDataException("插件列表文件过大（最大支持 2 MB）");

        PluginListDocument? document;
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            document = JsonSerializer.Deserialize<PluginListDocument>(File.ReadAllText(fullPath), options);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("插件列表不是有效的 JSON 文件", ex);
        }

        if (document == null || !string.Equals(document.Format, ExportFormat, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不是 DshLauncher 插件列表文件");
        if (document.SchemaVersion != ExportSchemaVersion)
            throw new InvalidDataException($"不支持的插件列表版本：{document.SchemaVersion}");

        var normalized = new List<PluginExportEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Plugins ?? new List<PluginExportEntry>())
        {
            if (entry == null) throw new InvalidDataException("插件列表包含空插件条目");
            var package = entry.Package?.Trim() ?? "";
            if (!IsSafePackageName(package))
                throw new InvalidDataException($"插件包名无效：{entry.Package}");
            var version = NormalizeSpec(entry.Version);
            var spec = NormalizeSpec(entry.Spec);
            if (version != null && !IsSafeVersion(version))
                throw new InvalidDataException($"插件 {package} 的版本无效：{version}");
            if (spec != null && !IsSafeVersion(spec))
                throw new InvalidDataException($"插件 {package} 的版本规格无效：{spec}");
            if (!seen.Add(package)) continue;
            normalized.Add(new PluginExportEntry
            {
                Package = package,
                Version = version,
                Spec = spec,
                IsBundle = entry.IsBundle,
                IsTemplate = entry.IsTemplate,
            });
        }
        document.Plugins = normalized;
        return document;
    }

    /// <summary>返回可安装条目；模板内置包不会被重复安装。</summary>
    public static List<PluginImportItem> GetImportItems(PluginListDocument document)
    {
        var result = new List<PluginImportItem>();
        foreach (var entry in document.Plugins ?? new List<PluginExportEntry>())
        {
            if (entry == null || entry.IsTemplate) continue;
            if (BuildInstallSpecifier(entry.Package, entry.Version, entry.Spec) == null) continue;
            result.Add(new PluginImportItem(entry.Package, entry.Version, entry.Spec, entry.IsTemplate));
        }
        return result;
    }

    /// <summary>按实际版本优先构造 npm/pnpm 安装参数，如 @scope/plugin@1.2.3。</summary>
    public static string? BuildInstallSpecifier(string package, string? version, string? spec)
    {
        if (!IsSafePackageName(package)) return null;
        var selected = NormalizeSpec(version) ?? NormalizeSpec(spec);
        if (selected == null) return null;
        return package.Trim() + "@" + selected;
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

    /// <summary>执行 dsh plugin --profile web <args>（如 add/remove/update）。输出实时回传。</summary>
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
            FileName = node ?? throw new InvalidOperationException("未找到 Node.js（插件管理需要 Node.js）"),
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
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    private string? ResolveInstalledVersion(string package)
    {
        if (!IsSafePackageName(package)) return null;
        try
        {
            var packageJson = Path.Combine(ProfileDir, "node_modules", package, "package.json");
            if (!File.Exists(packageJson)) return null;
            var root = JsonNode.Parse(File.ReadAllText(packageJson));
            return ReadString(root?["version"]);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonNode? node)
    {
        try { return node?.GetValue<string>(); }
        catch { return null; }
    }

    private static string? NormalizeSpec(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool IsSafePackageName(string? package)
    {
        return !string.IsNullOrWhiteSpace(package)
            && package.Length <= 214
            && !package.Contains('\\')
            && PackageNamePattern.IsMatch(package.Trim());
    }

    private static bool IsSafeVersion(string value)
    {
        return value.Length <= 128
            && !value.Any(char.IsControl)
            && !value.Any(char.IsWhiteSpace)
            && !value.StartsWith("-", StringComparison.Ordinal);
    }
}
