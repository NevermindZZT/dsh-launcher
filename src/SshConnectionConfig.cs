namespace DshLauncher;

/// <summary>SSH 远程连接配置（一个服务器主机 = 一条配置）。</summary>
public sealed class SshConnectionConfig
{
    /// <summary>配置显示名（如 生产服务器）。</summary>
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    /// <summary>认证方式：key（私钥，默认）或 password。</summary>
    public string AuthMethod { get; set; } = "key";
    /// <summary>私钥路径（AuthMethod=key 时）。</summary>
    public string KeyPath { get; set; } = "";
    /// <summary>密码（AuthMethod=password 时；明文存本地配置，注意保护）。</summary>
    public string? Password { get; set; }
    /// <summary>本地端口转发端口（0 = 自动分配；默认 3080）。</summary>
    public int LocalPort { get; set; } = 3080;
    /// <summary>远端 dsh 监听端口（默认 3080）。</summary>
    public int RemotePort { get; set; } = 3080;
    /// <summary>启动器关闭时是否停止远端 dsh（false = 保持运行，下次秒连）。</summary>
    public bool StopRemoteOnClose { get; set; } = true;
    /// <summary>远端 node 可执行文件路径（空 = 自动探测）。</summary>
    public string RemoteNode { get; set; } = "";
    /// <summary>远端 dsh bin.js 路径（空 = 自动探测）。</summary>
    public string RemoteDshBin { get; set; } = "";
    /// <summary>启动启动器时是否自动连接该服务器（多连接并行）。</summary>
    public bool AutoConnect { get; set; } = true;

    public string DisplayName => string.IsNullOrEmpty(Name) ? $"{User}@{Host}:{Port}" : Name;
}
