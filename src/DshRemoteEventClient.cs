using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DshLauncher;

/// <summary>
/// One independent native DSH Remote Event consumer. It authenticates with the
/// startup URL captured by the launcher, listens on /api/remote.mux, and sends
/// only structured $events/result replies for the received event id.
/// </summary>
internal sealed class DshRemoteEventClient : IDshInteractionResponder, IAsyncDisposable
{
    private const int ReceiveLimit = 512 * 1024;
    private readonly IDshConnection _connection;
    private readonly string _sourceKey;
    private readonly Action<DshPendingInteraction> _onInteraction;
    private readonly Action<string, string> _onCancelled;
    private readonly Action<string> _log;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _responseGate = new(1, 1);
    private Task? _loop;
    private CookieContainer? _cookies;
    private string? _origin;
    private string? _clientId;
    private ClientWebSocket? _socket;

    public DshRemoteEventClient(IDshConnection connection, Action<DshPendingInteraction> onInteraction, Action<string, string> onCancelled, Action<string>? log = null)
    {
        _connection = connection;
        _sourceKey = ConnectionManager.IdOf(connection);
        _onInteraction = onInteraction;
        _onCancelled = onCancelled;
        _log = log ?? Diag.Log;
    }

    public string SourceKey => _sourceKey;
    public string SourceName => _connection.DisplayName;

    public void Start()
    {
        if (_loop is { IsCompleted: false }) return;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var startupUrl = _connection.CurrentUrl;
                if (_connection.State != HostState.Running || string.IsNullOrWhiteSpace(startupUrl))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                await ConnectAndReceiveAsync(new Uri(startupUrl), ct);
                delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log($"[Approval] {_connection.DisplayName} Remote Event 连接中断: {ex.Message}");
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task ConnectAndReceiveAsync(Uri startupUrl, CancellationToken ct)
    {
        var cookies = await AuthenticateAsync(startupUrl, ct);
        var origin = new Uri(startupUrl.GetLeftPart(UriPartial.Authority));
        var socketUrl = new UriBuilder(origin) { Scheme = origin.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", Path = "/api/remote.mux", Query = "" }.Uri;

        using var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _cookies = cookies;
        _origin = origin.GetLeftPart(UriPartial.Authority);
        _clientId = null;
        _socket = socket;
        try
        {
            await socket.ConnectAsync(socketUrl, ct);
            var streamId = "launcher-events-" + Guid.NewGuid().ToString("N");
            var open = JsonSerializer.Serialize(new { type = "open", streamId, endpoint = "$events", payload = new { args = new { } } }, _json);
            await socket.SendAsync(Encoding.UTF8.GetBytes(open), WebSocketMessageType.Text, true, ct);
            _log($"[Approval] 已订阅 {_connection.DisplayName} 的 dsh 交互事件");
            await ReceiveLoopAsync(socket, ct);
        }
        finally
        {
            if (ReferenceEquals(_socket, socket)) _socket = null;
            _clientId = null;
        }
    }

    private static async Task<CookieContainer> AuthenticateAsync(Uri startupUrl, CancellationToken ct)
    {
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(startupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"dsh 认证失败: HTTP {(int)response.StatusCode}。请使用由 DshLauncher 启动的实例或重新连接。");
        return cookies;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("dsh Remote Event 返回了非文本消息");
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > ReceiveLimit) throw new InvalidOperationException("dsh Remote Event 消息超过大小限制");
            if (!result.EndOfMessage) continue;
            var raw = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            stream.SetLength(0);
            HandleWireMessage(raw);
        }
    }

    private void HandleWireMessage(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var type)) return;
        if (type.GetString() == "error") throw new InvalidOperationException(root.TryGetProperty("error", out var error) ? error.GetRawText() : "dsh Remote Event stream failed");
        if (type.GetString() != "item" || !root.TryGetProperty("value", out var value)) return;
        if (!value.TryGetProperty("type", out var frameType)) return;
        switch (frameType.GetString())
        {
            case "ready":
                _clientId = ReadString(value, "clientId");
                if (string.IsNullOrWhiteSpace(_clientId)) throw new InvalidOperationException("dsh Remote Event 未返回 clientId");
                return;
            case "cancel":
                var cancelled = ReadString(value, "eventId");
                if (!string.IsNullOrWhiteSpace(cancelled)) _onCancelled(_sourceKey, cancelled);
                return;
            case "waterfall":
                HandleWaterfall(value);
                return;
        }
    }

    private void HandleWaterfall(JsonElement frame)
    {
        var eventId = ReadString(frame, "eventId");
        var eventName = ReadString(frame, "event");
        var agentId = ReadString(frame, "agentId");
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(_clientId)) return;
        if (!frame.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object)
        {
            _ = ReplyAsync(eventId, _clientId!, new DshInteractionDecision("next"));
            return;
        }

        if (eventName == "approval/request")
        {
            _onInteraction(new DshPendingInteraction(_sourceKey, _connection.DisplayName, eventId, _clientId!, agentId ?? "", DshInteractionKind.Approval,
                ReadString(request, "toolName"), ReadString(request, "reason"), Array.Empty<DshUserQuestion>(), this));
            return;
        }
        if (eventName == "user-questions/request")
        {
            var questions = ParseQuestions(request);
            if (questions.Count == 0)
            {
                _ = ReplyAsync(eventId, _clientId!, DshInteractionDecision.CancelQuestion());
                return;
            }
            _onInteraction(new DshPendingInteraction(_sourceKey, _connection.DisplayName, eventId, _clientId!, agentId ?? "", DshInteractionKind.Question,
                null, null, questions, this));
            return;
        }

        // This client subscribes to the common event stream. Always decline events
        // it does not own so another DSH UI provider remains eligible to handle them.
        _ = ReplyAsync(eventId, _clientId!, new DshInteractionDecision("next"));
    }

    private static IReadOnlyList<DshUserQuestion> ParseQuestions(JsonElement request)
    {
        if (!request.TryGetProperty("questions", out var array) || array.ValueKind != JsonValueKind.Array) return Array.Empty<DshUserQuestion>();
        var result = new List<DshUserQuestion>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(item, "id");
            var question = ReadString(item, "question");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(question)) continue;
            var options = new List<DshQuestionOption>();
            if (item.TryGetProperty("options", out var rawOptions) && rawOptions.ValueKind == JsonValueKind.Array)
                foreach (var option in rawOptions.EnumerateArray())
                    if (option.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(ReadString(option, "label")))
                        options.Add(new DshQuestionOption(ReadString(option, "label")!, ReadString(option, "description")));
            var multi = item.TryGetProperty("multiSelect", out var rawMulti) && rawMulti.ValueKind == JsonValueKind.True;
            result.Add(new DshUserQuestion(id!, question!, ReadString(item, "header"), ReadString(item, "detail"), options, multi));
        }
        return result;
    }

    public async Task ReplyAsync(DshPendingInteraction interaction, DshInteractionDecision decision)
        => await ReplyAsync(interaction.EventId, interaction.ClientId, decision);

    private async Task ReplyAsync(string eventId, string clientId, DshInteractionDecision decision)
    {
        if (decision.Outcome == "next")
        {
            await SendResultAsync(eventId, clientId, new { kind = "next" });
            return;
        }
        await SendResultAsync(eventId, clientId, DshInteractionOutcome.Build(decision));
    }

    private async Task SendResultAsync(string eventId, string clientId, object outcome)
    {
        var origin = _origin ?? throw new InvalidOperationException("dsh Remote Event 未建立认证连接");
        var cookies = _cookies ?? throw new InvalidOperationException("dsh Remote Event 缺少认证 Cookie");
        await _responseGate.WaitAsync(_stop.Token);
        try
        {
            using var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var body = new { type = "client-request", rpcId = Guid.NewGuid().ToString("N"), method = "$events/result", payload = new { args = new { clientId, eventId, outcome } } };
            using var response = await client.PostAsJsonAsync(origin + "/api/$events/result", body, _json, _stop.Token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"dsh 交互结果提交失败: HTTP {(int)response.StatusCode}");
            using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_stop.Token));
            if (!reply.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("dsh 未接受交互结果");
        }
        finally { _responseGate.Release(); }
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _socket?.Abort(); } catch { }
        if (_loop != null) { try { await _loop; } catch { } }
        _responseGate.Dispose();
        _stop.Dispose();
    }
}
