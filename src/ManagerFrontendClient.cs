using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshLauncher;

public sealed record ManagerBrowserCookie(string Name, string Value, string Domain, string Path, bool Secure);

public sealed class ManagerAuthState
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class ManagerAgentInfo
{
    [JsonPropertyName("agentId")] public string AgentId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("launcherVersion")] public string LauncherVersion { get; set; } = "";
    [JsonPropertyName("agentType")] public string? AgentType { get; set; }
    [JsonPropertyName("agentVersion")] public string? AgentVersion { get; set; }
    [JsonPropertyName("pluginVersion")] public string? PluginVersion { get; set; }
    [JsonPropertyName("capabilities")] public List<string>? Capabilities { get; set; }
    [JsonPropertyName("lastSeenAt")] public DateTimeOffset? LastSeenAt { get; set; }
    [JsonPropertyName("revoked")] public bool Revoked { get; set; }
    [JsonPropertyName("online")] public bool Online { get; set; }
}

public sealed class ManagerInstanceInfo
{
    [JsonPropertyName("agentId")] public string AgentId { get; set; } = "";
    [JsonPropertyName("agentName")] public string? AgentName { get; set; }
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("urlAvailable")] public bool UrlAvailable { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("generation")] public long Generation { get; set; }
    [JsonPropertyName("eventSeq")] public long EventSeq { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("lastSeenAt")] public DateTimeOffset? LastSeenAt { get; set; }
}

public sealed class ManagerDiagnosticsProxy
{
    [JsonPropertyName("httpRejected")] public ulong HttpRejected { get; set; }
    [JsonPropertyName("streamOverflow")] public ulong StreamOverflow { get; set; }
    [JsonPropertyName("tunnelDropped")] public ulong TunnelDropped { get; set; }
    [JsonPropertyName("wsOpenSent")] public ulong WsOpenSent { get; set; }
    [JsonPropertyName("wsOpenAcked")] public ulong WsOpenAcked { get; set; }
    [JsonPropertyName("wsOpenFailed")] public ulong WsOpenFailed { get; set; }
    [JsonPropertyName("wsHeartbeatFailed")] public ulong WsHeartbeatFailed { get; set; }
    [JsonPropertyName("wsClosed")] public ulong WsClosed { get; set; }
}

public sealed class ManagerDiagnostics
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("proxy")] public ManagerDiagnosticsProxy? Proxy { get; set; }
}

public sealed class ManagerDashboardSnapshot
{
    public string ServerUrl { get; init; } = "";
    public bool Authenticated { get; init; }
    public string? Username { get; init; }
    public string? ManagerVersion { get; init; }
    public IReadOnlyList<ManagerAgentInfo> Agents { get; init; } = Array.Empty<ManagerAgentInfo>();
    public IReadOnlyList<ManagerInstanceInfo> Instances { get; init; } = Array.Empty<ManagerInstanceInfo>();
    public ManagerDiagnostics? Diagnostics { get; init; }
    public string? Error { get; init; }
    public string? Notice { get; init; }
}

public sealed class ManagerApiException : Exception
{
    public int StatusCode { get; }
    public bool Unauthorized => StatusCode is 401 or 403;

    public ManagerApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// dsh-manager 管理员 API 客户端。只使用 manager 的结构化 API，不嵌入 Dashboard，也不解析 Dashboard DOM。
/// </summary>
public sealed class ManagerFrontendClient : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ManagerFrontendSettings _frontend;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private Uri? _baseUri;
    private bool _disposed;

    public ManagerFrontendClient(AppSettings settings)
    {
        _settings = settings;
        _frontend = settings.ManagerFrontend;
        _handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = _cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
        };
        _http = new HttpClient(_handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DshLauncher/" + VersionHelper.Current.TrimStart('v'));
        TryConfigureServer(CurrentServerUrl, persist: false);
    }

    public string CurrentServerUrl => string.IsNullOrWhiteSpace(_frontend.ServerUrl)
        ? _settings.Manager.ServerUrl.Trim()
        : _frontend.ServerUrl.Trim();

    public async Task<ManagerDashboardSnapshot> LoadDashboardAsync(CancellationToken ct = default)
    {
        EnsureNotDisposed();
        if (!TryConfigureServer(CurrentServerUrl, persist: false) || _baseUri == null)
        {
            return new ManagerDashboardSnapshot
            {
                ServerUrl = CurrentServerUrl,
                Authenticated = false,
                Error = string.IsNullOrWhiteSpace(CurrentServerUrl) ? "请先配置 dsh-manager 地址。" : "dsh-manager 地址无效。",
            };
        }

        var auth = await GetAuthStateAsync(ct);
        if (!auth.Ok)
        {
            return new ManagerDashboardSnapshot
            {
                ServerUrl = CurrentServerUrl,
                Authenticated = false,
                Username = _frontend.Username,
                ManagerVersion = auth.Version,
            };
        }

        var agentsTask = GetJsonAsync<AgentsResponse>("/api/v1/agents", ct);
        var instancesTask = GetJsonAsync<InstancesResponse>("/api/v1/instances", ct);
        var diagnosticsTask = GetJsonAsync<ManagerDiagnostics>("/api/v1/admin/diagnostics", ct);
        await Task.WhenAll(agentsTask, instancesTask, diagnosticsTask);

        return new ManagerDashboardSnapshot
        {
            ServerUrl = CurrentServerUrl,
            Authenticated = true,
            Username = auth.Username ?? _frontend.Username,
            ManagerVersion = auth.Version,
            Agents = agentsTask.Result?.Agents?.ToList() ?? new List<ManagerAgentInfo>(),
            Instances = instancesTask.Result?.Instances?.ToList() ?? new List<ManagerInstanceInfo>(),
            Diagnostics = diagnosticsTask.Result,
        };
    }

    public async Task<ManagerAuthState> LoginAsync(string serverUrl, string username, string password, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        if (string.IsNullOrWhiteSpace(username)) throw new InvalidOperationException("请输入 manager 用户名。");
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("请输入 manager 密码。");
        if (!TryConfigureServer(serverUrl, persist: true) || _baseUri == null)
            throw new InvalidOperationException("dsh-manager 地址必须是 http:// 或 https:// 地址。");

        ClearSessionCookie();
        using var response = await _http.PostAsJsonAsync(ApiUri("/api/v1/auth/login"), new { username, password }, _json, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw CreateApiException(response.StatusCode, body, "manager 登录失败");

        var result = Deserialize<ManagerAuthState>(body) ?? new ManagerAuthState { Ok = true, Username = username };
        _frontend.Username = result.Username ?? username;
        _frontend.SessionExpiresAt = result.ExpiresAt ?? DateTimeOffset.UtcNow.AddHours(24);
        PersistSessionCookie();
        _settings.Save();
        return result;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        EnsureNotDisposed();
        if (_baseUri != null)
        {
            try
            {
                using var response = await _http.PostAsync(ApiUri("/api/v1/auth/logout"), content: null, ct);
                _ = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Diag.Log("manager logout request failed: " + ex.Message);
            }
        }
        ClearSessionCookie();
        _frontend.ClearSession();
        _settings.Save();
    }

    public async Task<ManagerCommandResult> SendCommandAsync(string agentId, string instanceId, string action, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Agent 和实例不能为空。");
        if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("操作不能为空。", nameof(action));
        var path = $"/api/v1/instances/{Uri.EscapeDataString(agentId)}/{Uri.EscapeDataString(instanceId)}/commands";
        return await PostJsonAsync<ManagerCommandResult>(path, new { action }, ct) ?? new ManagerCommandResult { OK = true };
    }

    public async Task<ManagerOpenResult> OpenInstanceAsync(string agentId, string instanceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Agent 和实例不能为空。");
        var path = $"/api/v1/instances/{Uri.EscapeDataString(agentId)}/{Uri.EscapeDataString(instanceId)}/open";
        var result = await PostJsonAsync<ManagerOpenResult>(path, payload: null, ct)
            ?? throw new InvalidOperationException("manager 未返回实例地址。");
        if (!result.OK || string.IsNullOrWhiteSpace(result.Url))
            throw new ManagerApiException(409, result.Error ?? "dsh 实例尚未就绪。");
        if (_baseUri == null) throw new InvalidOperationException("manager 地址无效。");
        return result with { AbsoluteUrl = new Uri(_baseUri, result.Url).AbsoluteUri };
    }

    public IReadOnlyList<ManagerBrowserCookie> GetBrowserCookies()
    {
        if (_baseUri == null) return Array.Empty<ManagerBrowserCookie>();
        var result = new List<ManagerBrowserCookie>();
        foreach (Cookie cookie in _cookies.GetCookies(_baseUri))
        {
            if (!string.Equals(cookie.Name, "dsh-session", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(cookie.Name, "dsh-target", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new ManagerBrowserCookie(
                cookie.Name, cookie.Value, _baseUri.Host,
                string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                _baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
        _handler.Dispose();
    }

    private async Task<ManagerAuthState> GetAuthStateAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(ApiUri("/api/v1/auth/me"), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            ClearSessionCookie();
            _frontend.ClearSession();
            _settings.Save();
            return new ManagerAuthState();
        }
        if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, body, "读取 manager 登录状态失败");
        return Deserialize<ManagerAuthState>(body) ?? new ManagerAuthState();
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(ApiUri(path), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, body, "manager 请求失败");
        return Deserialize<T>(body);
    }

    private async Task<T?> PostJsonAsync<T>(string path, object? payload, CancellationToken ct)
    {
        using var response = payload == null
            ? await _http.PostAsync(ApiUri(path), content: null, ct)
            : await _http.PostAsJsonAsync(ApiUri(path), payload, _json, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, body, "manager 请求失败");
        return Deserialize<T>(body);
    }

    private bool TryConfigureServer(string? raw, bool persist)
    {
        raw = raw?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            _baseUri = null;
            return false;
        }
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrWhiteSpace(uri.Fragment)
            || !string.IsNullOrWhiteSpace(uri.Query))
        {
            _baseUri = null;
            return false;
        }

        var normalized = new Uri(uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/') + "/");
        if (_baseUri == null || !UriEquals(_baseUri, normalized))
        {
            _baseUri = normalized;
            ClearSessionCookie();
            if (persist)
            {
                _frontend.ServerUrl = normalized.GetLeftPart(UriPartial.Authority) + normalized.AbsolutePath.TrimEnd('/');
                _frontend.ClearSession();
                _settings.Save();
            }
            RestoreSessionCookie();
        }
        else if (persist)
        {
            _frontend.ServerUrl = normalized.GetLeftPart(UriPartial.Authority) + normalized.AbsolutePath.TrimEnd('/');
            _settings.Save();
        }
        return true;
    }

    private Uri ApiUri(string path)
    {
        if (_baseUri == null) throw new InvalidOperationException("请先配置 dsh-manager 地址。");
        return new Uri(_baseUri, path.TrimStart('/'));
    }

    private void RestoreSessionCookie()
    {
        if (_baseUri == null || string.IsNullOrWhiteSpace(_frontend.SessionCookie)) return;
        if (_frontend.SessionExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            _frontend.ClearSession();
            return;
        }
        try
        {
            _cookies.Add(_baseUri, new Cookie("dsh-session", _frontend.SessionCookie, "/", _baseUri.Host)
            {
                Secure = _baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase),
                HttpOnly = true,
            });
        }
        catch { }
    }

    private void PersistSessionCookie()
    {
        if (_baseUri == null) return;
        try
        {
            var cookie = _cookies.GetCookies(_baseUri)["dsh-session"];
            if (cookie != null && !string.IsNullOrWhiteSpace(cookie.Value)) _frontend.SessionCookie = cookie.Value;
        }
        catch { }
    }

    private void ClearSessionCookie()
    {
        if (_baseUri == null) return;
        try
        {
            _cookies.SetCookies(_baseUri, "dsh-session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/");
            _cookies.SetCookies(_baseUri, "dsh-target=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/");
        }
        catch { }
    }

    private static bool UriEquals(Uri left, Uri right) =>
        string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    private static T? Deserialize<T>(string body)
    {
        try { return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException ex) { throw new InvalidOperationException("manager 返回了无效 JSON：" + ex.Message); }
    }

    private static ManagerApiException CreateApiException(HttpStatusCode status, string body, string prefix)
    {
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)) message = error.GetString();
        }
        catch { }
        return new ManagerApiException((int)status, prefix + (string.IsNullOrWhiteSpace(message) ? $"（HTTP {(int)status}）" : "：" + message));
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ManagerFrontendClient));
    }

    private sealed class AgentsResponse { [JsonPropertyName("agents")] public List<ManagerAgentInfo> Agents { get; set; } = new(); }
    private sealed class InstancesResponse { [JsonPropertyName("instances")] public List<ManagerInstanceInfo> Instances { get; set; } = new(); }
}

public sealed class ManagerCommandResult
{
    [JsonPropertyName("ok")] public bool OK { get; set; }
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed record ManagerOpenResult(
    [property: JsonPropertyName("ok")] bool OK,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonIgnore] string? AbsoluteUrl = null);
