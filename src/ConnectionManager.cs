namespace DshLauncher;

/// <summary>
/// 多连接管理器：同时管理一个本地连接 + 多个 SSH 远程连接。
/// 每个连接独立运行（本地 spawn/attach；SSH 隧道 + 远端 dsh），UI 通过 Tab 切换。
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly List<IDshConnection> _connections = new();

    public IReadOnlyList<IDshConnection> Connections => _connections;

    /// <summary>本地连接（始终存在，索引 0）。</summary>
    public IDshConnection Local => _connections[0];

    /// <summary>连接唯一标识：local 或 ssh:&lt;name&gt;。</summary>
    public static string IdOf(IDshConnection c) => c.IsRemote ? $"ssh:{c.DisplayName}" : "local";

    /// <summary>按设置构建连接列表：本地 + AutoConnect 的 SSH（以及所有 SSH，便于手动连接）。</summary>
    public void BuildFrom(AppSettings settings)
    {
        _connections.Clear();
        var local = new HostSupervisor();
        if (settings.AttachPort > 0) local.AttachPort = settings.AttachPort;
        local.WorkingDirectory = settings.WorkingDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _connections.Add(local);
        foreach (var cfg in settings.SshConnections)
        {
            if (!string.IsNullOrEmpty(cfg.Host) && !string.IsNullOrEmpty(cfg.User))
            {
                _connections.Add(new SshConnection(cfg));
            }
        }
    }

    /// <summary>
    /// 运行时同步连接列表（设置变更后调用）：复用运行中的连接实例（本地 + 名称/主机匹配的 SSH），
    /// 新增的创建，已删除的停止并移除。这样已运行的连接不会因设置刷新而中断。
    /// </summary>
    public void SyncFrom(AppSettings settings)
    {
        var keep = new List<IDshConnection> { _connections[0] }; // 本地保持
        var existingSsh = _connections.Skip(1).OfType<SshConnection>().ToList();
        foreach (var cfg in settings.SshConnections)
        {
            if (string.IsNullOrEmpty(cfg.Host) || string.IsNullOrEmpty(cfg.User)) continue;
            var match = existingSsh.FirstOrDefault(s =>
                s.Config.Name == cfg.Name && s.Config.Host == cfg.Host);
            if (match != null) keep.Add(match);
            else keep.Add(new SshConnection(cfg));
        }
        // 已删除的连接：停止并释放
        foreach (var removed in _connections.Except(keep).ToList())
        {
            try { removed.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { if (removed is IDisposable d) d.Dispose(); } catch { }
        }
        _connections.Clear();
        _connections.AddRange(keep);
    }

    /// <summary>启动时需要自动连接的连接（本地 + SSH AutoConnect）。</summary>
    public IEnumerable<IDshConnection> AutoStartConnections()
    {
        yield return _connections[0];
        foreach (var c in _connections.Skip(1))
        {
            if (c is SshConnection sc && sc.Config?.AutoConnect != false) yield return c;
        }
    }

    public void Dispose()
    {
        foreach (var c in _connections)
        {
            try { if (c is IDisposable d) d.Dispose(); } catch { }
        }
    }
}
