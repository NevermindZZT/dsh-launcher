namespace DshLauncher;

/// <summary>Window-scoped Launcher shortcuts, represented by WinForms-compatible key flags.</summary>
public static class LauncherShortcuts
{
    public const int Shift = 0x10000, Control = 0x20000, Alt = 0x40000;
    public static readonly (string Id, string Label, string Default)[] Actions =
    {
        ("restart", "重启宿主 / 远端", "Ctrl+Shift+1"),
        ("logs", "日志", "Ctrl+Shift+2"),
        ("plugins", "插件管理", "Ctrl+Shift+3"),
        ("settings", "设置（本地窗口）", "Ctrl+Shift+4"),
        ("manager", "dsh-manager", "Ctrl+Shift+5"),
        ("quit", "退出 / 关闭连接窗口", "Ctrl+Shift+6"),
        ("connections", "选择连接", "Ctrl+Shift+7"),
        ("sync", "同步配置（SSH 窗口）", "Ctrl+Shift+8"),
        ("folder", "打开目录（SSH 窗口）", "Ctrl+Shift+9"),
    };

    public static string Get(IReadOnlyDictionary<string, string>? overrides, string id)
    {
        if (overrides != null && overrides.TryGetValue(id, out var value) && value != null && TryParse(value, out _)) return value;
        return Actions.FirstOrDefault(a => a.Id == id).Default ?? "";
    }

    // Empty disables a shortcut. Require a modifier and restrict keys to layouts shared with the editor.
    public static bool TryParse(string? value, out int keyData)
    {
        keyData = 0;
        if (value == "") return true;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('+');
        if (parts.Length < 2) return false;
        var modifiers = 0;
        foreach (var part in parts[..^1])
        {
            var flag = part switch { "Ctrl" => Control, "Shift" => Shift, "Alt" => Alt, _ => 0 };
            if (flag == 0 || (modifiers & flag) != 0) return false;
            modifiers |= flag;
        }
        var key = parts[^1];
        int code;
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) code = key[0];
        else if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key[1..], out var f) && f is >= 1 and <= 12 && key == "F" + f) code = 0x6f + f;
        else return false;
        keyData = modifiers | code;
        return true;
    }

    public static bool Matches(IReadOnlyDictionary<string, string>? overrides, string id, int keyData) =>
        TryParse(Get(overrides, id), out var configured) && configured != 0 && configured == keyData;

    public static bool Validate(IReadOnlyDictionary<string, string>? overrides, out string error)
    {
        var used = new Dictionary<int, string>();
        foreach (var action in Actions)
        {
            var value = overrides != null && overrides.TryGetValue(action.Id, out var v) ? v : action.Default;
            if (!TryParse(value, out var key)) { error = $"{action.Label}的快捷键无效。"; return false; }
            if (key != 0 && used.TryGetValue(key, out var other)) { error = $"{action.Label}与{other}使用了相同的快捷键。"; return false; }
            if (key != 0) used[key] = action.Label;
        }
        error = "";
        return true;
    }
}
