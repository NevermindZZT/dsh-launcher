using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DshLauncher;

internal sealed class WindowsNotificationService : IDisposable
{
    private const string AppId = "DshLauncher";
    private readonly NotifyIcon _tray;
    private readonly object _gate = new();
    private Action? _pendingActivation;
    private bool _disposed;

    public WindowsNotificationService(NotifyIcon tray)
    {
        _tray = tray;
        _tray.BalloonTipClicked += OnBalloonTipClicked;
    }

    public void Show(string title, string body, Action? activate = null, bool requireInteraction = false)
    {
        if (_disposed) return;
        if (TryShowToast(title, body, requireInteraction)) return;
        lock (_gate) _pendingActivation = activate;
        try
        {
            _tray.ShowBalloonTip(8000, Trim(title, 63), Trim(body, 255), ToolTipIcon.Info);
        }
        catch { }
    }

    private static bool TryShowToast(string title, string body, bool requireInteraction)
    {
        try
        {
            EnsureAppShortcut();
            var xml = $"<toast{(requireInteraction ? " scenario=\"reminder\"" : "")}><visual><binding template=\"ToastGeneric\"><text>{XmlEscape(Trim(title, 200))}</text>{(string.IsNullOrWhiteSpace(body) ? "" : $"<text>{XmlEscape(Trim(body, 2000))}</text>")}</binding></visual></toast>";
            var xml64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
            var command = $"$ErrorActionPreference='Stop';$null=[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime];$null=[Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom.XmlDocument,ContentType=WindowsRuntime];$x=New-Object Windows.Data.Xml.Dom.XmlDocument;$x.LoadXml([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{xml64}')));$n=New-Object -TypeName Windows.UI.Notifications.ToastNotification -ArgumentList $x;$notifier=[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{AppId}');$notifier.Show($n)";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) return false;
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = $"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null) return false;
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void EnsureAppShortcut()
    {
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        var shortcut = Path.Combine(programs, "DshLauncher.lnk");
        Directory.CreateDirectory(programs);
        if (File.Exists(shortcut)) return;

        var link = (IShellLinkW)Activator.CreateInstance(typeof(ShellLink))!;
        link.SetPath(Application.ExecutablePath);
        link.SetDescription("DeepSeek Harness launcher");
        var store = (IPropertyStore)link;
        var key = new PropertyKey
        {
            FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            PropertyId = 5,
        };
        var appId = Marshal.StringToCoTaskMemUni(AppId);
        try
        {
            var value = new PropVariant { VariantType = 31, Pointer = appId };
            store.SetValue(ref key, ref value);
            store.Commit();
            ((IPersistFile)link).Save(shortcut, true);
        }
        finally { Marshal.FreeCoTaskMem(appId); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(IntPtr name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(IntPtr args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation(IntPtr iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile(out IntPtr fileName);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        Action? activate;
        lock (_gate)
        {
            activate = _pendingActivation;
            _pendingActivation = null;
        }
        try { activate?.Invoke(); } catch { }
    }

    private static string Trim(string value, int max)
    {
        value = value ?? string.Empty;
        return value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tray.BalloonTipClicked -= OnBalloonTipClicked;
    }
}

internal sealed record BrowserNotification(string Title, string Body, string? Tag, bool RequireInteraction, string? Icon);

internal static class BrowserNotificationBridge
{
    public static bool TryParse(string raw, out BrowserNotification notification)
    {
        notification = null!;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) ||
                !string.Equals(type.GetString(), "notification", StringComparison.OrdinalIgnoreCase)) return false;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var tag = root.TryGetProperty("tag", out var tagValue) ? tagValue.GetString() : null;
            var require = root.TryGetProperty("requireInteraction", out var req) && req.ValueKind == JsonValueKind.True;
            var icon = root.TryGetProperty("icon", out var iconValue) ? iconValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) return false;
            notification = new BrowserNotification(title, body, tag, require, icon);
            return true;
        }
        catch { return false; }
    }
}
