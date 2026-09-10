namespace DshLauncher;

/// <summary>Correlates a WebView response submission with its browser-side acknowledgement.</summary>
internal sealed class BrowserDshInteractionReplyTracker
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.Ordinal);

    public PendingReply Begin()
    {
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion)) throw new InvalidOperationException("无法创建 dsh 交互回执请求");
        return new PendingReply(requestId, completion.Task);
    }

    public void Cancel(string requestId) => _pending.TryRemove(requestId, out _);

    public bool TryComplete(string raw)
    {
        if (!BrowserDshInteractionBridge.TryParseReply(raw, out var reply)) return false;
        if (!_pending.TryRemove(reply.RequestId, out var completion)) return true;
        if (reply.Accepted) completion.TrySetResult(true);
        else completion.TrySetException(new InvalidOperationException(reply.Error ?? "dsh 未接受交互结果"));
        return true;
    }

    internal readonly record struct PendingReply(string RequestId, Task Completion);
}
