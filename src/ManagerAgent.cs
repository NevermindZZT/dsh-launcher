using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DshLauncher;

public sealed class ManagerAgent : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly Action<string> _log;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ProxySocket> _proxySockets = new();

    public ManagerAgent(AppSettings settings, ConnectionManager connections, Action<string>? log = null)
    {
        _settings = settings;
        _connections = connections;
        _log = log ?? Diag.Log;
    }

    public Task StartAsync()
    {
        if (!_settings.Manager.Enabled || string.IsNullOrWhiteSpace(_settings.Manager.ServerUrl)) return Task.CompletedTask;
        if (_loop is { IsCompleted: false }) return Task.CompletedTask;
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_stop.Token));
        return Task.CompletedTask;
    }

    public async Task RestartAsync()
    {
        await DisposeAsync();
        await StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop == null) return;
        _stop.Cancel();
        if (_loop != null) { try { await _loop; } catch { } }
        _stop.Dispose();
        _stop = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureEnrolledAsync(ct);
                await ConnectAndRunAsync(ct);
                delay = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log("[Manager] 连接失败: " + ex.Message);
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task EnsureEnrolledAsync(CancellationToken ct)
    {
        var m = _settings.Manager;
        if (!Uri.TryCreate(m.ServerUrl, UriKind.Absolute, out var managerUri) || (managerUri.Scheme != Uri.UriSchemeHttp && managerUri.Scheme != Uri.UriSchemeHttps)) throw new InvalidOperationException("dsh-manager 地址必须使用 http:// 或 https://");
        var fingerprint = NormalizeFingerprint(m.ServerCertificateFingerprint);
        if (managerUri.Scheme == Uri.UriSchemeHttps && fingerprint.Length != 64) throw new InvalidOperationException("HTTPS 模式请填写 manager 启动日志中的 64 位 SHA-256 TLS 指纹");
        if (managerUri.Scheme == Uri.UriSchemeHttp) _log("[Manager] 警告：当前使用 HTTP，Agent 数据可能被窃听或篡改");
        if (!string.IsNullOrWhiteSpace(m.AgentId) && !string.IsNullOrWhiteSpace(m.AgentToken)) return;
        if (string.IsNullOrWhiteSpace(m.PairingCode)) throw new InvalidOperationException("Manager 尚未配置 Agent 配对码");
        using var client = CreateHttpClient();
        var payload = new { pairingCode = m.PairingCode, name = string.IsNullOrWhiteSpace(m.AgentName) ? Environment.MachineName : m.AgentName, platform = "windows", launcherVersion = VersionHelper.Current };
        using var response = await client.PostAsJsonAsync(BuildHttpUrl("/api/v1/agents/enroll"), payload, _json, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Agent 配对失败: " + text);
        var result = JsonSerializer.Deserialize<EnrollResponse>(text, _json) ?? throw new InvalidOperationException("Manager 返回了无效配对结果");
        m.AgentId = result.AgentId;
        m.AgentToken = result.AgentToken;
        m.PairingCode = "";
        _settings.Save();
        _log("[Manager] Agent 配对成功: " + m.AgentId);
    }

    private async Task ConnectAndRunAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        ConfigureSocket(socket.Options);
        var uri = new Uri(BuildWebSocketUrl("/api/v1/agent/connect"));
        await socket.ConnectAsync(uri, ct);
        _log("[Manager] Agent 通道已连接");
        await SendAsync(socket, new AgentMessage { Type = "register", Instances = Snapshot() }, ct);
        var receive = ReceiveLoopAsync(socket, ct);
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var completed = await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(15), ct));
                if (completed == receive) { await receive; break; }
                await SendAsync(socket, new AgentMessage { Type = "heartbeat", Instances = Snapshot() }, ct);
            }
        }
        finally
        {
            try { socket.Abort(); } catch { }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            ms.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var json = Encoding.UTF8.GetString(ms.ToArray()); ms.SetLength(0);
            ManagerCommand? command;
            try { command = JsonSerializer.Deserialize<ManagerCommand>(json, _json); }
            catch (Exception parseError) { _log("[Manager] 收到无效 manager 消息: " + parseError.Message); continue; }
            if (command?.Type == "command") _ = ExecuteCommandAsync(socket, command, ct);
            else if (command?.Type == "proxy_request")
            {
                var proxy = JsonSerializer.Deserialize<ManagerProxyRequest>(json, _json);
                if (proxy != null) _ = ExecuteProxyRequestAsync(socket, proxy, ct);
            }
            else if (command?.Type == "proxy_ws_open")
            {
                var open = JsonSerializer.Deserialize<ManagerProxyWebSocketOpen>(json, _json);
                if (open != null) { _log("[Manager] 打开 dsh WebSocket: " + open.InstanceId + " " + open.Path); _ = OpenProxyWebSocketAsync(socket, open, ct); }
            }
            else if (command?.Type == "proxy_ws_frame")
            {
                var frame = JsonSerializer.Deserialize<ManagerProxyWebSocketFrame>(json, _json);
                if (frame != null) _ = ForwardProxyWebSocketFrameAsync(frame, ct);
            }
            else if (command?.Type == "proxy_ws_close")
            {
                var close = JsonSerializer.Deserialize<ManagerProxyWebSocketFrame>(json, _json);
                if (close != null) _ = CloseProxyWebSocketAsync(close);
            }
        }
    }

    private async Task ExecuteCommandAsync(ClientWebSocket socket, ManagerCommand command, CancellationToken ct)
    {
        var result = new AgentMessage { Type = "command_result", RequestId = command.RequestId, InstanceId = command.InstanceId };
        try
        {
            var connection = _connections.Connections.FirstOrDefault(c => ConnectionManager.IdOf(c) == command.InstanceId);
            if (connection == null) throw new InvalidOperationException("找不到实例: " + command.InstanceId);
            switch (command.Action.ToLowerInvariant())
            {
                case "start": await connection.StartAsync(ct); break;
                case "stop": await connection.StopAsync(); break;
                case "restart": await connection.RestartAsync(ct); break;
                case "sync": await connection.SyncFromLocalAsync(line => _log("[Manager] " + line), ct); break;
                case "update": await connection.UpdateDshAsync(line => _log("[Manager] " + line), ct); break;
                default: throw new InvalidOperationException("不支持的操作: " + command.Action);
            }
            result.OK = true;
        }
        catch (Exception ex) { result.OK = false; result.Error = ex.Message; }
        try { await SendAsync(socket, result, ct); } catch (Exception ex) { _log("[Manager] 返回命令结果失败: " + ex.Message); }
    }


    private async Task ExecuteProxyRequestAsync(ClientWebSocket socket, ManagerProxyRequest request, CancellationToken ct)
    {
        var result = new AgentMessage { Type = "proxy_response", RequestId = request.RequestId };
        try
        {
            var connection = _connections.Connections.FirstOrDefault(c => ConnectionManager.IdOf(c) == request.InstanceId);
            if (connection == null || string.IsNullOrWhiteSpace(connection.CurrentUrl)) throw new InvalidOperationException("dsh 实例未运行");
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var target = new Uri(new Uri(connection.CurrentUrl.TrimEnd('/') + "/"), request.Path.TrimStart('/'));
            var targetOrigin = target.GetLeftPart(UriPartial.Authority);
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), target);
            var bodyBytes = string.IsNullOrEmpty(request.Body) ? Array.Empty<byte>() : Convert.FromBase64String(request.Body);
            var hasContentHeaders = request.Headers.Keys.Any(k => k.StartsWith("Content-", StringComparison.OrdinalIgnoreCase));
            if (bodyBytes.Length > 0 || hasContentHeaders) message.Content = new ByteArrayContent(bodyBytes);
            foreach (var pair in request.Headers)
            {
                if (string.Equals(pair.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(pair.Key, "Origin", StringComparison.OrdinalIgnoreCase)) { message.Headers.TryAddWithoutValidation(pair.Key, targetOrigin); continue; }
                if (string.Equals(pair.Key, "Referer", StringComparison.OrdinalIgnoreCase)) { message.Headers.TryAddWithoutValidation(pair.Key, targetOrigin + "/"); continue; }
                if (pair.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    if (message.Content != null && !string.Equals(pair.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) message.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    continue;
                }
                message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            result.Status = (int)response.StatusCode;
            result.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers.Concat(response.Content.Headers)) { if (!result.Headers.ContainsKey(header.Key)) result.Headers[header.Key] = string.Join(", ", header.Value); }
            result.Body = Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync(ct));
        }
        catch (Exception ex) { result.Status = 502; result.Error = ex.Message; }
        try { await SendAsync(socket, result, ct); } catch (Exception ex) { _log("[Manager] 返回代理结果失败: " + ex.Message); }
    }


    private async Task OpenProxyWebSocketAsync(ClientWebSocket managerSocket, ManagerProxyWebSocketOpen request, CancellationToken ct)
    {
        var result = new AgentMessage { Type = "proxy_ws_open_result", RequestId = request.RequestId };
        try
        {
            var connection = _connections.Connections.FirstOrDefault(c => ConnectionManager.IdOf(c) == request.InstanceId);
            if (connection == null || string.IsNullOrWhiteSpace(connection.CurrentUrl)) throw new InvalidOperationException("dsh 实例未运行");
            var local = new ClientWebSocket();
            var target = BuildWebSocketTarget(connection.CurrentUrl, request.Path);
            var targetOrigin = new Uri(connection.CurrentUrl).GetLeftPart(UriPartial.Authority);
            local.Options.SetRequestHeader("Origin", targetOrigin);
            await local.ConnectAsync(new Uri(target), ct);
            var tunnel = new ProxySocket(local);
            if (!_proxySockets.TryAdd(request.RequestId, tunnel)) { local.Abort(); throw new InvalidOperationException("重复的 WebSocket tunnel"); }
            result.OK = true;
            await SendAsync(managerSocket, result, ct);
            _ = ReceiveProxyWebSocketAsync(managerSocket, request.RequestId, tunnel, ct);
            return;
        }
        catch (Exception ex) { result.OK = false; result.Error = ex.Message; }
        try { await SendAsync(managerSocket, result, ct); } catch { }
    }

    private async Task ReceiveProxyWebSocketAsync(ClientWebSocket managerSocket, string requestId, ProxySocket tunnel, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var messageBuffer = new MemoryStream();
        WebSocketMessageType? messageType = null;
        try
        {
            while (tunnel.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var received = await tunnel.Socket.ReceiveAsync(buffer, ct);
                if (received.MessageType == WebSocketMessageType.Close) break;
                messageType ??= received.MessageType;
                messageBuffer.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage) continue;
                await SendAsync(managerSocket, new AgentMessage { Type = "proxy_ws_frame", RequestId = requestId, FrameType = messageType == WebSocketMessageType.Binary ? "binary" : "text", Body = Convert.ToBase64String(messageBuffer.ToArray()) }, ct);
                messageBuffer.SetLength(0);
                messageType = null;
            }
        }
        catch (Exception ex) { _log("[Manager] dsh WebSocket 接收失败: " + ex.Message); }
        finally
        {
            _proxySockets.TryRemove(requestId, out _);
            try { tunnel.Socket.Abort(); } catch { }
            try { await SendAsync(managerSocket, new AgentMessage { Type = "proxy_ws_close", RequestId = requestId, Error = "dsh websocket closed" }, ct); } catch { }
        }
    }

    private async Task ForwardProxyWebSocketFrameAsync(ManagerProxyWebSocketFrame frame, CancellationToken ct)
    {
        if (!_proxySockets.TryGetValue(frame.RequestId, out var tunnel)) return;
        var data = Convert.FromBase64String(frame.Body ?? "");
        await tunnel.SendGate.WaitAsync(ct);
        try { await tunnel.Socket.SendAsync(data, frame.FrameType == "binary" ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, ct); }
        finally { tunnel.SendGate.Release(); }
    }

    private async Task CloseProxyWebSocketAsync(ManagerProxyWebSocketFrame frame)
    {
        if (_proxySockets.TryRemove(frame.RequestId, out var tunnel))
        {
            try { await tunnel.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, frame.Error ?? "browser closed", CancellationToken.None); } catch { tunnel.Socket.Abort(); }
        }
    }

    private static string BuildWebSocketTarget(string baseUrl, string path)
    {
        var uri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));
        var scheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return new UriBuilder(uri) { Scheme = scheme }.Uri.ToString();
    }

    private sealed class ProxySocket
    {
        public ClientWebSocket Socket { get; }
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public ProxySocket(ClientWebSocket socket) { Socket = socket; }
    }

    private List<ManagerInstance> Snapshot() => _connections.Connections.Select(c => new ManagerInstance
    {
        InstanceId = ConnectionManager.IdOf(c), DisplayName = c.DisplayName, Type = c.IsRemote ? "ssh" : "local", State = c.State.ToString().ToLowerInvariant(), URLAvailable = !string.IsNullOrWhiteSpace(c.CurrentUrl)
    }).ToList();

    private HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = ValidateCertificate };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
    private void ConfigureSocket(ClientWebSocketOptions options)
    {
        options.SetRequestHeader("Authorization", "Bearer " + _settings.Manager.AgentToken);
        options.SetRequestHeader("X-Agent-Id", _settings.Manager.AgentId);
        options.RemoteCertificateValidationCallback = (_, cert, _, errors) => ValidateCertificate(null, cert, null, errors);
    }
    private bool ValidateCertificate(HttpRequestMessage? _, System.Security.Cryptography.X509Certificates.X509Certificate? cert, System.Security.Cryptography.X509Certificates.X509Chain? __, System.Net.Security.SslPolicyErrors errors)
    {
        var expected = NormalizeFingerprint(_settings.Manager.ServerCertificateFingerprint);
        if (string.IsNullOrWhiteSpace(expected)) return errors == System.Net.Security.SslPolicyErrors.None;
        if (cert == null) return false;
        var actual = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
    private string BuildHttpUrl(string path) => new Uri(new Uri(_settings.Manager.ServerUrl.TrimEnd('/') + "/"), path.TrimStart('/')).ToString();
    private string BuildWebSocketUrl(string path) { var u = BuildHttpUrl(path); return u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + u[8..] : "ws://" + u[7..]; }
    private static string NormalizeFingerprint(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    private async Task SendAsync(ClientWebSocket socket, AgentMessage message, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message, _json);
        await _sendGate.WaitAsync(ct);
        try { await socket.SendAsync(data, WebSocketMessageType.Text, true, ct); }
        finally { _sendGate.Release(); }
    }

    private sealed class EnrollResponse { public string AgentId { get; set; } = ""; public string AgentToken { get; set; } = ""; }
    private sealed class AgentMessage { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string InstanceId { get; set; } = ""; public bool? OK { get; set; } public string? Error { get; set; } public int Status { get; set; } public Dictionary<string,string>? Headers { get; set; } public string? Body { get; set; } public string FrameType { get; set; } = ""; public List<ManagerInstance>? Instances { get; set; } }
    private sealed class ManagerCommand { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string InstanceId { get; set; } = ""; public string Action { get; set; } = ""; }
    private sealed class ManagerProxyRequest { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string InstanceId { get; set; } = ""; public string Method { get; set; } = "GET"; public string Path { get; set; } = "/"; public Dictionary<string,string> Headers { get; set; } = new(); public string Body { get; set; } = ""; }
    private sealed class ManagerProxyWebSocketOpen { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string InstanceId { get; set; } = ""; public string Path { get; set; } = "/"; }
    private sealed class ManagerProxyWebSocketFrame { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string FrameType { get; set; } = "text"; public string? Body { get; set; } public string? Error { get; set; } }
    private sealed class ManagerInstance { public string InstanceId { get; set; } = ""; public string DisplayName { get; set; } = ""; public string Type { get; set; } = ""; public string State { get; set; } = ""; public bool URLAvailable { get; set; } }
}
