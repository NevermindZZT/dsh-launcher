using System.Drawing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

/// <summary>
/// dsh-manager 代理实例窗口。它只加载 manager 返回的同源 /dsh/&lt;session&gt;/ 地址，
/// 使用独立 WebView2 user-data profile，不与本地/SSH/其它 manager 实例共享 Cookie。
/// </summary>
public sealed class ManagerConnectionWindow : Form
{
    private readonly MainForm _main;
    private readonly ShellWebView _web = new();
    private readonly Uri _target;
    private readonly IReadOnlyList<ManagerBrowserCookie> _cookies;
    private readonly string _displayName;
    private readonly Panel _loadingOverlay = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 20, 24), Visible = true };
    private readonly LoadingSpinner _spinner = new() { Size = new Size(56, 56) };
    private readonly Label _loadingText = new()
    {
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 11f),
        ForeColor = Color.FromArgb(0x9E, 0x9E, 0x9E),
    };
    private bool _quitting;
    private NativeCaptionChrome? _captionChrome;

    private string WindowStateKey => "manager:" + TargetKey;

    public string TargetKey => _target.AbsoluteUri;

    public ManagerConnectionWindow(
        MainForm main,
        Uri target,
        string displayName,
        IReadOnlyList<ManagerBrowserCookie> cookies)
    {
        _main = main;
        _target = target;
        _displayName = displayName;
        _cookies = cookies;

        Text = "Manager · " + displayName;
        FormBorderStyle = FormBorderStyle.Sizable;
        ControlBox = true;
        MinimizeBox = true;
        MaximizeBox = true;
        ShowInTaskbar = true;
        ResizeEnd += (_, _) => _main.SaveWindowStateFor(this, WindowStateKey);

        var palette = ThemeHelper.GetPalette(true);
        BackColor = palette.WindowBack;
        ForeColor = palette.Text;
        _web.DefaultBackgroundColor = palette.WindowBack;

        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000);
        Width = Math.Max(1100, (int)(wa.Width * 0.88));
        Height = Math.Max(760, (int)(wa.Height * 0.88));
        MinimumSize = new Size(980, 680);
        StartPosition = FormStartPosition.CenterScreen;
        _main.RestoreWindowStateFor(this, WindowStateKey);
        Icon = MainForm.LoadAppIconShared();
        KeyPreview = true;

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);
        _loadingOverlay.Controls.Add(_spinner);
        _loadingOverlay.Controls.Add(_loadingText);
        _loadingOverlay.Resize += (_, _) =>
        {
            var cx = _loadingOverlay.ClientSize.Width / 2;
            var cy = _loadingOverlay.ClientSize.Height / 2;
            _spinner.Location = new Point(cx - _spinner.Width / 2, cy - 70);
            _loadingText.Location = new Point(0, cy + 8);
            _loadingText.Width = _loadingOverlay.ClientSize.Width;
            _loadingText.Height = 36;
        };
        Controls.Add(_loadingOverlay);
        _captionChrome = NativeCaptionChrome.Attach(this, Icon);
        _captionChrome.AddButton("设置", _main.ShowSettingsFromChild);
        _captionChrome.AddButton("Manager", () => _ = _main.ShowManagerFromChildAsync());
        _captionChrome.AddMenuButton("工具",
            new CaptionMenuItem("日志", _main.ShowLogsFromChild),
            new CaptionMenuItem("插件管理", _main.ShowPluginsFromChild),
            new CaptionMenuItem("重启 dsh", () => MessageBox.Show(this, "请在 dsh-manager 面板中执行远程实例重启。", "Manager 远程实例", MessageBoxButtons.OK, MessageBoxIcon.Information)));
        _captionChrome.AddButton("关于", _main.ShowAboutFromChild);

        FormClosing += (_, _) => { _main.SaveWindowStateFor(this, WindowStateKey); _quitting = true; };
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ThemeHelper.ApplyWindowTheme(Handle, true);
        ShowLoading("正在打开 manager 远程 dsh…");
        try
        {
            await EnsureWebView2Async();
        }
        catch (Exception ex)
        {
            HideLoading();
            MessageBox.Show(this, ex.Message, "打开 manager 远程 dsh 失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task EnsureWebView2Async()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DshLauncher", "WebView2", "manager", Sanitize(_target.Authority + _target.AbsolutePath));
        var envOptions = new CoreWebView2EnvironmentOptions();
        if (Environment.GetEnvironmentVariable("DSHLAUNCHER_DISABLE_NO_PROXY") != "1")
            envOptions.AdditionalBrowserArguments = "--no-proxy-server";

        var env = await CoreWebView2Environment.CreateAsync(null, userData, envOptions);
        await _web.EnsureCoreWebView2Async(env);
        var cwv = _web.CoreWebView2 ?? throw new InvalidOperationException("WebView2 初始化失败");
        cwv.Settings.AreDefaultContextMenusEnabled = true;
        cwv.Settings.AreDevToolsEnabled = false;
        cwv.Settings.IsStatusBarEnabled = false;
        await cwv.AddScriptToExecuteOnDocumentCreatedAsync(WebShell.NativeChromeGuardScript);
        await cwv.AddScriptToExecuteOnDocumentCreatedAsync(WebShell.HostBridgeScript);
        await cwv.AddScriptToExecuteOnDocumentCreatedAsync(WebShell.NativeThemeScript);

        foreach (var source in _cookies)
        {
            try
            {
                var cookie = cwv.CookieManager.CreateCookie(source.Name, source.Value, source.Domain, source.Path);
                cookie.IsHttpOnly = true;
                cookie.IsSecure = source.Secure;
                cwv.CookieManager.AddOrUpdateCookie(cookie);
            }
            catch (Exception ex)
            {
                Diag.Log("manager browser cookie import failed: " + ex.Message);
            }
        }

        cwv.WebMessageReceived += (_, e) =>
        {
            var raw = e.TryGetWebMessageAsString();
            if (_captionChrome?.TryApplyThemeMessage(raw) == true) return;
            if (BrowserNotificationBridge.TryParse(raw, out var notice))
            {
                _main.ShowSystemNotification(notice.Title, notice.Body, () => SafeUi(() => { Show(); Activate(); }), notice.RequireInteraction);
                return;
            }
            WebShellBridge.TryHandleWindowCommand(this, raw, action =>
            {
                switch (action)
                {
                    case "manager": _ = _main.ShowManagerFromChildAsync(); break;
                    case "about": _main.ShowAboutFromChild(); break;
                    case "logs": _main.ShowLogsFromChild(); break;
                    case "plugins": _main.ShowPluginsFromChild(); break;
                    case "settings": _main.ShowManagerFromChildAsync(); break;
                    case "restart":
                        MessageBox.Show(this, "请在 dsh-manager 面板中执行远程实例重启。", "Manager 远程实例", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                }
            });
        };
        cwv.NavigationStarting += OnNavigationStarting;
        cwv.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternalLink(e.Uri);
        };
        cwv.DocumentTitleChanged += (_, _) =>
        {
            var title = cwv.DocumentTitle;
            if (string.IsNullOrWhiteSpace(title)) return;
            SafeUi(() =>
            {
                var displayTitle = WebShellBridge.FormatSessionTitle("Manager · " + _displayName + " · " + title);
                Text = displayTitle;
            });
        };
        cwv.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) HideLoading();
            else HideLoading();
        };
        WebView2PermissionPolicy.Attach(cwv);
        try
        {
            var controllerField = typeof(WebView2).GetField("_coreWebView2Controller",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var controller = controllerField?.GetValue(_web) as CoreWebView2Controller;
            if (controller != null) controller.AcceleratorKeyPressed += (_, key) =>
            {
                var ctrl = (GetKeyState(0x11) & 0x8000) != 0;
                var shift = (GetKeyState(0x10) & 0x8000) != 0;
                if (!ctrl || !shift) return;
                if ((Keys)key.VirtualKey == Keys.M)
                {
                    key.Handled = true;
                    _ = _main.ShowManagerFromChildAsync();
                }
            };
        }
        catch { }

        _web.Source = _target;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (IsManagerOrigin(uri)) return;
        e.Cancel = true;
        OpenExternalLink(e.Uri);
    }

    private bool IsManagerOrigin(Uri uri) =>
        string.Equals(uri.Scheme, _target.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, _target.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _target.Port;

    private void OpenExternalLink(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var target)) _main.OpenExternalLink(target.AbsoluteUri);
    }

    private void ShowLoading(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => ShowLoading(text)); return; }
        _loadingText.Text = text;
        _loadingOverlay.Visible = true;
        _loadingOverlay.BringToFront();
    }

    private void HideLoading()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(HideLoading); return; }
        _loadingOverlay.Visible = false;
    }

    internal void SetWorkAreaMaximizedBounds(Rectangle bounds) => MaximizedBounds = bounds;

    protected override void WndProc(ref Message m)
    {
        if (_captionChrome?.TryHandleWindowMessage(ref m) == true) return;
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_quitting) _quitting = true;
        base.OnFormClosing(e);
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        return new string(chars)[..Math.Min(chars.Length, 100)];
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
