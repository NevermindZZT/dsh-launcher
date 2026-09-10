namespace DshLauncher;

/// <summary>Owns per-connection Remote Event clients and serializes native human interaction.</summary>
internal sealed class DshInteractionCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DshRemoteEventClient> _clients = new(StringComparer.Ordinal);
    private readonly Queue<DshPendingInteraction> _pending = new();
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DshPendingInteraction> _delegated = new(StringComparer.Ordinal);
    private DshPendingInteraction? _active;
    public Func<DshPendingInteraction, bool>? TryDelegate { get; set; }

    public event Action<DshPendingInteraction>? InteractionReady;
    public event Action<DshPendingInteraction>? InteractionCancelled;
    public event Action<int>? PendingCountChanged;

    public void RefreshConnections(IEnumerable<IDshConnection> connections)
    {
        // The stock DSH web client holds a session write handle. Opening a second
        // raw /api/remote.mux client can race it and trigger SessionAlreadyOwnedError
        // when the browser resumes or switches models. Keep the experimental direct
        // subscriber opt-in until DSH exposes a host-side observer transport.
        if (!string.Equals(Environment.GetEnvironmentVariable("DSHLAUNCHER_ENABLE_EXPERIMENTAL_NATIVE_REMOTE_EVENTS"), "1", StringComparison.Ordinal))
        {
            Diag.Log("[Approval] 已禁用实验性 dsh Remote Event 直连，避免与 Web UI 会话写入器冲突");
            return;
        }
        foreach (var connection in connections)
        {
            var key = ConnectionManager.IdOf(connection);
            lock (_gate)
            {
                if (_clients.ContainsKey(key)) continue;
                var client = new DshRemoteEventClient(connection, Enqueue, Cancel, Diag.Log);
                _clients.Add(key, client);
                client.Start();
            }
        }
    }

    public void PublishExternal(DshPendingInteraction interaction) => Enqueue(interaction);
    public void CancelExternal(string sourceKey, string eventId) => Cancel(sourceKey, eventId);

    private void Enqueue(DshPendingInteraction interaction)
    {
        DshPendingInteraction? next = null;
        int count;
        lock (_gate)
        {
            var key = Key(interaction);
            if (!_known.Add(key)) return;
            if (TryDelegate?.Invoke(interaction) == true)
            {
                _delegated[key] = interaction;
                count = PendingCountLocked();
            }
            else
            {
                _pending.Enqueue(interaction);
                next = PromoteNextLocked();
                count = PendingCountLocked();
            }
        }
        PendingCountChanged?.Invoke(count);
        if (next != null) InteractionReady?.Invoke(next);
    }

    private void Cancel(string sourceKey, string eventId)
    {
        DshPendingInteraction? cancelled = null;
        DshPendingInteraction? next = null;
        int count;
        lock (_gate)
        {
            var key = sourceKey + ":" + eventId;
            _known.Remove(key);
            if (_delegated.Remove(key, out var delegated)) cancelled = delegated;
            else if (_active != null && Key(_active) == key)
            {
                cancelled = _active;
                _active = null;
            }
            else if (_pending.Count > 0)
            {
                var keep = _pending.Where(item => Key(item) != key).ToArray();
                _pending.Clear();
                foreach (var item in keep) _pending.Enqueue(item);
            }
            next = PromoteNextLocked();
            count = PendingCountLocked();
        }
        if (cancelled != null) InteractionCancelled?.Invoke(cancelled);
        PendingCountChanged?.Invoke(count);
        if (next != null) InteractionReady?.Invoke(next);
    }

    public async Task SubmitAsync(DshPendingInteraction interaction, DshInteractionDecision decision)
    {
        try { await interaction.Owner.ReplyAsync(interaction, decision); }
        catch (Exception ex) { Diag.Log($"[Approval] 提交 {interaction.SourceName} 的交互结果失败: {ex.Message}"); throw; }
        Complete(interaction);
    }

    private void Complete(DshPendingInteraction interaction)
    {
        DshPendingInteraction? next = null;
        int count;
        lock (_gate)
        {
            if (_active != null && Key(_active) == Key(interaction)) _active = null;
            _known.Remove(Key(interaction));
            next = PromoteNextLocked();
            count = PendingCountLocked();
        }
        PendingCountChanged?.Invoke(count);
        if (next != null) InteractionReady?.Invoke(next);
    }

    private DshPendingInteraction? PromoteNextLocked()
    {
        if (_active != null || _pending.Count == 0) return null;
        _active = _pending.Dequeue();
        return _active;
    }

    public int PendingCount
    {
        get { lock (_gate) return PendingCountLocked(); }
    }

    private int PendingCountLocked() => _pending.Count + (_active == null ? 0 : 1);
    private static string Key(DshPendingInteraction item) => item.SourceKey + ":" + item.EventId;

    public async ValueTask DisposeAsync()
    {
        DshRemoteEventClient[] clients;
        lock (_gate)
        {
            clients = _clients.Values.ToArray();
            _clients.Clear();
            _pending.Clear();
            _active = null;
            _known.Clear();
            _delegated.Clear();
        }
        await Task.WhenAll(clients.Select(client => client.DisposeAsync().AsTask()));
    }
}
