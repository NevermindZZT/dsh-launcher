using System.Globalization;
using System.Net;

namespace DshLauncher;

/// <summary>Helpers for DSH's process-local one-time browser startup URL.</summary>
internal static class DshStartupUrl
{
    private const string TokenParameter = "token";
    private const string UnauthorizedMarker = "dsh web authentication required";

    public static bool HasToken(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && TryGetToken(uri, out _);

    public static bool TryExtractToken(string? input, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var candidate = input.Trim();
        if (candidate.StartsWith("dsh web:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate["dsh web:".Length..].Trim();
            var separator = candidate.IndexOf(' ');
            if (separator >= 0) candidate = candidate[..separator];
        }
        if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && TryGetToken(uri, out token);

        if (candidate.StartsWith("token=", StringComparison.Ordinal)) candidate = candidate[6..];
        try { candidate = Uri.UnescapeDataString(candidate); }
        catch { return false; }
        if (candidate.Length == 0 || candidate.Any(char.IsWhiteSpace) || candidate.IndexOfAny(new[] { '&', '?', '#' }) >= 0)
            return false;
        token = candidate;
        return true;
    }

    public static string WithToken(string baseUrl, string tokenOrUrl)
    {
        if (!TryExtractToken(tokenOrUrl, out var token)) throw new ArgumentException("A DSH startup token is required.", nameof(tokenOrUrl));
        var builder = new UriBuilder(baseUrl) { Query = "token=" + Uri.EscapeDataString(token), Fragment = string.Empty };
        return builder.Uri.ToString();
    }

    private static bool TryGetToken(Uri uri, out string token)
    {
        token = string.Empty;
        var matches = 0;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var rawName = separator < 0 ? part : part[..separator];
            string name;
            try { name = Uri.UnescapeDataString(rawName); }
            catch { continue; }
            if (!string.Equals(name, TokenParameter, StringComparison.Ordinal)) continue;
            matches++;
            if (separator < 0) continue;
            try { token = Uri.UnescapeDataString(part[(separator + 1)..]); }
            catch { token = string.Empty; }
        }
        if (matches == 1 && !string.IsNullOrWhiteSpace(token)) return true;
        token = string.Empty;
        return false;
    }


    public static bool IsDshResponse(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.Unauthorized)
            return body.Contains(UnauthorizedMarker, StringComparison.OrdinalIgnoreCase);

        return status == HttpStatusCode.OK
            && (body.Contains("__DSH_BOOT__", StringComparison.OrdinalIgnoreCase)
                || body.Contains("DeepSeek Harness", StringComparison.OrdinalIgnoreCase));
    }

    public static string Redact(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query))
            return value;
        return uri.GetLeftPart(UriPartial.Path) + "?[redacted]";
    }
}

internal static class DshStartupAuthMessages
{
    private static bool UseChinese => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string Localize(string chinese, string english) => UseChinese ? chinese : english;

    public static string Title => Localize("无法附加到已运行的 DSH", "Cannot attach to the running DSH");

    public static string TokenPromptTitle => Localize("输入 DSH startup token", "Enter DSH startup token");
    public static string TokenPromptIntro => Localize(
        "请粘贴 dsh web 启动 URL 中 token= 后的值，也可以粘贴完整 URL 或整行 dsh web 日志。token 不写入启动器设置或日志；若启用 dsh-manager Agent，连接 URL 会按现有实例状态上报给已配置的 Manager。",
        "Paste the value after token=, the full startup URL, or the complete dsh web log line. The token is not written to launcher settings or logs. If the dsh-manager Agent is enabled, the connection URL is also reported to the configured Manager as part of instance status.");
    public static string TokenInputLabel => Localize("启动令牌", "Startup token");
    public static string TokenInputHint => Localize("token 值、启动 URL 或 dsh web 日志行", "Token, startup URL, or dsh web log line");
    public static string TokenRetryLoading => Localize("正在使用 token 重新连接 DSH…", "Retrying DSH with the startup token…");
    public static string TokenRetryFailed => Localize("无法应用该 token。请检查启动 URL 后重试。", "Could not apply the token. Check the startup URL and try again.");
    public static string TokenInputInvalid => Localize("未找到有效 token。请粘贴 token 值或完整启动 URL。", "No valid token was found. Paste the token value or the full startup URL.");
    public static string ContinueButton => Localize("继续", "Continue");
    public static string CancelButton => Localize("取消", "Cancel");
}
