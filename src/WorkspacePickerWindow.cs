using System.Drawing;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

/// <summary>Standalone WinUI3-style WebView picker for Windows or POSIX directory trees.</summary>
internal sealed class WorkspacePickerWindow : Form
{
    internal sealed record Entry(string Path, bool IsDirectory);
    private readonly Func<string, List<Entry>> _list;
    private readonly bool _isPosix;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private string _current;
    private bool _closing;
    private ThemeHelper.Palette _palette = ThemeHelper.CurrentPagePalette;
    public event Action<string>? PathConfirmed;

    public WorkspacePickerWindow(string title, string initialPath, bool isPosix, Func<string, List<Entry>> list)
    {
        Text = title;
        _current = initialPath;
        _isPosix = isPosix;
        _list = list;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(760, 560);
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        MaximumSize = new Size(Math.Max(760, area.Width - 32), Math.Max(560, area.Height - 32));
        Size = new Size(Math.Min(1040, MaximumSize.Width), Math.Min(760, MaximumSize.Height));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        LauncherIconTheme.Attach(this);
        BackColor = _palette.WindowBack;
        _web.BackColor = BackColor;
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);
        FormClosing += (_, _) => _closing = true;
        FormClosed += (_, _) => ThemeHelper.PagePaletteChanged -= OnPagePaletteChanged;
        Shown += async (_, _) => await InitializeAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeHelper.PagePaletteChanged += OnPagePaletteChanged;
        ApplyWindowPalette(_palette);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        ThemeHelper.PagePaletteChanged -= OnPagePaletteChanged;
        base.OnHandleDestroyed(e);
    }

    private void OnPagePaletteChanged(ThemeHelper.Palette palette)
    {
        _palette = palette;
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(() => ApplyWindowPalette(palette));
        else ApplyWindowPalette(palette);
    }

    private void ApplyWindowPalette(ThemeHelper.Palette palette)
    {
        _palette = palette;
        BackColor = palette.WindowBack;
        _web.BackColor = palette.WindowBack;
        _web.DefaultBackgroundColor = palette.WindowBack;
        if (IsHandleCreated)
        {
            ThemeHelper.ApplyWindowTheme(Handle, palette.WindowBack.GetBrightness() < 0.55f);
            ThemeHelper.ApplyTitleBarPalette(Handle, palette);
        }
        WebThemeBridge.Apply(_web, _palette);
    }

    private void ApplyWebPalette() => WebThemeBridge.Apply(_web, _palette);

    private async Task InitializeAsync()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshLauncher", "WebView2", "workspace-picker");
            var environment = await CoreWebView2Environment.CreateAsync(null, path);
            if (_closing) return;
            await _web.EnsureCoreWebView2Async(environment);
            var core = _web.CoreWebView2!;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.WebMessageReceived += (_, e) => HandleMessage(e.TryGetWebMessageAsString());
            core.NavigationCompleted += (_, _) => ApplyWebPalette();
            await RenderAsync();
        }
        catch (Exception ex) { if (!_closing) MessageBox.Show(this, ex.Message, "文件选择器初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void HandleMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString();
            var path = root.TryGetProperty("path", out var value) ? value.GetString() ?? _current : _current;
            if (action == "navigate") { _current = Normalize(path); _ = RenderAsync(); }
            else if (action == "up") { _current = ParentPath(_current); _ = RenderAsync(); }
            else if (action == "confirm") { PathConfirmed?.Invoke(Normalize(path)); Close(); }
        }
        catch { }
    }

    private async Task RenderAsync()
    {
        if (_web.CoreWebView2 == null || _closing) return;
        var requestedPath = _current;
        List<Entry> entries;
        try { entries = await Task.Run(() => _list(requestedPath)); }
        catch (Exception ex) { entries = new(); Diag.Log("picker list: " + ex.Message); }
        if (_web.CoreWebView2 == null || _closing || !string.Equals(requestedPath, _current, StringComparison.Ordinal)) return;
        var data = new { current = requestedPath, entries, isPosix = _isPosix };
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
        _web.CoreWebView2.NavigateToString(Html.Replace("__PAYLOAD__", payload, StringComparison.Ordinal));
    }

    private string Normalize(string value) => string.IsNullOrWhiteSpace(value) && !_isPosix ? "" : (string.IsNullOrWhiteSpace(value) ? _current : value.Trim());
    private string ParentPath(string path)
    {
        if (_isPosix) { var p = path.TrimEnd('/'); var i = p.LastIndexOf('/'); return i <= 0 ? "/" : p[..i]; }
        if (string.IsNullOrWhiteSpace(path)) return "";
        var root = Path.GetPathRoot(path);
        return string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ? "" : Directory.GetParent(path)?.FullName ?? "";
    }

    private const string Html = """
<!doctype html><html><head><meta charset="utf-8"><style>
:root{color-scheme:dark;--bg:#202020;--surface:#292929;--line:#4b4b4b;--text:#f3f3f3;--muted:#b8b8b8;--accent:#60cdff}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px Segoe UI,system-ui}.app{height:100vh;display:grid;grid-template-rows:auto auto 1fr auto;gap:14px;padding:22px}.path{display:flex;gap:8px}.path input{flex:1}.list{overflow:auto;border:1px solid var(--line);border-radius:10px;background:var(--surface);padding:8px}.entry{width:100%;display:flex;gap:10px;align-items:center;padding:10px;border:0;border-radius:7px;background:transparent;color:inherit;text-align:left;font:inherit;cursor:pointer}.entry:hover{background:#183646;color:var(--accent)}input,button{height:38px;border:1px solid var(--line);border-radius:8px;background:#1f1f1f;color:var(--text);padding:0 12px;font:inherit}button{cursor:pointer}button.primary{background:var(--accent);border-color:var(--accent);color:#00344d;font-weight:650}.bar{display:flex;justify-content:space-between;gap:8px}.muted{color:var(--muted)}
</style></head><body><main class="app" id="app"></main><script>
const d=JSON.parse(new TextDecoder().decode(Uint8Array.from(atob('__PAYLOAD__'),c=>c.charCodeAt(0)))),q=s=>document.querySelector(s),send=(action,path)=>chrome.webview.postMessage(JSON.stringify({action,path})),esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const icon='📁';q('#app').innerHTML='<div><b>选择文件夹</b><div class="muted">'+(d.isPosix?'Linux / SSH 路径':'Windows 本地路径')+'</div></div><div class="path"><button id="up">上级</button><input id="path" value="'+esc(d.current)+'"><button id="go">转到</button></div><div class="list">'+(d.entries.length?d.entries.filter(x=>x.isDirectory).map(x=>'<button class="entry" data-p="'+esc(x.path)+'"><span>'+icon+'</span><span>'+esc(x.path)+'</span></button>').join(''):'<p class="muted">此目录中没有可显示的文件夹。</p>')+'</div><div class="bar"><button id="cancel">取消</button><button class="primary" id="confirm">选择此文件夹</button></div>';
q('#up').onclick=()=>send('up');q('#go').onclick=()=>send('navigate',q('#path').value);q('#path').onkeydown=e=>{if(e.key==='Enter')send('navigate',q('#path').value)};q('.list').onclick=e=>{let b=e.target.closest('[data-p]');if(b)send('navigate',b.dataset.p)};q('#confirm').onclick=()=>send('confirm',q('#path').value);q('#cancel').onclick=()=>window.close();
</script></body></html>
""";
}
