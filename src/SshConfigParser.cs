namespace DshLauncher;

/// <summary>一个 ~/.ssh/config 中的主机条目。</summary>
public sealed record SshHostEntry(string Alias, string? HostName, string? User, int Port, string? IdentityFile);

/// <summary>解析系统 OpenSSH 配置（~/.ssh/config），读取已记录的主机，供 SSH 连接配置导入。</summary>
public static class SshConfigParser
{
    /// <summary>系统 SSH 配置文件路径（~/.ssh/config），不存在返回 null。</summary>
    public static string? ConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var p = Path.Combine(home, ".ssh", "config");
        return File.Exists(p) ? p : null;
    }

    /// <summary>解析所有 Host 条目（忽略通配符 * 条目）。</summary>
    public static List<SshHostEntry> ParseHosts()
    {
        var result = new List<SshHostEntry>();
        var path = ConfigPath();
        if (path == null) return result;
        try
        {
            string? alias = null, hostName = null, user = null, identityFile = null;
            var port = 22;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                var key = parts[0].ToLowerInvariant();
                var val = parts.Length > 1 ? parts[1].Trim() : "";
                if (key == "host")
                {
                    if (alias != null && !alias.Contains('*'))
                        result.Add(new SshHostEntry(alias, hostName, user, port, identityFile));
                    alias = val; hostName = null; user = null; identityFile = null; port = 22;
                }
                else if (alias != null)
                {
                    switch (key)
                    {
                        case "hostname": hostName = val; break;
                        case "user": user = val; break;
                        case "port": int.TryParse(val, out port); break;
                        case "identityfile": identityFile = ExpandHome(val); break;
                    }
                }
            }
            if (alias != null && !alias.Contains('*'))
                result.Add(new SshHostEntry(alias, hostName, user, port, identityFile));
        }
        catch
        {
            // 解析失败返回空列表
        }
        return result;
    }

    private static string ExpandHome(string p)
        => p.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
