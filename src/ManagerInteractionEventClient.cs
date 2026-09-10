using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DshLauncher;

/// <summary>Authenticated manager-admin event client that projects remote Agent prompts into the native queue.</summary>
internal sealed class ManagerInteractionEventClient : IDshInteractionResponder, IAsyncDisposable
{
    private readonly Uri _baseUri;
    private readonly CookieContainer _cookies;
    private readonly Action<DshPendingInteraction> _onInteraction;
    private readonly Action<string, string> _onCancelled;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _sources = new(StringComparer.Ordinal);
    private Task? _loop;
    private ClientWebSocket? _socket;

    public ManagerInteractionEventClient(Uri baseUri, CookieContainer cookies, Action<DshPendingInteraction> onInteraction, Action<string,string> onCancelled)
    { _baseUri = baseUri; _cookies = cookies; _onInteraction = onInteraction; _onCancelled = onCancelled; }

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
                using var socket = new ClientWebSocket();
                socket.Options.Cookies = _cookies;
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                var ws = new UriBuilder(_baseUri) { Scheme = _baseUri.Scheme == "https" ? "wss" : "ws", Path = "/api/v1/admin/events", Query = "" }.Uri;
                _socket = socket;
                await socket.ConnectAsync(ws, ct);
                delay = TimeSpan.FromSeconds(1);
                await ReceiveAsync(socket, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Diag.Log("[Manager] 审批事件订阅断开: " + ex.Message);
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 2));
            }
            finally { _socket = null; }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 512 * 1024) throw new InvalidOperationException("manager 审批事件过大");
            if (!result.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            stream.SetLength(0);
            Handle(text);
        }
    }

    private void Handle(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var type = TextOf(root,"type");
        var requestId = TextOf(root,"requestId");
        if (type == "remote_event_cancel" && requestId != null)
        {
            if (_sources.TryRemove(requestId, out var cancelledSource)) _onCancelled(cancelledSource, requestId);
            return;
        }
        if (type != "remote_event" || requestId == null || !root.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object) return;
        var agentId=TextOf(root,"agentId") ?? "agent"; var instanceId=TextOf(root,"instanceId") ?? "instance";
        var source="manager:"+agentId+":"+instanceId;
        var kind=TextOf(root,"event") == "approval/request" ? DshInteractionKind.Approval : TextOf(root,"event") == "user-questions/request" ? DshInteractionKind.Question : (DshInteractionKind?)null;
        if (kind == null) return;
        var questions = kind == DshInteractionKind.Question ? Questions(request) : Array.Empty<DshUserQuestion>();
        if (kind == DshInteractionKind.Question && questions.Count == 0) return;
        _sources[requestId]=source;
        _onInteraction(new DshPendingInteraction(source, $"Manager · {agentId}/{instanceId}", requestId, "", TextOf(root,"remoteAgentId") ?? "", kind.Value, TextOf(request,"toolName"), TextOf(request,"reason"), questions, this));
    }

    public async Task ReplyAsync(DshPendingInteraction interaction, DshInteractionDecision decision)
    {
        var socket = _socket ?? throw new InvalidOperationException("manager 审批事件连接不可用");
        var outcome = DshInteractionOutcome.Build(decision);
        var raw=JsonSerializer.Serialize(new { type="remote_event_result", requestId=interaction.EventId, outcome });
        await _sendGate.WaitAsync(_stop.Token);
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(raw), WebSocketMessageType.Text, true, _stop.Token); _sources.TryRemove(interaction.EventId,out _); }
        finally { _sendGate.Release(); }
    }

    private static string? TextOf(JsonElement value,string name) => value.TryGetProperty(name,out var child) && child.ValueKind==JsonValueKind.String ? child.GetString() : null;
    private static IReadOnlyList<DshUserQuestion> Questions(JsonElement request)
    {
        if (!request.TryGetProperty("questions",out var list)||list.ValueKind!=JsonValueKind.Array) return Array.Empty<DshUserQuestion>();
        var result=new List<DshUserQuestion>();
        foreach(var item in list.EnumerateArray())
        {
            var id=TextOf(item,"id"); var question=TextOf(item,"question"); if(string.IsNullOrWhiteSpace(id)||string.IsNullOrWhiteSpace(question)) continue;
            var options=new List<DshQuestionOption>();
            if(item.TryGetProperty("options",out var raw)&&raw.ValueKind==JsonValueKind.Array) foreach(var opt in raw.EnumerateArray()) { var label=TextOf(opt,"label"); if(!string.IsNullOrWhiteSpace(label)) options.Add(new DshQuestionOption(label!,TextOf(opt,"description"))); }
            result.Add(new DshUserQuestion(id!,question!,TextOf(item,"header"),TextOf(item,"detail"),options,item.TryGetProperty("multiSelect",out var multi)&&multi.ValueKind==JsonValueKind.True));
        }
        return result;
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); try { _socket?.Abort(); } catch{} if(_loop!=null) try { await _loop; } catch{} _sendGate.Dispose(); _stop.Dispose(); }
}
