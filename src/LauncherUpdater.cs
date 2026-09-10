using System.Text.Json;

namespace DshLauncher;

/// <summary>
/// DshLauncher 更新检查：从 GitHub Releases 获取最新版本和 Windows 下载链接，不自动下载或替换当前程序。
/// </summary>
public static class LauncherUpdater
{
    public const string ProjectUrl = "https://github.com/NevermindZZT/dsh-launcher";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/NevermindZZT/dsh-launcher/releases/latest";

    public sealed record UpdateCheckResult(
        string CurrentVersion,
        string? LatestVersion,
        string? ReleaseUrl,
        string? DownloadUrl,
        string? ReleaseName,
        string? Error)
    {
        public bool IsUpdateAvailable => IsNewer(LatestVersion, CurrentVersion);
    }

    /// <summary>检查 GitHub 最新 Release；网络失败返回 Error，不阻断 launcher 正常使用。</summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var current = VersionHelper.Current;
        try
        {
            var json = await NodeHttpClient.GetStringAsync(
                LatestReleaseApiUrl,
                "DshLauncher/" + current.TrimStart('v'),
                ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
            var releaseUrl = root.TryGetProperty("html_url", out var urlValue) ? urlValue.GetString() : null;
            var downloadUrl = FindWindowsDownloadUrl(root) ?? releaseUrl;
            var releaseName = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            var latest = NormalizeVersion(tag);
            return latest == null
                ? new UpdateCheckResult(current, null, releaseUrl, downloadUrl, releaseName, "GitHub Release 未返回有效版本")
                : new UpdateCheckResult(current, latest, releaseUrl, downloadUrl, releaseName, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(current, null, null, null, null, FriendlyError(ex));
        }
    }

    private static string? FindWindowsDownloadUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            if (!string.Equals(name, "DshLauncher.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (asset.TryGetProperty("browser_download_url", out var urlValue))
                return urlValue.GetString();
        }
        return null;
    }

    public static bool IsNewer(string? latest, string? installed)
    {
        if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(installed)) return false;
        var a = ParseCore(latest);
        var b = ParseCore(installed);
        for (var i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i] > b[i];
        }
        return false;
    }

    public static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        while (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];
        return string.IsNullOrWhiteSpace(normalized) ? null : "v" + normalized;
    }

    private static int[] ParseCore(string value)
    {
        var core = value.Trim().TrimStart('v', 'V').Split('-')[0].Split('.');
        var result = new int[3];
        for (var i = 0; i < result.Length; i++)
            int.TryParse(i < core.Length ? core[i] : "0", out result[i]);
        return result;
    }

    private static string FriendlyError(Exception ex)
    {
        return ex switch
        {
            HttpRequestException => "无法连接 GitHub，请检查网络后重试",
            TaskCanceledException => "检查更新超时",
            _ => "检查更新失败：" + ex.Message,
        };
    }
}
