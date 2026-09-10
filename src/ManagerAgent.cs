using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DshLauncher;

public sealed class ManagerAgent : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly Action<string> _log;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private readonly ConcurrentDictionary<string, ProxySocket> _proxySockets = new();
    private readonly ConcurrentDictionary<string, HttpClient> _proxyHttpClients = new();
    private readonly ConcurrentDictionary<string, ProxyHttpOperation> _proxyHttpOperations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _proxyHttpGate = new(MaxConcurrentProxyHttpOperations, MaxConcurrentProxyHttpOperations);
    private readonly ConcurrentDictionary<string, DshPendingInteraction> _relayedInteractions = new(StringComparer.Ordinal);
    private ManagerOutboundScheduler? _activeScheduler;
    private int _remoteEventsSupported;
    private static readonly string[] AgentCapabilities = [
        "command", "proxy.http", "proxy.websocket", "proxy.binary-response-v1",
        "proxy.http-stream-v1", "proxy.http-request-stream-v1", "proxy.binary-websocket-frame-v1", "proxy.cancel-v1", "remote.events-v1"
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
        await using var scheduler = new ManagerOutboundScheduler(socket, ct);
        _activeScheduler = scheduler;
        await SendAsync(scheduler, new AgentMessage { Type = "register", AgentType = "launcher", AgentVersion = VersionHelper.Current, Capabilities = AgentCapabilities, Instances = Snapshot() }, ct);
        var receive = ReceiveLoopAsync(scheduler, ct);
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var completed = await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(15), ct));
                if (completed == receive) { await receive; break; }
                await SendAsync(scheduler, new AgentMessage { Type = "heartbeat", AgentType = "launcher", AgentVersion = VersionHelper.Current, Capabilities = AgentCapabilities, Instances = Snapshot() }, ct);
            }
        }
        finally
        {
            if (ReferenceEquals(_activeScheduler, scheduler)) _activeScheduler = null;
            Volatile.Write(ref _remoteEventsSupported, 0);
            _relayedInteractions.Clear();
            try { socket.Abort(); } catch { }
        }
    }

    private async Task ReceiveLoopAsync(ManagerOutboundScheduler scheduler, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (scheduler.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await scheduler.Socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            ms.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var payload = ms.ToArray(); ms.SetLength(0);
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                await HandleBinaryManagerMessageAsync(scheduler, payload, ct);
                continue;
            }
            var json = Encoding.UTF8.GetString(payload);
            ManagerCommand? command;
            try { command = JsonSerializer.Deserialize<ManagerCommand>(json, _json); }
            catch (Exception parseError) { _log("[Manager] 收到无效 manager 消息: " + parseError.Message); continue; }
            if (command?.Type == "hello")
            {
                Volatile.Write(ref _remoteEventsSupported, command.Capabilities?.Contains("remote.events-v1", StringComparer.Ordinal) == true ? 1 : 0);
                _log(Volatile.Read(ref _remoteEventsSupported) == 1 ? "[Manager] 已协商远程审批事件中继" : "[Manager] 服务端未提供远程审批事件中继，将使用本机浮窗");
            }
            else if (command?.Type == "remote_event_result") _ = HandleRelayedResultAsync(command);
            else if (command?.Type == "remote_event_cancel") { if (!string.IsNullOrWhiteSpace(command.RequestId)) _relayedInteractions.TryRemove(command.RequestId, out _); }
            else if (command?.Type == "command") _ = ExecuteCommandAsync(scheduler, command, ct);
            else if (command?.Type is "proxy_request" or "proxy_request_start")
            {
                var proxy = JsonSerializer.Deserialize<ManagerProxyRequest>(json, _json);
                if (proxy != null)
                {
                    var streamedRequest = command.Type == "proxy_request_start";
                    if (TryStartProxyHttpOperation(proxy.RequestId, streamedRequest, ct, out var operation, out var rejection))
                        _ = ExecuteProxyRequestAsync(scheduler, proxy, operation);
                    else
                        _ = RejectProxyRequestAsync(scheduler, proxy.RequestId, rejection, ct);
                }
            }
            else if (command?.Type == "proxy_request_end")
            {
                var end = JsonSerializer.Deserialize<ManagerProxyRequestEnd>(json, _json);
                if (end != null) CompleteProxyHttpRequestBody(end.RequestId);
            }
            else if (command?.Type == "proxy_cancel")
            {
                var cancel = JsonSerializer.Deserialize<ManagerProxyCancel>(json, _json);
                if (cancel != null) CancelProxyHttpOperation(cancel.RequestId);
            }
            else if (command?.Type == "proxy_ws_open")
            {
                var open = JsonSerializer.Deserialize<ManagerProxyWebSocketOpen>(json, _json);
                if (open != null) { _log("[Manager] 打开 dsh WebSocket: " + open.InstanceId + " " + open.Path); _ = OpenProxyWebSocketAsync(scheduler, open, ct); }
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

    private Task HandleRelayedResultAsync(ManagerCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.RequestId) || !_relayedInteractions.TryRemove(command.RequestId, out var interaction)) return Task.CompletedTask;
        _log("[Manager] 收到远程交互结果，但本轮尚未绑定 Manager 前端回答者: " + interaction.SourceName);
        return Task.CompletedTask;
    }

    private async Task HandleBinaryManagerMessageAsync(ManagerOutboundScheduler scheduler, byte[] payload, CancellationToken ct)
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
            else if (frame?.Type == "proxy_request_chunk_binary")
            {
                AppendProxyHttpRequestBody(frame.RequestId, payload[(separator + 1)..]);
            }
        }
        catch (Exception ex) { _log("[Manager] 处理二进制 manager 消息失败: " + ex.Message); }
    }

    private async Task ExecuteCommandAsync(ManagerOutboundScheduler scheduler, ManagerCommand command, CancellationToken ct)
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
        try { await SendAsync(scheduler, result, ct); } catch (Exception ex) { _log("[Manager] 返回命令结果失败: " + ex.Message); }
    }


    private async Task ExecuteProxyRequestAsync(ManagerOutboundScheduler scheduler, ManagerProxyRequest request, ProxyHttpOperation operation)
    {
        try { await ExecuteProxyRequestCoreAsync(scheduler, request, operation); }
        finally { EndProxyHttpOperation(request.RequestId, operation); }
    }

    private async Task ExecuteProxyRequestCoreAsync(ManagerOutboundScheduler scheduler, ManagerProxyRequest request, ProxyHttpOperation operation)
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
            var hasContentHeaders = request.Headers.Keys.Any(k => k.StartsWith("Content-", StringComparison.OrdinalIgnoreCase));
            if (operation.IsStreamedRequest)
            {
                message.Content = new StreamedProxyRequestContent(operation);
            }
            else
            {
                var bodyBytes = DecodeProxyRequestBody(request.Body);
                if (bodyBytes.Length > 0 || hasContentHeaders) message.Content = new ByteArrayContent(bodyBytes);
            }
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
                await StreamProxyResponseAsync(scheduler, result, response, token);
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
                await SendProxyResponseBinaryAsync(scheduler, result, binaryBody, operation.Token);
            else
                await SendAsync(scheduler, result, operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception ex) { _log("[Manager] 返回代理结果失败: " + ex.Message); }
    }


    private async Task OpenProxyWebSocketAsync(ManagerOutboundScheduler scheduler, ManagerProxyWebSocketOpen request, CancellationToken ct)
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
            await SendAsync(scheduler, result, ct);
            _ = ReceiveProxyWebSocketAsync(scheduler, request.RequestId, tunnel, ct);
            return;
        }
        catch (Exception ex) { result.OK = false; result.Error = ex.Message; }
        try { await SendAsync(scheduler, result, ct); } catch { }
    }

    private async Task ReceiveProxyWebSocketAsync(ManagerOutboundScheduler scheduler, string requestId, ProxySocket tunnel, CancellationToken ct)
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
                    await SendBinaryEnvelopeAsync(scheduler, new AgentMessage { Type = "proxy_ws_frame_binary", RequestId = requestId, FrameType = "binary" }, data, ct);
                else
                    await SendAsync(scheduler, new AgentMessage { Type = "proxy_ws_frame", RequestId = requestId, FrameType = messageType == WebSocketMessageType.Binary ? "binary" : "text", Body = Convert.ToBase64String(data) }, ct);
                messageBuffer.SetLength(0);
                messageType = null;
            }
        }
        catch (Exception ex) { _log("[Manager] dsh WebSocket 接收失败: " + ex.Message); }
        finally
        {
            _proxySockets.TryRemove(requestId, out _);
            try { tunnel.Socket.Abort(); } catch { }
            try { await SendAsync(scheduler, new AgentMessage { Type = "proxy_ws_close", RequestId = requestId, Error = "dsh websocket closed" }, ct); } catch { }
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

    private bool TryStartProxyHttpOperation(string requestId, bool streamedRequest, CancellationToken stopToken, out ProxyHttpOperation operation, out string rejection)
    {
        operation = null!;
        rejection = "";
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 256) { rejection = "invalid proxy request id"; return false; }
        if (_proxyHttpOperations.ContainsKey(requestId)) { rejection = "duplicate proxy request"; return false; }
        if (!_proxyHttpGate.Wait(0)) { rejection = "proxy busy"; return false; }
        var candidate = new ProxyHttpOperation(CancellationTokenSource.CreateLinkedTokenSource(stopToken), streamedRequest);
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

    private void AppendProxyHttpRequestBody(string requestId, byte[] chunk)
    {
        if (!_proxyHttpOperations.TryGetValue(requestId, out var operation) || !operation.TryAppendRequestBody(chunk, out var error)) return;
        if (error != null) _log("[Manager] 流式代理请求中止: " + requestId + " " + error.Message);
    }

    private void CompleteProxyHttpRequestBody(string requestId)
    {
        if (_proxyHttpOperations.TryGetValue(requestId, out var operation)) operation.CompleteRequestBody();
    }

    private void EndProxyHttpOperation(string requestId, ProxyHttpOperation operation)
    {
        ((ICollection<KeyValuePair<string, ProxyHttpOperation>>)_proxyHttpOperations).Remove(new KeyValuePair<string, ProxyHttpOperation>(requestId, operation));
        operation.Dispose();
        _proxyHttpGate.Release();
    }

    private async Task RejectProxyRequestAsync(ManagerOutboundScheduler scheduler, string requestId, string error, CancellationToken ct)
    {
        try { await SendAsync(scheduler, new AgentMessage { Type = "proxy_response", RequestId = requestId, Status = error == "proxy busy" ? 429 : 409, Error = error }, ct); }
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

    private async Task StreamProxyResponseAsync(ManagerOutboundScheduler scheduler, AgentMessage response, HttpResponseMessage localResponse, CancellationToken ct)
    {
        if (localResponse.Content.Headers.ContentLength is long length && length > MaxProxyResponseBytes) throw new ProxyPayloadTooLargeException();
        response.Type = "proxy_response_start";
        await SendAsync(scheduler, response, ct);
        long total = 0;
        try
        {
            await using var stream = await localResponse.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[ProxyBufferSize];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                if (total + read > MaxProxyResponseBytes) throw new ProxyPayloadTooLargeException();
                await SendBinaryEnvelopeAsync(scheduler, new AgentMessage { Type = "proxy_response_chunk_binary", RequestId = response.RequestId }, buffer.AsMemory(0, read), ct);
                total += read;
            }
            await SendAsync(scheduler, new AgentMessage { Type = "proxy_response_end", RequestId = response.RequestId }, ct);
            _log("[Manager] 流式代理响应: HTTP " + response.Status + " bytes=" + total + " request=" + response.RequestId);
        }
        catch (Exception ex)
        {
            try { await SendAsync(scheduler, new AgentMessage { Type = "proxy_response_end", RequestId = response.RequestId, Error = ex.Message }, ct); } catch { }
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
        private const int RequestBodyQueueChunks = 8;
        private long _requestBodyBytes;
        private int _requestBodyCompleted;
        private readonly Channel<byte[]>? _requestBody;

        public CancellationTokenSource Cancellation { get; }
        public CancellationToken Token => Cancellation.Token;
        public bool IsCancellationRequested => Cancellation.IsCancellationRequested;
        public bool IsStreamedRequest => _requestBody != null;
        public ChannelReader<byte[]> RequestBody => _requestBody?.Reader ?? throw new InvalidOperationException("request body is not streamed");

        public ProxyHttpOperation(CancellationTokenSource cancellation, bool streamedRequest)
        {
            Cancellation = cancellation;
            if (streamedRequest)
                _requestBody = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(RequestBodyQueueChunks)
                { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false });
        }

        public bool TryAppendRequestBody(byte[] chunk, out Exception? error)
        {
            error = null;
            if (_requestBody == null || Volatile.Read(ref _requestBodyCompleted) != 0) return false;
            if (chunk.Length == 0 || chunk.Length > ProxyBufferSize)
            {
                error = new InvalidOperationException("invalid streamed proxy request chunk");
                CompleteRequestBody(error);
                return false;
            }
            if (Interlocked.Add(ref _requestBodyBytes, chunk.Length) > MaxProxyRequestBodyBytes)
            {
                error = new ProxyPayloadTooLargeException();
                CompleteRequestBody(error);
                return false;
            }
            if (_requestBody.Writer.TryWrite(chunk)) return true;
            error = new InvalidOperationException("streamed proxy request exceeded bounded buffer");
            CompleteRequestBody(error);
            return false;
        }

        public void CompleteRequestBody(Exception? error = null)
        {
            if (_requestBody != null && Interlocked.Exchange(ref _requestBodyCompleted, 1) == 0) _requestBody.Writer.TryComplete(error);
        }

        public void Cancel()
        {
            CompleteRequestBody(new OperationCanceledException());
            try { Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            CompleteRequestBody();
            Cancellation.Dispose();
        }
    }

    private sealed class StreamedProxyRequestContent : HttpContent
    {
        private readonly ProxyHttpOperation _operation;
        public StreamedProxyRequestContent(ProxyHttpOperation operation) => _operation = operation;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteChunksAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => WriteChunksAsync(stream, cancellationToken);
        private async Task WriteChunksAsync(Stream stream, CancellationToken cancellationToken)
        {
            await foreach (var chunk in _operation.RequestBody.ReadAllAsync(cancellationToken))
                await stream.WriteAsync(chunk.AsMemory(), cancellationToken);
        }
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
    private Task SendProxyResponseBinaryAsync(ManagerOutboundScheduler scheduler, AgentMessage message, byte[] body, CancellationToken ct)
    {
        message.Type = "proxy_response_binary";
        message.Body = null;
        return SendBinaryEnvelopeAsync(scheduler, message, body, ct);
    }

    private Task SendBinaryEnvelopeAsync(ManagerOutboundScheduler scheduler, AgentMessage message, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(message, _json);
        var data = new byte[header.Length + 1 + body.Length];
        Buffer.BlockCopy(header, 0, data, 0, header.Length);
        data[header.Length] = (byte)'\n';
        body.CopyTo(data.AsMemory(header.Length + 1));
        return scheduler.EnqueueAsync(data, WebSocketMessageType.Binary, PriorityFor(message), message.RequestId, ct);
    }

    private Task SendAsync(ManagerOutboundScheduler scheduler, AgentMessage message, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message, _json);
        return scheduler.EnqueueAsync(data, WebSocketMessageType.Text, PriorityFor(message), message.RequestId, ct);
    }

    private static OutboundPriority PriorityFor(AgentMessage message) => message.Type switch
    {
        "register" or "heartbeat" or "command_result" or "proxy_response_end" => OutboundPriority.Critical,
        "proxy_response_chunk_binary" => OutboundPriority.Bulk,
        "proxy_ws_frame" or "proxy_ws_frame_binary" => OutboundPriority.Interactive,
        _ => OutboundPriority.Interactive
    };

    private enum OutboundPriority { Critical, Interactive, Bulk }

    // One writer owns ClientWebSocket.SendAsync. Per-class bounded channels and a
    // weighted schedule keep lifecycle/control traffic responsive without letting
    // a continuous stream of chunks or WebSocket frames starve other traffic.
    private sealed class ManagerOutboundScheduler : IAsyncDisposable
    {
        private static readonly OutboundPriority[] Schedule = [
            OutboundPriority.Critical, OutboundPriority.Critical, OutboundPriority.Critical, OutboundPriority.Critical,
            OutboundPriority.Critical, OutboundPriority.Critical, OutboundPriority.Critical, OutboundPriority.Critical,
            OutboundPriority.Interactive, OutboundPriority.Interactive, OutboundPriority.Interactive, OutboundPriority.Interactive,
            OutboundPriority.Bulk
        ];
        private readonly Channel<OutboundFrame> _critical = CreateChannel(16);
        private readonly Channel<OutboundFrame> _interactive = CreateChannel(32);
        private readonly Channel<OutboundFrame> _bulk = CreateChannel(32);
        private readonly CancellationTokenSource _stop;
        private readonly Task _writer;
        private int _scheduleIndex;

        public ClientWebSocket Socket { get; }

        public ManagerOutboundScheduler(ClientWebSocket socket, CancellationToken stop)
        {
            Socket = socket;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(stop);
            _writer = Task.Run(WriteLoopAsync);
        }

        public async Task EnqueueAsync(byte[] data, WebSocketMessageType type, OutboundPriority priority, string flowId, CancellationToken ct)
        {
            var frame = new OutboundFrame(data, type, flowId, ct);
            await WriterFor(priority).WriteAsync(frame, ct);
            await frame.Completion.Task.WaitAsync(ct);
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var frame = TryDequeue();
                    if (frame == null)
                    {
                        await WaitForWorkAsync(_stop.Token);
                        continue;
                    }
                    if (frame.Cancellation.IsCancellationRequested)
                    {
                        frame.Completion.TrySetCanceled(frame.Cancellation);
                        continue;
                    }
                    try
                    {
                        await Socket.SendAsync(frame.Data, frame.MessageType, true, _stop.Token);
                        frame.Completion.TrySetResult();
                    }
                    catch (OperationCanceledException) when (frame.Cancellation.IsCancellationRequested)
                    {
                        frame.Completion.TrySetCanceled(frame.Cancellation);
                    }
                    catch (Exception ex)
                    {
                        frame.Completion.TrySetException(ex);
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { Drain(new OperationCanceledException("manager outbound scheduler stopped")); }
        }

        private OutboundFrame? TryDequeue()
        {
            for (var attempt = 0; attempt < Schedule.Length; attempt++)
            {
                var priority = Schedule[_scheduleIndex++ % Schedule.Length];
                if (ReaderFor(priority).TryRead(out var frame)) return frame;
            }
            return null;
        }

        private async Task WaitForWorkAsync(CancellationToken ct)
        {
            var waits = new[] {
                _critical.Reader.WaitToReadAsync(ct).AsTask(),
                _interactive.Reader.WaitToReadAsync(ct).AsTask(),
                _bulk.Reader.WaitToReadAsync(ct).AsTask()
            };
            await Task.WhenAny(waits);
        }

        private ChannelWriter<OutboundFrame> WriterFor(OutboundPriority priority) => priority switch
        {
            OutboundPriority.Critical => _critical.Writer,
            OutboundPriority.Interactive => _interactive.Writer,
            _ => _bulk.Writer
        };

        private ChannelReader<OutboundFrame> ReaderFor(OutboundPriority priority) => priority switch
        {
            OutboundPriority.Critical => _critical.Reader,
            OutboundPriority.Interactive => _interactive.Reader,
            _ => _bulk.Reader
        };

        private static Channel<OutboundFrame> CreateChannel(int capacity) => Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            AllowSynchronousContinuations = false
        });

        private void Drain(Exception error)
        {
            foreach (var reader in new[] { _critical.Reader, _interactive.Reader, _bulk.Reader })
                while (reader.TryRead(out var frame)) frame.Completion.TrySetException(error);
        }

        public async ValueTask DisposeAsync()
        {
            _critical.Writer.TryComplete();
            _interactive.Writer.TryComplete();
            _bulk.Writer.TryComplete();
            _stop.Cancel();
            try { await _writer; } catch { }
            _stop.Dispose();
        }

        private sealed class OutboundFrame
        {
            public byte[] Data { get; }
            public WebSocketMessageType MessageType { get; }
            public string FlowId { get; }
            public CancellationToken Cancellation { get; }
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public OutboundFrame(byte[] data, WebSocketMessageType messageType, string flowId, CancellationToken cancellation)
            { Data = data; MessageType = messageType; FlowId = flowId; Cancellation = cancellation; }
        }
    }

    private sealed class EnrollResponse { public string AgentId { get; set; } = ""; public string AgentToken { get; set; } = ""; }
    private sealed class AgentMessage { public string Type { get; set; } = ""; public string AgentType { get; set; } = ""; public string AgentVersion { get; set; } = ""; public string PluginVersion { get; set; } = ""; public string[]? Capabilities { get; set; } public string RequestId { get; set; } = ""; public string InstanceId { get; set; } = ""; public bool? OK { get; set; } public string? Error { get; set; } public int Status { get; set; } public Dictionary<string,string>? Headers { get; set; } public List<string>? SetCookies { get; set; } public string? Body { get; set; } public string FrameType { get; set; } = ""; public List<ManagerInstance>? Instances { get; set; } }
    private sealed class ManagerCommand { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string InstanceId { get; set; } = ""; public string Action { get; set; } = ""; public string[]? Capabilities { get; set; } public JsonElement? Outcome { get; set; } }
    private sealed class ManagerProxyRequest { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string InstanceId { get; set; } = ""; public string Method { get; set; } = "GET"; public string Path { get; set; } = "/"; public Dictionary<string,string> Headers { get; set; } = new(); public string Body { get; set; } = ""; public bool Bootstrap { get; set; } public bool BinaryResponse { get; set; } public bool StreamResponse { get; set; } }
    private sealed class ManagerProxyRequestEnd { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; }
    private sealed class ManagerProxyCancel { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string? Reason { get; set; } }
    private sealed class ManagerProxyWebSocketOpen { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string InstanceId { get; set; } = ""; public string Path { get; set; } = "/"; public Dictionary<string,string> Headers { get; set; } = new(); public bool BinaryFrames { get; set; } }
    private sealed class ManagerProxyWebSocketFrame { public string Type { get; set; } = ""; public string RequestId { get; set; } = ""; public string FrameType { get; set; } = "text"; public string? Body { get; set; } public string? Error { get; set; } }
    private sealed class ManagerInstance { public string InstanceId { get; set; } = ""; public string DisplayName { get; set; } = ""; public string Type { get; set; } = ""; public string State { get; set; } = ""; public bool URLAvailable { get; set; } public string? StartupUrl { get; set; } }
}
