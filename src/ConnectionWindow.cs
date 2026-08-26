using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

/// <summary>
/// SSH 远程连接独立窗口：每个 SSH 连接一个窗口（独立 WebView + 独立 user data，会话互不干扰）。
/// 快捷键 Ctrl+Shift+R/L/P/Q 作用于本窗口的连接。
/// </summary>
public sealed class ConnectionWindow : Form
{
    private readonly IDshConnection _conn;
    private readonly MainForm _main;
    private readonly ShellWebView _web = new();
    private readonly Panel _loadingOverlay = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 20, 24), Visible = true };
    private readonly LoadingSpinner _spinner = new() { Size = new Size(56, 56) };
    private readonly Label _loadingText = new()
    {
        AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 11f), ForeColor = Color.FromArgb(0x9E, 0x9E, 0x9E),
    };
    private bool _quitting;
    private bool _syncing;

    public IDshConnection Connection => _conn;

    public ConnectionWindow(IDshConnection conn, MainForm main)
    {
        _conn = conn;
        _main = main;
        Text = conn.DisplayName;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        Resize += (_, _) => WebShellBridge.ApplyShape(this);
        // 远程窗口首帧直接使用系统深色背景，避免冷启动白闪
        var initialPalette = ThemeHelper.GetPalette(ThemeHelper.IsSystemDarkMode());
        BackColor = initialPalette.WindowBack;
        ForeColor = initialPalette.Text;
        _web.DefaultBackgroundColor = initialPalette.WindowBack;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000);
        Width = Math.Max(1100, (int)(wa.Width * 0.88));
        Height = Math.Max(760, (int)(wa.Height * 0.88));
        MinimumSize = new Size(980, 680);
        StartPosition = FormStartPosition.CenterScreen;
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
        WebShellBridge.InstallResizeGrips(this);

        FormClosing += (_, _) =>
        {
            if (_quitting) return;
            _quitting = true;
            try { _conn.StopAsync().GetAwaiter().GetResult(); } catch { }
        };
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Mica 深色（Handle 已就绪）
        ThemeHelper.ApplyWindowTheme(Handle, ThemeHelper.IsSystemDarkMode());
        ShowLoading($"正在连接 {_conn.DisplayName}…");
        try
        {
            await EnsureWebView2Async();
            // 连接启动；导航统一由 _conn.Ready 事件触发（避免双导航竞争）
            await _conn.StartAsync();
        }
        catch (Exception ex)
        {
            HideLoading();
            MessageBox.Show(this, ex.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task EnsureWebView2Async()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DshLauncher", "WebView2", Sanitize(_conn.DisplayName));
        var envOptions = new CoreWebView2EnvironmentOptions();
        if (Environment.GetEnvironmentVariable("DSHLAUNCHER_DISABLE_NO_PROXY") != "1")
        {
            envOptions.AdditionalBrowserArguments = "--no-proxy-server";
        }
        var env = await CoreWebView2Environment.CreateAsync(null, userData, envOptions);
        await _web.EnsureCoreWebView2Async(env);
        var cwv = _web.CoreWebView2;
        cwv.Settings.AreDefaultContextMenusEnabled = true;
        cwv.Settings.AreDevToolsEnabled = false;
        cwv.Settings.IsStatusBarEnabled = false;
        await cwv.AddScriptToExecuteOnDocumentCreatedAsync(WebShell.Script);
        await WebModalRouter.Install(_web);
        cwv.WebMessageReceived += (_, e) =>
        {
            var raw = e.TryGetWebMessageAsString();
            if (WebModalRouter.TryHandle(raw, (action, payload) =>
            {
                if (action == "settings.save") { return; }
                if (action == "ssh.form")
                {
                    if (_main != null)
                    {
                        var name = payload.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var cfg = string.IsNullOrWhiteSpace(name)
                            ? new SshConnectionConfig()
                            : _main.FindSshConnection(name) ?? new SshConnectionConfig();
                        WebModalRouter.Open(_web, "ssh-edit", new { page = "ssh-edit", mode = string.IsNullOrWhiteSpace(name) ? "add" : "edit", originalName = name ?? "", config = cfg });
                    }
                    return;
                }
                if (action == "ssh.save")
                {
                    try
                    {
                        if (_main == null) return;
                        var cfg = JsonSerializer.Deserialize<SshConnectionConfig>(payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        var original = payload.TryGetProperty("originalName", out var oldName) ? oldName.GetString() : null;
                        if (cfg == null || string.IsNullOrWhiteSpace(cfg.Host) || string.IsNullOrWhiteSpace(cfg.User))
                        {
                            MessageBox.Show(this, "请填写主机和用户名。", "SSH 配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        else
                        {
                            _main.SaveSshConnection(cfg, original);
                            WebModalRouter.Open(_web, "ssh", new { page = "ssh", ssh = _main.SshConnectionSnapshot() });
                        }
                    }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "SSH 配置保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    return;
                }
                if (action == "ssh.delete")
                {
                    if (_main != null && payload.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                    {
                        _main.DeleteSshConnection(n.GetString()!);
                        WebModalRouter.Open(_web, "ssh", new { page = "ssh", ssh = _main.SshConnectionSnapshot() });
                    }
                    return;
                }
                if (action == "ssh.connect")
                {
                    if (_main != null && payload.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                        _main.OpenSshConnection(n.GetString()!);
                    return;
                }
                if (action == "logs.open") { WebModalRouter.Open(_web, "logs", new { page="logs", history=ReadHistory() }); return; }
                if (action == "plugins.open") { WebModalRouter.Open(_web, "plugins", new { page="plugins", plugins=Array.Empty<object>() }); return; }
                if (action == "folder.list") { _ = ListFolderAsync(payload); return; }
                if (action == "folder.create") { _ = CreateFolderAsync(payload); return; }
                if (action == "folder.refresh" || action == "folder.list") { _ = ListFolderAsync(payload); return; }
            })) return;
            WebShellBridge.TryHandleWindowCommand(this, raw, action =>
            {
                switch (action)
                {
                    case "settings": WebModalRouter.Open(_web, "settings"); break;
                    case "logs": WebModalRouter.Open(_web, "logs"); break;
                    case "plugins": WebModalRouter.Open(_web, "plugins"); break;
                    case "ssh":
                        if (_main != null) WebModalRouter.Open(_web, "ssh", new { page = "ssh", ssh = _main.SshConnectionSnapshot() });
                        break;
                    case "restart": _ = RestartAsync(); break;
                    case "about": WebModalRouter.Open(_web, "about", new { page="about", version=VersionHelper.Current }); break;
                }
            });
        };
        _web.DefaultBackgroundColor = Color.FromArgb(18, 20, 24);
        cwv.DocumentTitleChanged += (_, _) =>
        {
            var title = cwv.DocumentTitle;
            if (string.IsNullOrWhiteSpace(title)) return;
            SafeUi(() =>
            {
                // 标题固定以产品名开头，并显示当前 SSH 会话名
                var combined = WebShellBridge.FormatSessionTitle(_conn.DisplayName);
                if (Text != combined) Text = combined;
                try
                {
                    var encoded = JsonSerializer.Serialize(combined);
                    _ = cwv.ExecuteScriptAsync($"window.__dshLauncherSetTitle && window.__dshLauncherSetTitle({encoded})");
                }
                catch { }
            });
        };

        // 注入远端文件选择拦截：dsh UI 的「工作区加号」等调起浏览器文件选择器（只能选客户端本地），
        // 这里拦截 input[type=file].click / showDirectoryPicker → 通知启动器弹远端目录浏览器。
        const string pickerScript = @"(function(){
  if (window.__dshRemotePickerInstalled) return;
  window.__dshRemotePickerInstalled = true;
  function notify(msg) { window.chrome.webview.postMessage(msg || 'pick-folder'); }
  function desc(t) {
    if (!t) return 'null';
    var cls = '';
    try { cls = (typeof t.className === 'string' ? t.className : (t.className && t.className.baseVal ? t.className.baseVal : '')).slice(0, 50); } catch(err) {}
    return t.tagName + (t.type ? '[' + t.type + ']' : '') + (t.id ? '#' + t.id : '') + (cls ? '.' + cls : '');
  }
  function fileRelated(e) {
    var t = e.target;
    if (t && t.tagName === 'INPUT' && t.type === 'file') return true;
    // 事件路径（composedPath）里是否有文件 input
    var path = e.composedPath ? e.composedPath() : [];
    for (var i = 0; i < path.length; i++) { if (path[i] && path[i].tagName === 'INPUT' && path[i].type === 'file') return true; }
    // label[for] 关联文件 input
    var lab = t && t.closest ? t.closest('label[for]') : null;
    if (lab) {
      var target = document.getElementById(lab.htmlFor);
      if (target && target.tagName === 'INPUT' && target.type === 'file') return true;
    }
    // 点击元素是文件 input 的兄弟/父级（input 被隐藏，点击 SVG 触发它）
    var p = t && t.parentElement ? t.parentElement : null;
    if (p) {
      if (p.tagName === 'INPUT' && p.type === 'file') return true;
      for (var j = 0; j < p.children.length; j++) {
        if (p.children[j].tagName === 'INPUT' && p.children[j].type === 'file') return true;
      }
    }
    return false;
  }
  // 精准拦截：dsh 前端发起 host.pickDirectory（打开文件夹的 flow 请求，后端会弹系统对话框）
  // —— 拦截该 WebSocket 请求（阻止后端在服务器上弹窗），改为弹启动器远端目录浏览器
  var origSend = WebSocket.prototype.send;
  WebSocket.prototype.send = function(data) {
    try {
      var s = typeof data === 'string' ? data : '[binary]';
      if (s.indexOf('pickDirectory') >= 0 || s.indexOf('workspace.create') >= 0) {
        notify('pick-folder');
        return;
      }
      notify('send:' + s.slice(0, 100));
    } catch(err) {}
    return origSend.apply(this, arguments);
  };
  // 点击层精准拦截：dsh「添加工作区」按钮（aria-label/title 含「添加工作区 / Add workspace」），
  // 阻止其 flow（远端弹系统对话框无效）→ 弹启动器远端浏览器
  function matchAddBtn(t) {
    var b = t && t.closest ? t.closest('button') : null;
    var guard = 0;
    while (b && guard++ < 8) {
      var al = (b.getAttribute('aria-label') || b.title || '');
      if (al.indexOf('添加工作区') >= 0 || al.indexOf('Add workspace') >= 0) return true;
      b = b.parentElement && b.parentElement.closest ? b.parentElement.closest('button') : null;
    }
    return false;
  }
  document.addEventListener('click', function(e) {
    if (matchAddBtn(e.target)) {
      e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation();
      notify('pick-folder');
      return;
    }
  }, true);
  // 动态创建的 input[type=file]（MutationObserver 兜底）
  var origClick = HTMLInputElement.prototype.click;
  HTMLInputElement.prototype.click = function() {
    if (this.type === 'file') { notify('pick-folder'); return; }
    return origClick.apply(this, arguments);
  };
  try {
    var mo = new MutationObserver(function(muts){
      muts.forEach(function(m){
        if (m.addedNodes) m.addedNodes.forEach(function(n){
          if (n.tagName === 'INPUT' && n.type === 'file') {
            n.addEventListener('click', function(ev){ ev.preventDefault(); ev.stopPropagation(); notify('pick-folder'); }, true);
          }
        });
      });
    });
    mo.observe(document, {childList: true, subtree: true});
  } catch(err) {}
  if (window.showDirectoryPicker) {
    window.showDirectoryPicker = function(){ notify('pick-folder'); return new Promise(function(){}); };
  }
  if (window.showOpenFilePicker) {
    window.showOpenFilePicker = function(){ notify('pick-folder'); return new Promise(function(){}); };
  }
  if (window.showSaveFilePicker) {
    window.showSaveFilePicker = function(){ notify('pick-folder'); return new Promise(function(){}); };
  }
})();";
        try { await cwv.AddScriptToExecuteOnDocumentCreatedAsync(pickerScript); } catch { }
        cwv.WebMessageReceived += (_, e) =>
        {
            var msg = e.TryGetWebMessageAsString();
            if (!string.IsNullOrEmpty(msg) && msg.StartsWith("click:") && msg.Length > 60) msg = msg.Substring(0, 60);
            _conn.AppendLog($"[SSH页面] message: {msg}");
            if (msg == "pick-folder") SafeUi(OpenRemoteFolder);
        };

        cwv.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) SafeUi(HideLoading);
            // 诊断：检查文件选择拦截脚本是否注入成功
            try
            {
                _ = cwv.ExecuteScriptAsync("window.__dshRemotePickerInstalled === true ? 'installed' : 'missing'")
                    .ContinueWith(t => _conn.AppendLog($"[SSH页面] picker interceptor: {t.Result ?? "err"}"));
            }
            catch { }
        };
        // WebView2 焦点下快捷键（反射内部 controller）
        try
        {
            var f = typeof(WebView2).GetField("_coreWebView2Controller",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var c = f?.GetValue(_web) as CoreWebView2Controller;
            if (c != null) c.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
        }
        catch { }
        _conn.StateChanged += s => SafeUi(() =>
        {
            if (s == HostState.Starting) ShowLoading($"正在连接 {_conn.DisplayName}…");
            else if (s == HostState.Running) ShowLoading("正在加载界面…");
        });
        _conn.Ready += url => SafeUi(() => { Navigate(url); HideLoading(); });
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private void Navigate(string url)
    {
        try { _web.Source = new Uri(url); } catch { }
    }

    private void ShowLoading(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => ShowLoading(text)); return; }
        _loadingText.Text = text;
        _loadingOverlay.Visible = true;
        _loadingOverlay.BringToFront();
        _spinner.Visible = true;
    }

    private void HideLoading()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(HideLoading); return; }
        _loadingOverlay.Visible = false;
    }

    private void SafeUi(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    // ── 快捷键（作用于本窗口连接）──
    internal void SetWorkAreaMaximizedBounds(Rectangle bounds) => MaximizedBounds = bounds;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84)
        {
            var hit = WebShellBridge.ResizeHitTest(this, PointToClient(Cursor.Position));
            if (hit != 0) { m.Result = (IntPtr)hit; return; }
        }
        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (HandleShortcut(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool HandleShortcut(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Shift | Keys.R: _ = RestartAsync(); return true;
            case Keys.Control | Keys.Shift | Keys.L: WebModalRouter.Open(_web, "logs", new { page="logs", history=ReadHistory() }); return true;
            case Keys.Control | Keys.Shift | Keys.P: WebModalRouter.Open(_web, "plugins", new { page="plugins", plugins=Array.Empty<object>() }); return true;
            case Keys.Control | Keys.Shift | Keys.C: OpenPicker(); return true;
            case Keys.Control | Keys.Shift | Keys.Y: _ = SyncFromLocalAsync(); return true;
            case Keys.Control | Keys.Shift | Keys.O: OpenRemoteFolder(); return true;
            case Keys.Control | Keys.Shift | Keys.Q: Close(); return true;
        }
        return false;
    }

    private void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        var ctrl = (GetKeyState(0x11) & 0x8000) != 0;
        var shift = (GetKeyState(0x10) & 0x8000) != 0;
        if (!ctrl || !shift) return;
        if (HandleShortcut(Keys.Control | Keys.Shift | (Keys)e.VirtualKey)) e.Handled = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    /// <summary>Ctrl+Shift+C：由主窗口弹出连接选择器（选择服务器后连接）。</summary>
    private void OpenPicker()
    {
        if (_main != null)
        {
            _main.ShowConnectionPicker();
        }
        else
        {
            Activate();
        }
    }

    /// <summary>Ctrl+Shift+O：远端目录浏览器 → 选服务器路径写入 dsh 工作区 → 刷新页面（无需文件选择器）。</summary>
    private void OpenRemoteFolder()
    {
        if (_conn is not SshConnection)
        {
            MessageBox.Show(this, "仅 SSH 远程连接支持此功能。", "打开远端文件夹", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // 目录选择器在当前 SSH WebView context 中打开，避免跨窗口串联。
        WebModalRouter.Open(_web, "folder", new { page="folder", connection=_conn.DisplayName });
        return;
        /* using var browser = new RemoteFolderBrowserForm(sc.ListRemoteDirectory);
        if (browser.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(browser.SelectedPath))
        {
            var path = browser.SelectedPath;
            ShowLoading($"正在添加工作区 {path} …");
            // 主路径：dsh RPC workspace.create（后端正常创建，无需重启）
            var (rpcOk, rpcErr) = await sc.CreateWorkspaceRpcAsync(path, line => SafeUi(() => { _loadingText.Text = line; }));
            if (rpcOk)
            {
                HideLoading();
                // 不弹完成确认框 —— 刷新后工作区出现在列表即为反馈
                NavigateCurrent();
                return;
            }
            // fallback：写入 workspace.json + 自动重启远端使生效
            ShowLoading($"RPC 失败（{rpcErr}），改用写入配置文件并重启远端…");
            try
            {
                await sc.AddRemoteWorkspaceAsync(path, line => SafeUi(() => { _loadingText.Text = line; }));
                ShowLoading("正在重启远端 dsh 使工作区生效…");
                await sc.RestartAsync();
                NavigateCurrent();
                HideLoading();
            }
            catch (Exception ex)
            {
                HideLoading();
                MessageBox.Show(this, ex.Message, "添加工作区失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        } */
    }

    /// <summary>重新导航当前 URL（刷新页面）。</summary>
    private void NavigateCurrent()
    {
        try
        {
            if (_web.Source != null) _web.Reload();
        }
        catch { }
    }

    /// <summary>Ctrl+Shift+Y：把本地 dsh 配置与插件同步到本服务器，完成后可选重启远端。</summary>
    private async Task SyncFromLocalAsync()
    {
        if (_syncing) return; // 防重复触发
        _syncing = true;
        ShowLoading("正在同步本地配置与插件…");
        try
        {
            var result = await _conn.SyncFromLocalAsync(line => SafeUi(() =>
            {
                // 同步进度实时显示在加载层
                _loadingText.Text = line;
                Diag.Log(line);
            }));
            HideLoading();
            var restart = MessageBox.Show(this, result + "\n\n插件已同步，是否重启远端 dsh 使生效？", "同步完成",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (restart == DialogResult.Yes)
            {
                ShowLoading("正在重启远端 dsh…");
                try
                {
                    await _conn.RestartAsync();
                    HideLoading();
                }
                catch (Exception ex)
                {
                    HideLoading();
                    MessageBox.Show(this, ex.Message, "重启失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        catch (Exception ex)
        {
            HideLoading();
            MessageBox.Show(this, ex.Message, "同步失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _syncing = false;
        }
    }

    private async Task RestartAsync()
    {
        ShowLoading("正在重启…");
        try { await _conn.RestartAsync(); HideLoading(); }
        catch (Exception ex) { HideLoading(); MessageBox.Show(this, ex.Message, "重启失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private string ReadHistory() { try { return File.Exists(_conn.LogFile) ? File.ReadAllText(_conn.LogFile) : ""; } catch { return ""; } }
    private async Task ListFolderAsync(JsonElement p) { if (_conn is not SshConnection sc) return; var path=p.TryGetProperty("path",out var q)?q.GetString()??"~":"~"; try { var dirs=sc.ListRemoteDirectory(path); WebModalRouter.Open(_web,"folder",new {page="folder",path,dirs}); } catch(Exception ex){ _conn.AppendLog(ex.Message); } await Task.CompletedTask; }
    private async Task CreateFolderAsync(JsonElement p) { if (_conn is not SshConnection sc) return; var path=p.TryGetProperty("path",out var q)?q.GetString():null; if(string.IsNullOrWhiteSpace(path)) return; var (ok,err)=await sc.CreateWorkspaceRpcAsync(path,line=>_conn.AppendLog(line)); if(!ok) { await sc.AddRemoteWorkspaceAsync(path,line=>_conn.AppendLog(line)); } NavigateCurrent(); }
}
