using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

/// <summary>
/// 主窗口：无工具栏/状态栏，WebView2 独占窗口内容。
/// 标题栏配色自动跟随系统深色/浅色模式（DWM），WebView2 背景同为深色，与 dsh 深色 UI 一致。
/// 全部控制入口在托盘菜单与快捷键：Ctrl+Shift+R 重启 / Ctrl+Shift+L 日志 / Ctrl+Shift+P 插件 / Ctrl+Shift+S 设置 / Ctrl+Shift+Q 退出。
/// 关闭窗口默认隐藏到托盘（宿主保持运行）；托盘「退出」才停止服务。
/// </summary>
public sealed class MainForm : Form
{
    private readonly ShellWebView _web = new();
    private readonly NotifyIcon _tray;
    private readonly AppSettings _settings = AppSettings.Load();
    // 连接抽象：本地（HostSupervisor）或 SSH 远端（SshConnection），构造时按设置创建
    private readonly ConnectionManager _connections = new();
    private readonly ManagerAgent _managerAgent;
    private IDshConnection _current = null!;

    private readonly List<ConnectionWindow> _remoteWindows = new();
    private const string ShowEventName = "Local\\DshLauncher_ShowWindow";
    private EventWaitHandle? _showEvent;
    private Thread? _showWatcher;
    private LogForm? _logForm;
    private PluginsForm? _pluginsForm;
    private string? _pendingUpdate;
    private bool _quitting;
    private bool _loadingHiddenGuard;

    /// <summary>当前是否为 SSH 远程连接。</summary>
    /// <summary>当前活动连接（多连接下指向 Tab 当前项，现有代码继续用 _host 引用）。</summary>
    private IDshConnection _host => _current;

    private bool IsRemote => _current.IsRemote;

    // 启动加载覆盖层（dsh 启动/导航期间显示提示与动画）
    private readonly Panel _loadingOverlay = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 20, 24), Visible = true };
    private readonly LoadingSpinner _spinner = new() { Size = new Size(56, 56) };
    private readonly Label _loadingText = new()
    {
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 11f),
        ForeColor = Color.FromArgb(0x9E, 0x9E, 0x9E),
    };

    public MainForm()
    {
        Diag.Log("MainForm ctor start");
        _connections.BuildFrom(_settings);
        _managerAgent = new ManagerAgent(_settings, _connections, line => Diag.Log(line));
        _current = _connections.Local;
        Diag.Log($"connections: {_connections.Connections.Count} ({string.Join(", ", _connections.Connections.Select(c => c.DisplayName))})");
        Text = "DeepSeek Harness";
        FormBorderStyle = FormBorderStyle.None;
        Resize += (_, _) => WebShellBridge.ApplyShape(this);
        // 在窗口句柄/主题初始化前就设置深色背景，避免冷启动首帧出现白条
        var initialPalette = ThemeHelper.GetPalette(ThemeHelper.IsSystemDarkMode());
        BackColor = initialPalette.WindowBack;
        ForeColor = initialPalette.Text;
        _web.DefaultBackgroundColor = initialPalette.WindowBack;
        // 默认大小按屏幕工作区自适应（约 92%），不再固定偏小
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000);
        Width = Math.Max(1280, (int)(wa.Width * 0.92));
        Height = Math.Max(800, (int)(wa.Height * 0.92));
        MinimumSize = new Size(1100, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = LoadAppIcon();
        KeyPreview = true;

        // WebView2 独占窗口内容（无工具栏/状态栏）
        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);

        // 启动加载覆盖层（覆盖在 WebView2 之上，启动/导航期间显示）
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
        WebShellBridge.InstallTitleDragSurface(this);

        // 托盘：全部控制入口
        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "DeepSeek Harness",
            Visible = true, // 常驻托盘：启动即显示，关闭主窗口只是隐藏
        };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("打开主窗口", null, (_, _) => ShowMainWindow());
        // SSH 连接窗口（显示/激活）
        foreach (var c in _connections.Connections.Skip(1))
        {
            var conn = c;
            trayMenu.Items.Add("SSH: " + conn.DisplayName, null, (_, _) => ShowOrOpenRemoteWindow(conn));
        }
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("重启宿主  (Ctrl+Shift+R)", null, (_, _) => _ = RestartHostAsync());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("日志  (Ctrl+Shift+L)", null, (_, _) => ShowLogForm());
        trayMenu.Items.Add("插件管理  (Ctrl+Shift+P)", null, (_, _) => ShowPluginsForm());
        trayMenu.Items.Add("设置  (Ctrl+Shift+S)", null, (_, _) => ShowWebModal("settings"));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("更新 dsh…", null, (_, _) => _ = UpdateDshAsync());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("退出  (Ctrl+Shift+Q)", null, (_, _) => OnQuit());
        trayMenu.Renderer = new ThemeToolStripRenderer(); // WinUI 3 风格主题菜单
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();

        // 本地连接事件（SSH 连接由各自的 ConnectionWindow 订阅处理）
        var local = _connections.Local;
        local.StateChanged += s => SafeUi(() =>
        {
            UpdateTrayStatus(s);
            if (s == HostState.Starting) ShowLoading("正在启动 dsh 服务…");
            else if (s == HostState.Running) ShowLoading("正在加载界面…");
        });
        local.Ready += url => SafeUi(() =>
        {
            Diag.Log("local ready: " + url);
            Navigate(url);
            HideLoading();
        });
        local.UnexpectedExit += diag => SafeUi(() =>
        {
            if (!_quitting && Visible)
            {
                MessageBox.Show(this, diag + "\n\n可用托盘菜单「重启宿主」或 Ctrl+Shift+R 重新启动。", "dsh 异常退出",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });

        // 单实例协作：监听"显示主窗口"事件（第二实例启动时触发，激活本窗口）
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showWatcher = new Thread(() =>
            {
                while (!_quitting)
                {
                    try { _showEvent.WaitOne(); }
                    catch { break; }
                    if (!_quitting) ShowMainWindow();
                }
            })
            { IsBackground = true };
            _showWatcher.Start();
        }
        catch
        {
            // 事件创建失败（权限等）不阻塞启动
        }

        FormClosed += (_, _) =>
        {
            if (_host is IDisposable d) { try { d.Dispose(); } catch { } }
            try { _showEvent?.Dispose(); } catch { }
        };
        Diag.Log("MainForm ctor done");
    }

    // ── 主题适配 ──
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        base.OnHandleDestroyed(e);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) SafeUi(ApplyTheme);
    }

    private void ApplyTheme()
    {
        ThemeHelper.ApplyTitleBarTheme(Handle, ThemeHelper.IsSystemDarkMode());
        // 加载层配色跟随主题
        var p = ThemeHelper.GetPalette(ThemeHelper.IsSystemDarkMode());
        _loadingOverlay.BackColor = p.WindowBack;
        _loadingText.ForeColor = p.MutedText;
        _spinner.SetAccent(p.Accent);
    }

    /// <summary>显示 SSH 连接窗口（已打开则激活，否则新建并连接）。</summary>
    private void ShowOrOpenRemoteWindow(IDshConnection conn)
    {
        var win = _remoteWindows.FirstOrDefault(w => ReferenceEquals(w.Connection, conn));
        if (win != null && !win.IsDisposed)
        {
            win.Show();
            win.Activate();
            return;
        }
        win = new ConnectionWindow(conn);
        _remoteWindows.Add(win);
        win.FormClosed += (_, _) => _remoteWindows.Remove(win);
        win.Show(this);
    }

    /// <summary>Ctrl+Shift+C：循环激活 SSH 连接窗口（再次按下回到主窗口）。</summary>
    /// <summary>Ctrl+Shift+C：弹出连接选择器，列出可连接的服务器，选择后连接/打开窗口。</summary>
    public void ShowConnectionPicker()
    {
        // 打开前同步最新连接配置（设置中添加的服务器立即生效）
        _connections.SyncFrom(_settings);
        if (_connections.Connections.Count <= 1)
        {
            _tray.ShowBalloonTip(3000, "DeepSeek Harness",
                "未配置 SSH 连接。请在设置 → SSH 连接中添加服务器。", ToolTipIcon.Info);
            return;
        }
        using var picker = new ConnectionPickerForm(_connections);
        if (picker.ShowDialog(this) == DialogResult.OK && picker.Selected != null)
        {
            var c = picker.Selected;
            if (c.IsRemote)
            {
                ShowOrOpenRemoteWindow(c); // 已打开则激活，未打开则新建窗口并连接
            }
            else
            {
                if (c.State != HostState.Running) _ = ConnectLocalAsync();
                ShowMainWindow();
            }
        }
    }

    /// <summary>连接本地（供选择器/托盘调用）。</summary>
    private async Task ConnectLocalAsync()
    {
        ShowLoading("正在启动本地 dsh…");
        try { await _current.StartAsync(); }
        catch (Exception ex) { HideLoading(); MessageBox.Show(this, ex.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>显示启动加载层并设置提示文字。</summary>
    private void ShowLoading(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => ShowLoading(text)); return; }
        Diag.Log("ShowLoading: " + text);
        _loadingText.Text = text;
        _loadingOverlay.Visible = true;
        _loadingOverlay.BringToFront();
        Diag.Log($"loading overlay visible={_loadingOverlay.Visible}, size={_loadingOverlay.Width}x{_loadingOverlay.Height}, spinner@{_spinner.Left},{_spinner.Top}");
    }

    /// <summary>隐藏启动加载层。</summary>
    private void HideLoading()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(HideLoading); return; }
        Diag.Log($"HideLoading: wasVisible={_loadingOverlay.Visible}");
        _loadingOverlay.Visible = false;
        // 保险：若 1.5s 后仍被重新显示（如导航竞态），再次隐藏（防止加载层残留导致白屏）
        if (!_loadingHiddenGuard)
        {
            _loadingHiddenGuard = true;
            var t = new System.Threading.Timer(_ =>
            {
                SafeUi(() =>
                {
                    _loadingHiddenGuard = false;
                    if (_loadingOverlay.Visible && !_quitting)
                    {
                        Diag.Log("HideLoading: overlay re-shown, force hide");
                        _loadingOverlay.Visible = false;
                    }
                });
            }, null, TimeSpan.FromMilliseconds(1500), System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Diag.Log("OnShown begin, runtime=" + (WebView2Runtime.InstalledVersion() ?? "null"));
        if (!WebView2Runtime.EnsureInstalled(this)) return;
        ShowLoading("正在启动 DeepSeek Harness…");
        try
        {
            await EnsureWebView2Async();

            // 未安装 dsh → 引导安装（本地连接需要）
            if (!DshUpdater.IsInstalled())
            {
                var res = MessageBox.Show(this,
                    "未检测到 dsh（DeepSeek Harness）。\n\n" +
                    "启动器需要 dsh 提供本地服务。是否现在自动安装？\n" +
                    "（等价于执行：npm install -g @deepseek-ai/dsh）",
                    "需要安装 dsh", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (res == DialogResult.Yes)
                {
                    await InstallDshAndContinueAsync();
                    if (_quitting) return;
                }
                else
                {
                    MessageBox.Show(this,
                        "未安装 dsh，启动器无法启动服务。\n请先安装 Node.js，然后执行：npm install -g @deepseek-ai/dsh",
                        "需要安装 dsh", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    OnQuit();
                    return;
                }
            }

            // 启动本地连接（主窗口）；导航统一由本地 Ready 事件触发（避免双导航竞争导致页面空白）。
            // 远程 SSH 连接不自动打开 —— 按需用 Ctrl+Shift+C 选择器或托盘菜单连接。
            _current = _connections.Local;
            await _current.StartAsync();
            _ = _managerAgent.StartAsync();

            // 启动后异步检查 dsh 更新
            _ = CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            HideLoading();
            MessageBox.Show(this, ex.Message, "DshLauncher 启动失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>初始化 WebView2（独立 user data 目录、深色背景）并收紧导航/权限策略。</summary>
    private async Task EnsureWebView2Async()
    {
        if (_web.CoreWebView2 != null) return;
        Diag.Log("EnsureWebView2Async begin");
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DshLauncher", "webview2");
        // 仅访问 loopback dsh，默认禁用系统代理避免代理干扰本地连接
        var envOptions = new CoreWebView2EnvironmentOptions();
        if (Environment.GetEnvironmentVariable("DSHLAUNCHER_DISABLE_NO_PROXY") != "1")
        {
            envOptions.AdditionalBrowserArguments = "--no-proxy-server";
        }
        var env = await CoreWebView2Environment.CreateAsync(null, userData, envOptions);
        await _web.EnsureCoreWebView2Async(env);

        var cwv = _web.CoreWebView2 ?? throw new InvalidOperationException("WebView2 初始化失败");
        cwv.Settings.AreDefaultContextMenusEnabled = true;
        cwv.Settings.AreDevToolsEnabled = false;
        cwv.Settings.IsStatusBarEnabled = false;
        await cwv.AddScriptToExecuteOnDocumentCreatedAsync(WebShell.Script);
         await WebModalRouter.Install(_web);
        cwv.WebMessageReceived += (_, e) =>
        {
            var raw = e.TryGetWebMessageAsString();
            WebShellBridge.TryHandleWindowCommand(this, raw, action =>
            {
                switch (action)
                {
                    case "settings": ShowWebModal("settings"); break;
                    case "logs": ShowLogForm(); break;
                    case "plugins": ShowPluginsForm(); break;
                    case "ssh": ShowConnectionPicker(); break;
                    case "restart": _ = RestartHostAsync(); break;
                    case "about": ShowWebModal("about"); break;
                }
            });
        };
        // 深色背景防加载白闪（与 dsh 深色 UI 一致）
        _web.DefaultBackgroundColor = Color.FromArgb(18, 20, 24);
        cwv.NavigationStarting += OnNavigationStarting;
        cwv.PermissionRequested += OnPermissionRequested;
        // WebView2 焦点下的快捷键拦截：Chromium 会优先消费 Ctrl+Shift 组合键，
        // 通过内部 CoreWebView2Controller 的 AcceleratorKeyPressed 事件接管（WinForms 控件未公开该属性，反射获取）
        try
        {
            var controllerField = typeof(WebView2).GetField("_coreWebView2Controller",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var controller = controllerField?.GetValue(_web) as CoreWebView2Controller;
            if (controller != null) controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
        }
        catch
        {
            // 反射失败不影响其它功能
        }
        // 窗口标题跟随网页 document.title（dsh 会按会话动态设置，如 "DeepSeek Harness - 评估方案"）
        cwv.DocumentTitleChanged += (_, _) =>
        {
            var title = cwv.DocumentTitle;
            if (string.IsNullOrWhiteSpace(title)) return;
            SafeUi(() =>
            {
                if (Text != title) Text = title;
            });
        };
        cwv.NavigationCompleted += (_, e) =>
        {
            var msg = $"页面加载: 成功={e.IsSuccess} HTTP={e.HttpStatusCode} 错误={e.WebErrorStatus}";
            Diag.Log(msg);
            _host.AppendLog(msg);
            if (e.IsSuccess) SafeUi(HideLoading);
        };
        // 导航失败自动重试（连接类错误，指数退避；用户/守卫取消的导航不重试）
        cwv.NavigationCompleted += OnNavigationFailedRetry;
        Diag.Log("EnsureWebView2Async done");
    }

    private void Navigate(string url)
    {
        if (_quitting || _web.CoreWebView2 == null) return;
        _host.AppendLog("导航到: " + url);
        var target = new Uri(url);
        var sameOrigin = _web.Source != null
            && string.Equals(_web.Source.GetLeftPart(UriPartial.Authority),
                target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
        if (sameOrigin)
        {
            _web.Reload();
        }
        else
        {
            _web.Source = target;
        }
    }

    /// <summary>仅放行 loopback http；外部 http(s) 交给系统浏览器。</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)) return;
        var loopback = (u.Host == "127.0.0.1" || u.Host == "localhost") && u.Scheme == "http";
        if (loopback) return;
        e.Cancel = true;
        if (u.Scheme is "http" or "https")
        {
            try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
        }
    }

    /// <summary>权限默认拒绝；放行剪贴板读取（复制按钮需要）。</summary>
    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead
            ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Deny;
    }

    private int _navFailures;
    private const int MaxNavRetries = 5;

    /// <summary>导航失败自动重试：连接类错误指数退避重载；守卫/用户取消（OperationCanceled）不重试。</summary>
    private async void OnNavigationFailedRetry(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _navFailures = 0;
            return;
        }
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled || _quitting) return;
        _navFailures++;
        if (_navFailures > MaxNavRetries)
        {
            Diag.Log("navigation failed after max retries, prompting");
            SafeUi(HideLoading);
            if (!_quitting)
            {
                _tray.ShowBalloonTip(6000, "DeepSeek Harness",
                    "dsh 页面加载失败（服务可能未就绪）。可右键托盘「重启宿主」或按 Ctrl+Shift+R 重试。",
                    ToolTipIcon.Warning);
            }
            return;
        }
        var delayMs = Math.Min(1000 * _navFailures, 8000);
        Diag.Log($"navigation failed ({e.WebErrorStatus}), retry {_navFailures} in {delayMs}ms");
        await Task.Delay(delayMs);
        if (_quitting || _web.CoreWebView2 == null || IsDisposed) return;
        try { _web.Reload(); } catch { }
    }

    private async Task RestartHostAsync()
    {
        ShowLoading("正在重启宿主…");
        try
        {
            await _host.RestartAsync();
            // 导航统一由连接 Ready 事件处理，避免重启后的双导航
        }
        catch (Exception ex)
        {
            HideLoading();
            MessageBox.Show(this, ex.Message, "重启失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 快捷键（Ctrl+Shift 组合，避免与 dsh Web UI / 浏览器冲突）：
    /// Ctrl+Shift+R 重启 / Ctrl+Shift+L 日志 / Ctrl+Shift+P 插件 / Ctrl+Shift+S 设置 / Ctrl+Shift+Q 退出 / Ctrl+Shift+C 连接切换（SSH 模式）。
    /// </summary>
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

    /// <summary>统一快捷键处理（ProcessCmdKey 与 WebView2 AcceleratorKeyPressed 共用）。</summary>
    private bool HandleShortcut(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Shift | Keys.R: _ = RestartHostAsync(); return true;
            case Keys.Control | Keys.Shift | Keys.L: ShowLogForm(); return true;
            case Keys.Control | Keys.Shift | Keys.P: ShowPluginsForm(); return true;
            case Keys.Control | Keys.Shift | Keys.S: ShowWebModal("settings"); return true;
            case Keys.Control | Keys.Shift | Keys.Q: OnQuit(); return true;
            case Keys.Control | Keys.Shift | Keys.C: ShowConnectionPicker(); return true;
        }
        return false;
    }

    /// <summary>WebView2 焦点下的快捷键：Chromium 优先消费 Ctrl+Shift 组合键，这里接管并标记 Handled。</summary>
    private void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        var ctrl = (GetKeyState(0x11) & 0x8000) != 0; // VK_CONTROL
        var shift = (GetKeyState(0x10) & 0x8000) != 0; // VK_SHIFT
        if (!ctrl || !shift) return;
        var key = (Keys)e.VirtualKey;
        if (HandleShortcut(Keys.Control | Keys.Shift | key)) e.Handled = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    /// <summary>打开（或聚焦）宿主日志窗口。</summary>
    private void ShowLogForm()
    {
        if (_logForm == null || _logForm.IsDisposed)
        {
            _logForm = new LogForm(_host);
            _logForm.Show(this);
        }
        else
        {
            _logForm.Show();
            _logForm.Activate();
        }
    }

    /// <summary>打开（或聚焦）插件管理窗口。</summary>
    private void ShowPluginsForm()
    {
        if (_pluginsForm == null || _pluginsForm.IsDisposed)
        {
            _pluginsForm = new PluginsForm(_host, RestartHostAsync);
            _pluginsForm.Show(this);
        }
        else
        {
            _pluginsForm.Show();
            _pluginsForm.Activate();
        }
    }

    private void ShowWebModal(string page) => WebModalRouter.Open(_web, _settings, page);

    /// <summary>打开设置窗口；保存后把设置应用到宿主。</summary>
    private void ShowSettingsForm()
    {
        using var dlg = new SettingsForm(_settings);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            dlg.Apply();
            // 同步连接列表（复用运行中实例，新增/删除的服务器生效）
            _connections.SyncFrom(_settings);
            _ = _managerAgent.RestartAsync();
            // 本地连接特有设置
            if (_host is HostSupervisor hs)
            {
                hs.AttachPort = _settings.AttachPort;
                hs.WorkingDirectory = _settings.WorkingDirectory
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
        }
    }

    /// <summary>引导安装 dsh（npm install -g @deepseek-ai/dsh@latest），输出实时进日志窗口。</summary>
    private async Task InstallDshAndContinueAsync()
    {
        ShowLogForm();
        _host.AppendLog(">>> npm install -g @deepseek-ai/dsh@latest");
        try
        {
            var code = await _host.UpdateDshAsync(line => _host.AppendLog(line));
            if (code == 0)
            {
                _host.AppendLog("dsh 安装成功");
            }
            else
            {
                _host.AppendLog($"dsh 安装失败（exit {code}）");
                MessageBox.Show(this, "dsh 安装失败，请手动执行：npm install -g @deepseek-ai/dsh",
                    "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                OnQuit();
            }
        }
        catch (Exception ex)
        {
            _host.AppendLog("[err] " + ex.Message);
            MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            OnQuit();
        }
    }

    /// <summary>启动后延迟检查 dsh 更新；有新版本时托盘气泡提示。</summary>
    private async Task CheckForUpdateAsync()
    {
        try
        {
            await Task.Delay(6000);
            var installed = await _host.GetInstalledVersionAsync();
            var latest = await DshUpdater.GetLatestVersionAsync();
            if (DshUpdater.IsNewer(latest, installed))
            {
                _pendingUpdate = latest;
                Diag.Log($"dsh update available: {installed} -> {latest}");
                _tray.ShowBalloonTip(6000, "DeepSeek Harness",
                    $"dsh 有新版本 {latest}（当前 {installed}）。右键托盘菜单「更新 dsh」即可升级。",
                    ToolTipIcon.Info);
            }
        }
        catch
        {
            // 检查失败静默
        }
    }

    /// <summary>检查并更新 dsh（npm install -g @deepseek-ai/dsh@latest），完成后重启宿主。</summary>
    private async Task UpdateDshAsync()
    {
        var installed = await _host.GetInstalledVersionAsync();
        var latest = await DshUpdater.GetLatestVersionAsync();
        if (!DshUpdater.IsNewer(latest, installed))
        {
            MessageBox.Show(this, "dsh 已是最新版本" + (installed != null ? $"（{installed}）" : ""),
                "dsh 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var res = MessageBox.Show(this,
            installed == null
                ? $"检测到最新版 dsh {latest}。是否现在安装？"
                : $"当前 dsh {installed}，最新 {latest}。是否现在更新？",
            "dsh 更新", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (res != DialogResult.OK) return;

        ShowLogForm();
        _host.AppendLog(">>> npm install -g @deepseek-ai/dsh@latest");
        try
        {
            var code = await _host.UpdateDshAsync(line => _host.AppendLog(line));
            if (code == 0)
            {
                _host.AppendLog("dsh 更新成功，正在重启宿主…");
                _pendingUpdate = null;
                MessageBox.Show(this, "dsh 更新成功，正在重启宿主…", "更新完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                await RestartHostAsync();
            }
            else
            {
                _host.AppendLog($"更新失败（exit {code}）");
                MessageBox.Show(this, "更新失败，详见日志窗口。可手动执行：npm install -g @deepseek-ai/dsh@latest",
                    "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            _host.AppendLog("[err] " + ex.Message);
            MessageBox.Show(this, ex.Message, "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>共享应用图标（供 ConnectionWindow 使用）。</summary>
    public static Icon LoadAppIconShared() => LoadAppIcon();

    /// <summary>从嵌入资源加载应用图标。</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("DshLauncher.app.ico");
            if (stream != null) return new Icon(stream);
        }
        catch
        {
            // 资源缺失时回退系统图标
        }
        return SystemIcons.Application;
    }

    /// <summary>更新托盘 ToolTip 反映宿主状态。</summary>
    private void UpdateTrayStatus(HostState s)
    {
        var tip = s switch
        {
            HostState.Running => "DeepSeek Harness · " + (IsRemote ? "远端已连接" : _host is HostSupervisor { IsAttached: true } ? "已连接外部实例" : "运行中"),
            HostState.Starting => "DeepSeek Harness · 启动中…",
            HostState.Failed => "DeepSeek Harness · 异常",
            _ => "DeepSeek Harness",
        };
        if (tip.Length > 63) tip = tip[..63];
        _tray.Text = tip;
    }

    /// <summary>
    /// 关闭窗口：默认隐藏到托盘（宿主保持运行）；设置 CloseExits 时停止宿主并退出。
    /// 托盘「退出」始终真正停止。
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_quitting) return;
        e.Cancel = true;
        if (_settings.CloseExits)
        {
            _quitting = true;
            _tray.Visible = false;
            foreach (var c in _connections.Connections) { try { c.StopAsync().GetAwaiter().GetResult(); } catch { } }
            try { _managerAgent.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            Application.Exit();
        }
        else
        {
            Hide();
            _tray.ShowBalloonTip(2000, "DeepSeek Harness", "已最小化到托盘，dsh 服务保持运行。", ToolTipIcon.Info);
        }
    }

    private void ShowMainWindow()
    {
        SafeUi(() =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        });
    }

    private async void OnQuit()
    {
        _quitting = true;
        _tray.Visible = false;
        foreach (var c in _connections.Connections) { try { await c.StopAsync(); } catch { } }
        try { await _managerAgent.DisposeAsync(); } catch { }
        Application.Exit();
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}