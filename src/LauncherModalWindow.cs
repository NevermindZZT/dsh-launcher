using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

/// <summary>Standalone WebView2 host for Launcher tools; it never injects UI into the dsh document.</summary>
internal sealed class LauncherModalWindow : Form
{
    private readonly AppSettings _settings;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private bool _ready;
    private bool _closing;
    private string _page = "about";
    private object? _data;

    public event Action<string>? BrowserMessage;

    public LauncherModalWindow(AppSettings settings)
    {
        _settings = settings;
        Text = "DshLauncher";
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(0x20, 0x20, 0x20);
        _web.BackColor = BackColor;
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);
        ApplyPageSize("about");
        FormClosing += (_, _) => _closing = true;
        Shown += async (_, _) => { CenterInWorkArea(); await InitializeAsync(); };
    }

    public void Open(string page, object? data = null)
    {
        _page = page;
        _data = data;
        Text = page switch { "logs" => "DshLauncher 日志", "plugins" => "DshLauncher 插件管理", "ssh" or "ssh-edit" => "DshLauncher SSH 连接", "manager" => "DshLauncher dsh-manager", "about" => "DshLauncher 关于", _ => "DshLauncher" };
        ApplyPageSize(page);
        if (_ready) Render();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Launcher surfaces intentionally follow dsh's dark theme rather than the system title-bar theme.
        ThemeHelper.ApplyWindowTheme(Handle, true);
    }

    private void ApplyPageSize(string page)
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var max = new Size(Math.Max(640, area.Width - 32), Math.Max(480, area.Height - 32));
        MaximumSize = max;
        var target = page switch
        {
            "about" => new Size(820, 680),
            "logs" => new Size(1120, 820),
            "plugins" => new Size(1080, 800),
            "manager" => new Size(780, 600),
            "ssh" or "ssh-edit" => new Size(1040, 780),
            _ => new Size(960, 720),
        };
        MinimumSize = new Size(640, 480);
        Size = new Size(Math.Min(target.Width, max.Width), Math.Min(target.Height, max.Height));
        if (IsHandleCreated) CenterInWorkArea();
    }

    private void CenterInWorkArea()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    private async Task InitializeAsync()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshLauncher", "WebView2", "launcher-modal");
            var environment = await CoreWebView2Environment.CreateAsync(null, path);
            if (_closing || IsDisposed) return;
            await _web.EnsureCoreWebView2Async(environment);
            if (_closing || IsDisposed) return;
            var core = _web.CoreWebView2!;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.WebMessageReceived += (_, e) => HandleWebMessage(e.TryGetWebMessageAsString());
            await WebModalRouter.Install(_web);
            core.NavigationCompleted += (_, _) => { _ready = true; Render(); };
            core.NavigateToString(Document);
        }
        catch (Exception ex)
        {
            if (!_closing && !IsDisposed) MessageBox.Show(this, "无法打开窗口：" + ex.Message, "初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void HandleWebMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "modal-size" &&
                root.TryGetProperty("width", out var width) && root.TryGetProperty("height", out var height))
            {
                ApplyContentSize(width.GetInt32(), height.GetInt32());
                return;
            }
        }
        catch { }
        BrowserMessage?.Invoke(raw);
    }

    private void ApplyContentSize(int contentWidth, int contentHeight)
    {
        // Stable page presets deliberately prevent a visible large-to-small resize transition.
    }

    private void Render()
    {
        if (_web.CoreWebView2 == null || _closing) return;
        if (_data == null) WebModalRouter.Open(_web, _settings, _page);
        else WebModalRouter.Open(_web, _page, _data);
    }

    private const string Document = """
<!doctype html><html><head><meta charset="utf-8"><style>
:root{color-scheme:dark;--dsh-launcher-bg:#202020;--dsh-launcher-fg:#f3f3f3;--dsh-launcher-accent:#60cdff}html,body{margin:0;width:100%;height:100%;background:#202020;color:#f3f3f3}#dsh-modal{display:block!important;background:#202020!important}#dsh-modal .backdrop{display:none!important}#dsh-modal .card{width:100%!important;max-width:none!important;height:100%!important;max-height:none!important;overflow:auto!important;background:#202020!important;color:#f3f3f3!important;border:0!important;border-radius:0!important;box-shadow:none!important}#dsh-modal .modal-header{padding:22px 26px 16px!important;border-color:#454545!important}#dsh-modal .modal-main{padding:20px 26px!important;min-height:100%}#dsh-modal fieldset{margin:0!important;padding:8px 0!important;border:0!important;background:transparent!important}#dsh-modal legend{padding:0!important;font-size:18px!important;font-weight:650!important}#dsh-modal #logbox{height:calc(100vh - 150px)!important;min-height:420px!important;max-height:none!important}#dsh-modal .modal-footer{padding:14px 26px 20px!important;border-color:#454545!important}#dsh-modal input,#dsh-modal textarea,#dsh-modal select{background:#1f1f1f!important;color:#f3f3f3!important;border-color:#5a5a5a!important;border-radius:8px!important}#dsh-modal button{background:#353535!important;color:#f3f3f3!important;border-color:#5a5a5a!important;border-radius:8px!important;min-height:34px!important}#dsh-modal button.primary{background:#60cdff!important;border-color:#60cdff!important;color:#00344d!important}#dsh-modal .manager-card,#dsh-modal .item{border-color:#4b4b4b!important;background:#2d2d2d!important;border-radius:10px!important}#dsh-modal .plugin-row{margin:0 0 10px!important;padding:0!important;border:0!important;background:transparent!important}#dsh-modal .plugin-select{padding:14px 16px!important;border:1px solid #4b4b4b!important;border-radius:10px!important;background:#2d2d2d!important}#dsh-modal .plugin-row.selected .plugin-select{border-color:#60cdff!important;background:#183646!important}#dsh-modal .folder-list{max-height:420px!important;background:#1f1f1f!important}#dsh-modal .folder-list button:hover,#dsh-modal .plugin-row.selected{background:#183646!important;color:#60cdff!important}
</style></head><body><script>window.__dshLauncherStandaloneModal=true;</script></body></html>
""";
}
