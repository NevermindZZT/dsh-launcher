using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
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
    private readonly ConcurrentDictionary<string, HttpClient> _proxyHttpClients = new();
    private readonly ConcurrentDictionary<string, ProxyHttpOperation> _proxyHttpOperations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _proxyHttpGate = new(MaxConcurrentProxyHttpOperations, MaxConcurrentProxyHttpOperations);
    private static readonly string[] AgentCapabilities = [
        "command", "proxy.http", "proxy.websocket", "proxy.binary-response-v1",
        "proxy.http-stream-v1", "proxy.binary-websocket-frame-v1", "proxy.cancel-v1"
    ];
    // The manager enables streamed responses only for immutable non-JavaScript
    // assets. DSH data/control endpoints retain the established HTTP transport.
    private const int ProxyBufferSize = 64 * 1024;
    private const int MaxConcurrentProxyHttpOperations = 16;
    private const int MaxProxyRequestBodyBytes = 8 * 1024 * 1024;
    private const int MaxProxyResponseBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan ProxyHttpTimeout = TimeSpan.FromSeconds(60);

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
        foreach (var operation in _proxyHttpOperations.Values) operation.Cancel();
        if (_loop != null) { try { await _loop; } catch { } }
        _stop.Dispose();
        _stop = null;
        foreach (var client in _proxyHttpClients.Values) client.Dispose();
        _proxyHttpClients.Clear();
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
                _log("[Manager] 连接失败: " + DescribeException(ex));
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task EnsureEnrolledAsync(CancellationToken ct)
    {
        var m = _settings.Manager;
        if (!Uri.TryCreate(m.ServerUrl, UriKind.Absolute, out var managerUri) || (managerUri.Scheme != Uri.UriSchemeHttp && managerUri.Scheme != Uri.UriSchemeHttps)) throw new InvalidOperationException("dsh-manager 地址必须使用 http:// 或 https://");
        if (!string.IsNullOrWhiteSpace(m.AgentId) && !string.IsNullOrWhiteSpace(m.AgentToken))
        {
            using var probeClient = CreateHttpClient();
            using var probeRequest = new HttpRequestMessage(HttpMethod.Post, BuildHttpUrl("/api/v1/agent/heartbeat"));
            probeRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + m.AgentToken);
            probeRequest.Headers.TryAddWithoutValidation("X-Agent-Id", m.AgentId);
            probeRequest.Content = JsonContent.Create(new { instances = Snapshot() }, options: _json);
            using var probe = await probeClient.SendAsync(probeRequest, ct);
            if (probe.IsSuccessStatusCode) return;
            if (probe.StatusCode != HttpStatusCode.Unauthorized && probe.StatusCode != HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Manager Agent 凭证检查失败: HTTP " + (int)probe.StatusCode);
            _log("[Manager] 保存的 Agent 凭证已失效，将重新配对");
            m.AgentId = "";
            m.AgentToken = "";
            _settings.Save();
        }
        if (string.IsNullOrWhiteSpace(m.PairingCode)) throw new InvalidOperationException("Manager 尚未配置 Agent 配对码");
        using var client = CreateHttpClient();
        var payload = new { pairingCode = m.PairingCode, name = string.IsNullOrWhiteSpace(m.AgentName) ? Environment.MachineName : m.AgentName, platform = "windows", launcherVersion = VersionHelper.Current, agentType = "launcher", agentVersion = VersionHelper.Current, capabilities = AgentCapabilities };
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
        await SendAsync(socket, new AgentMessage { Type = "register", AgentType = "launcher", AgentVersion = VersionHelper.Current, Capabilities = AgentCapabilities, Instances = Snapshot() }, ct);
        var receive = ReceiveLoopAsync(socket, ct);
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var completed = await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(15), ct));
                if (completed == receive) { await receive; break; }
                await SendAsync(socket, new AgentMessage { Type = "heartbeat", AgentType = "launcher", AgentVersion = VersionHelper.Current, Capabilities = AgentCapabilities, Instances = Snapshot() }, ct);
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
            var payload = ms.ToArray(); ms.SetLength(0);
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                _ = HandleBinaryManagerMessageAsync(socket, payload, ct);
                continue;
            }
            var json = Encoding.UTF8.GetString(payload);
            ManagerCommand? command;
            try { command = JsonSerializer.Deserialize<ManagerCommand>(json, _json); }
            catch (Exception parseError) { _log("[Manager] 收到无效 manager 消息: " + parseError.Message); continue; }
            if (command?.Type == "command") _ = ExecuteCommandAsync(socket, command, ct);
            else if (command?.Type == "proxy_request")
            {
                var proxy = JsonSerializer.Deserialize<ManagerProxyRequest>(json, _json);
                if (proxy != null)
                {
                    if (TryStartProxyHttpOperation(proxy.RequestId, ct, out var operation, out var rejection))
                        _ = ExecuteProxyRequestAsync(socket, proxy, operation);
                    else
                        _ = RejectProxyRequestAsync(socket, proxy.RequestId, rejection, ct);
                }
            }
            else if (command?.Type == "proxy_cancel")
            {
                var cancel = JsonSerializer.Deserialize<ManagerProxyCancel>(json, _json);
                if (cancel != null) CancelProxyHttpOperation(cancel.RequestId);
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

    private async Task HandleBinaryManagerMessageAsync(ClientWebSocket socket, byte[] payload, CancellationToken ct)
    {
        var separator = Array.IndexOf(payload, (byte)'\n');
        if (separator <= 0) { _log("[Manager] 收到无效二进制 manager 消息"); return; }
        try
        {
            var header = Encoding.UTF8.GetString(payload, 0, separator);
            var frame = JsonSerializer.Deserialize<ManagerProxyWebSocketFrame>(header, _json);
            if (frame?.Type is "proxy_ws_frame_binary" or "proxy_ws_frame")
            {
                // The binary envelope carries raw bytes, but its header still
                // determines whether the local DSH WebSocket expects text or binary.
                // Coercing text frames to binary breaks the remote mux handshake.
                await ForwardProxyWebSocketFrameAsync(frame, payload.AsMemory(separator + 1), ct);
            }
        }
        catch (Exception ex) { _log("[Manager] 处理二进制 manager 消息失败: " + ex.Message); }
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


    private async Task ExecuteProxyRequestAsync(ClientWebSocket socket, ManagerProxyRequest request, ProxyHttpOperation operation)
    {
        try { await ExecuteProxyRequestCoreAsync(socket, request, operation); }
        finally { EndProxyHttpOperation(request.RequestId, operation); }
    }

    private async Task ExecuteProxyRequestCoreAsync(ClientWebSocket socket, ManagerProxyRequest request, ProxyHttpOperation operation)
    {
        var result = new AgentMessage { Type = "proxy_response", RequestId = request.RequestId };
        byte[]? binaryBody = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            timeout.CancelAfter(ProxyHttpTimeout);
            var token = timeout.Token;
            var connection = _connections.Connections.FirstOrDefault(c => ConnectionManager.IdOf(c) == request.InstanceId);
            if (connection == null || string.IsNullOrWhiteSpace(connection.CurrentUrl)) throw new InvalidOperationException("dsh 实例未运行");
            var target = request.Bootstrap && request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && request.Path == "/"
                ? new Uri(connection.CurrentUrl)
                : BuildLocalHttpTarget(connection.CurrentUrl, request.Path);
            var targetOrigin = target.GetLeftPart(UriPartial.Authority);
            var client = GetProxyHttpClient(request.InstanceId, targetOrigin);
            var acceptedEncoding = request.Headers.TryGetValue("Accept-Encoding", out var requestEncoding) ? requestEncoding : "";
            var acceptsGzip = acceptedEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase);
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), target);
            var bodyBytes = DecodeProxyRequestBody(request.Body);
            var hasContentHeaders = request.Headers.Keys.Any(k => k.StartsWith("Content-", StringComparison.OrdinalIgnoreCase));
            if (bodyBytes.Length > 0 || hasContentHeaders) message.Content = new ByteArrayContent(bodyBytes);
            foreach (var pair in request.Headers)
            {
                if (string.Equals(pair.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (pair.Key.StartsWith("X-Dsh-Manager-", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(pair.Key, "Accept-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(pair.Key, "Origin", StringComparison.OrdinalIgnoreCase)) { message.Headers.TryAddWithoutValidation(pair.Key, targetOrigin); continue; }
                if (string.Equals(pair.Key, "Referer", StringComparison.OrdinalIgnoreCase)) { message.Headers.TryAddWithoutValidation(pair.Key, targetOrigin + "/"); continue; }
                if (pair.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    if (message.Content != null && !string.Equals(pair.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) message.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    continue;
                }
                message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
            message.Headers.TryAddWithoutValidation("Accept-Encoding", acceptsGzip ? "gzip" : "identity");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            result.Status = (int)response.StatusCode;
            result.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                if (!result.Headers.ContainsKey(header.Key)) result.Headers[header.Key] = string.Join(", ", header.Value);
            }
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies)) result.SetCookies = setCookies.ToList();
            if (request.StreamResponse)
            {
                await StreamProxyResponseAsync(socket, result, response, token);
                return;
            }
            var responseBytes = await ReadProxyResponseBodyAsync(response.Content, token);
            if (request.BinaryResponse) binaryBody = responseBytes;
            else result.Body = Convert.ToBase64String(responseBytes);
            _log("[Manager] 代理响应: " + request.Method + " HTTP " + (int)response.StatusCode + " bytes=" + responseBytes.Length);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _log("[Manager] 代理请求已取消");
            return;
        }
        catch (ProxyPayloadTooLargeException)
        {
            result.Status = 413;
            result.Error = "proxy payload too large";
        }
        catch (OperationCanceledException)
        {
            result.Status = 504;
            result.Error = "proxy request timed out";
        }
        catch (Exception ex) { result.Status = 502; result.Error = ex.Message; }
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            if (request.BinaryResponse && binaryBody != null && string.IsNullOrEmpty(result.Error))
                await SendProxyResponseBinaryAsync(socket, result, binaryBody, operation.Token);
            else
                await SendAsync(socket, result, operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception ex) { _log("[Manager] 返回代理结果失败: " + ex.Message); }
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
            if (request.Headers.TryGetValue("Cookie", out var cookie) && !string.IsNullOrWhiteSpace(cookie)) local.Options.SetRequestHeader("Cookie", cookie);
            await local.ConnectAsync(new Uri(target), ct);
            var tunnel = new ProxySocket(local, request.BinaryFrames);
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
                var data = messageBuffer.ToArray();
                if (messageType == WebSocketMessageType.Binary && tunnel.BinaryFrames)
                    await SendBinaryEnvelopeAsync(managerSocket, new AgentMessage { Type = "proxy_ws_frame_binary", RequestId = requestId, FrameType = "binary" }, data, ct);
                else
                    await SendAsync(managerSocket, new AgentMessage { Type = "proxy_ws_frame", RequestId = requestId, FrameType = messageType == WebSocketMessageType.Binary ? "binary" : "text", Body = Convert.ToBase64String(data) }, ct);
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

    private Task ForwardProxyWebSocketFrameAsync(ManagerProxyWebSocketFrame frame, CancellationToken ct) =>
        ForwardProxyWebSocketFrameAsync(frame, Convert.FromBase64String(frame.Body ?? ""), ct);

    private async Task ForwardProxyWebSocketFrameAsync(ManagerProxyWebSocketFrame frame, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!_proxySockets.TryGetValue(frame.RequestId, out var tunnel)) return;
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

    private bool TryStartProxyHttpOperation(string requestId, CancellationToken stopToken, out ProxyHttpOperation operation, out string rejection)
    {
        operation = null!;
        rejection = "";
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 256) { rejection = "invalid proxy request id"; return false; }
        if (_proxyHttpOperations.ContainsKey(requestId)) { rejection = "duplicate proxy request"; return false; }
        if (!_proxyHttpGate.Wait(0)) { rejection = "proxy busy"; return false; }
        var candidate = new ProxyHttpOperation(CancellationTokenSource.CreateLinkedTokenSource(stopToken));
        if (_proxyHttpOperations.TryAdd(requestId, candidate)) { operation = candidate; return true; }
        candidate.Dispose();
        _proxyHttpGate.Release();
        rejection = "duplicate proxy request";
        return false;
    }

    private void CancelProxyHttpOperation(string requestId)
    {
        if (_proxyHttpOperations.TryGetValue(requestId, out var operation)) operation.Cancel();
    }

    private void EndProxyHttpOperation(string requestId, ProxyHttpOperation operation)
    {
        ((ICollection<KeyValuePair<string, ProxyHttpOperation>>)_proxyHttpOperations).Remove(new KeyValuePair<string, ProxyHttpOperation>(requestId, operation));
        operation.Dispose();
        _proxyHttpGate.Release();
    }

    private async Task RejectProxyRequestAsync(ClientWebSocket socket, string requestId, string error, CancellationToken ct)
    {
        try { await SendAsync(socket, new AgentMessage { Type = "proxy_response", RequestId = requestId, Status = error == "proxy busy" ? 429 : 409, Error = error }, ct); }
        catch (Exception ex) { _log("[Manager] 返回代理拒绝结果失败: " + ex.Message); }
    }

    private static byte[] DecodeProxyRequestBody(string body)
    {
        if (string.IsNullOrEmpty(body)) return Array.Empty<byte>();
        if (body.Length > ((MaxProxyRequestBodyBytes + 2) / 3) * 4) throw new ProxyPayloadTooLargeException();
        var bytes = Convert.FromBase64String(body);
        if (bytes.Length > MaxProxyRequestBodyBytes) throw new ProxyPayloadTooLargeException();
        return bytes;
    }

    private static async Task<byte[]> ReadProxyResponseBodyAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is long length && length > MaxProxyResponseBytes) throw new ProxyPayloadTooLargeException();
        await using var stream = await content.ReadAsStreamAsync(ct);
        await using var output = new MemoryStream();
        var buffer = new byte[ProxyBufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            if (output.Length + read > MaxProxyResponseBytes) throw new ProxyPayloadTooLargeException();
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return output.ToArray();
    }

    private HttpClient GetProxyHttpClient(string instanceId, string targetOrigin)
    {
        var key = instanceId + "|" + targetOrigin;
        return _proxyHttpClients.GetOrAdd(key, _ => new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = Timeout.InfiniteTimeSpan });
    }

    private async Task StreamProxyResponseAsync(ClientWebSocket socket, AgentMessage response, HttpResponseMessage localResponse, CancellationToken ct)
    {
        if (localResponse.Content.Headers.ContentLength is long length && length > MaxProxyResponseBytes) throw new ProxyPayloadTooLargeException();
        response.Type = "proxy_response_start";
        await SendAsync(socket, response, ct);
        long total = 0;
        try
        {
            await using var stream = await localResponse.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[ProxyBufferSize];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                if (total + read > MaxProxyResponseBytes) throw new ProxyPayloadTooLargeException();
                await SendBinaryEnvelopeAsync(socket, new AgentMessage { Type = "proxy_response_chunk_binary", RequestId = response.RequestId }, buffer.AsMemory(0, read), ct);
                total += read;
            }
            await SendAsync(socket, new AgentMessage { Type = "proxy_response_end", RequestId = response.RequestId }, ct);
            _log("[Manager] 流式代理响应: HTTP " + response.Status + " bytes=" + total + " request=" + response.RequestId);
        }
        catch (Exception ex)
        {
            try { await SendAsync(socket, new AgentMessage { Type = "proxy_response_end", RequestId = response.RequestId, Error = ex.Message }, ct); } catch { }
        }
    }

    private static Uri BuildLocalHttpTarget(string baseUrl, string path)
    {
        var baseUri = new Uri(baseUrl);
        var origin = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/");
        return string.IsNullOrEmpty(path) || path == "/" ? origin : new Uri(origin, path.TrimStart('/'));
    }
    private static string BuildWebSocketTarget(string baseUrl, string path)
    {
        var uri = BuildLocalHttpTarget(baseUrl, path);
        var scheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return new UriBuilder(uri) { Scheme = scheme }.Uri.ToString();
    }

    private sealed class ProxyHttpOperation : IDisposable
    {
        public CancellationTokenSource Cancellation { get; }
        public CancellationToken Token => Cancellation.Token;
        public bool IsCancellationRequested => Cancellation.IsCancellationRequested;
        public ProxyHttpOperation(CancellationTokenSource cancellation) => Cancellation = cancellation;
        public void Cancel() { try { Cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        public void Dispose() => Cancellation.Dispose();
    }

    private sealed class ProxyPayloadTooLargeException : Exception { }

    private sealed class ProxySocket
    {
        public ClientWebSocket Socket { get; }
        public bool BinaryFrames { get; }
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public ProxySocket(ClientWebSocket socket, bool binaryFrames) { Socket = socket; BinaryFrames = binaryFrames; }
    }

    private List<ManagerInstance> Snapshot() => _connections.Connections.Select(c => new ManagerInstance
    {
        InstanceId = ConnectionManager.IdOf(c), DisplayName = c.DisplayName, Type = c.IsRemote ? "ssh" : "local", State = c.State.ToString().ToLowerInvariant(), URLAvailable = !string.IsNullOrWhiteSpace(c.CurrentUrl), StartupUrl = StartupUrlOf(c.CurrentUrl)
    }).ToList();
    private static string? StartupUrlOf(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        var hasToken = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Any(x => x.StartsWith("token=", StringComparison.OrdinalIgnoreCase) && x.Length > 6);
        return hasToken ? uri.ToString() : null;
    }

    private HttpClient CreateHttpClient() => new(new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };
    private void ConfigureSocket(ClientWebSocketOptions options)
    {
        options.SetRequestHeader("Authorization", "Bearer " + _settings.Manager.AgentToken);
        options.SetRequestHeader("X-Agent-Id", _settings.Manager.AgentId);
    }
    private string BuildHttpUrl(string path) => new Uri(new Uri(_settings.Manager.ServerUrl.TrimEnd('/') + "/"), path.TrimStart('/')).ToString();
    private string BuildWebSocketUrl(string path) { var u = BuildHttpUrl(path); return u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + u[8..] : "ws://" + u[7..]; }
    private static string DescribeException(Exception error)
    {
        var messages = new List<string>();
        for (var current = error; current != null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
                messages.Add(current.GetType().Name + ": " + current.Message);
        }
        return string.Join(" -> ", messages);
    }
    private Task SendProxyResponseBinaryAsync(ClientWebSocket socket, AgentMessage message, byte[] body, CancellationToken ct)
    {
        message.Type = "proxy_response_binary";
        message.Body = null;
        return SendBinaryEnvelopeAsync(socket, message, body, ct);
    }

    private async Task SendBinaryEnvelopeAsync(ClientWebSocket socket, AgentMessage message, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(message, _json);
        var data = new byte[header.Length + 1 + body.Length];
        Buffer.BlockCopy(header, 0, data, 0, header.Length);
        data[header.Length] = (byte)'\n';
        body.CopyTo(data.AsMemory(header.Length + 1));
        await _sendGate.WaitAsync(ct);
        try { await socket.SendAsync(data, WebSocketMessageType.Binary, true, ct); }
        finally { _sendGate.Release(); }
    }

    private async Task SendAsync(ClientWebSocket socket, AgentMessage message, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message, _json);
        await _sendGate.WaitAsync(ct);
        try { await socket.SendAsync(data, WebSocketMessageType.Text, true, ct); }
        finally { _sendGate.Release(); }
    }

    private sealed class EnrollResponse { public string AgentId { get; set; } = ""; public string AgentToken { get; set; } = ""; }
    private sealed class AgentMessage { public string Type { get; set; } = ""; public string AgentType { get; set; } = ""; public string AgentVersion { get; set; } = ""; public string PluginVersion { get; set; } = ""; public string[]? Capabilities { get; set; } public string RequestId { get; set; } = ""; public string InstanceId { get; set; } = ""; public bool? OK { get; set; } public string? Error { get; set; } public int Status { get; set; } public Dictionary<string,string>? Headers { get; set; } public List<string>? SetCookies { get; set; } public string? Body { get; set; } public string FrameType { get; set; } = ""; public List<ManagerInstance>? Instances { get; set; } }
    private sealed class ManagerCommand { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string InstanceId { get; set; } = ""; public string Action { get; set; } = ""; }
    private sealed class ManagerProxyRequest { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string InstanceId { get; set; } = ""; public string Method { get; set; } = "GET"; public string Path { get; set; } = "/"; public Dictionary<string,string> Headers { get; set; } = new(); public string Body { get; set; } = ""; public bool Bootstrap { get; set; } public bool BinaryResponse { get; set; } public bool StreamResponse { get; set; } }
    private sealed class ManagerProxyCancel { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string? Reason { get; set; } }
    private sealed class ManagerProxyWebSocketOpen { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string InstanceId { get; set; } = ""; public string Path { get; set; } = "/"; public Dictionary<string,string> Headers { get; set; } = new(); public bool BinaryFrames { get; set; } }
    private sealed class ManagerProxyWebSocketFrame { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string FrameType { get; set; } = "text"; public string? Body { get; set; } public string? Error { get; set; } }
    private sealed class ManagerInstance { public string InstanceId { get; set; } = ""; public string DisplayName { get; set; } = ""; public string Type { get; set; } = ""; public string State { get; set; } = ""; public bool URLAvailable { get; set; } public string? StartupUrl { get; set; } }
}
