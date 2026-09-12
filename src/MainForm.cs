using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
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
    private readonly WindowsNotificationService _notifications;
    private readonly AppSettings _settings = AppSettings.Load();
    // 连接抽象：本地（HostSupervisor）或 SSH 远端（SshConnection），构造时按设置创建
    private readonly ConnectionManager _connections = new();
    private readonly ManagerAgent _managerAgent;
    private readonly ManagerFrontendClient _managerFrontend;
    private readonly DshInteractionCoordinator _interactions = new();
    private readonly BrowserDshInteractionReplyTracker _browserInteractionReplies = new();
    private ToolStripMenuItem? _pendingInteractionsMenu;
    private DshInteractionWebOverlayForm? _interactionOverlay;
    private ManagerInteractionEventClient? _managerInteractions;
    private IDshConnection _current = null!;

    private readonly List<ConnectionWindow> _remoteWindows = new();
    private readonly List<ManagerConnectionWindow> _managerWindows = new();
    private int _managerRequestId;
    // 登录请求是单飞操作；WebView 中的双击或重复事件不得创建多次登录/弹窗刷新。
    private int _managerLoginInProgress;
    private readonly List<LinkWindow> _linkWindows = new();
    private const string ShowEventName = "Local\\DshLauncher_ShowWindow";
    private EventWaitHandle? _showEvent;
    private Thread? _showWatcher;
    private string? _pendingUpdate;
    private int _aboutRequestId;
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
        _managerFrontend = new ManagerFrontendClient(_settings);
        _managerAgent = new ManagerAgent(_settings, _connections, line =>
        {
            Diag.Log(line);
            _host.AppendLog(line);
        });
        _interactions.InteractionReady += interaction => SafeUi(() => ShowInteractionOverlay(interaction));
        _interactions.InteractionCancelled += interaction => SafeUi(() =>
        {
            if (_interactionOverlay is { IsDisposed: false } overlay && overlay.Matches(interaction))
            {
                _interactionOverlay = null;
                overlay.DismissCancelled();
            }
        });
        _interactions.PendingCountChanged += count => SafeUi(() => UpdatePendingInteractionMenu(count));
        _interactions.RefreshConnections(_connections.Connections);
        _current = _connections.Local;
        Diag.Log($"connections: {_connections.Connections.Count} ({string.Join(", ", _connections.Connections.Select(c => c.DisplayName))})");
        Text = "DeepSeek Harness";
        FormBorderStyle = FormBorderStyle.None;
        MinimizeBox = true;
        // 无边框窗口仍保留最小化系统样式，确保任务栏点击可最小化。
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

        // 托盘：全部控制入口
        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "DeepSeek Harness",
            Visible = true, // 常驻托盘：启动即显示，关闭主窗口只是隐藏
        };
        _notifications = new WindowsNotificationService(_tray);
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
        trayMenu.Items.Add("日志  (Ctrl+Shift+L)", null, (_, _) => ShowMainModal("logs", new { page = "logs", history = ReadLocalHistory() }));
        trayMenu.Items.Add("插件管理  (Ctrl+Shift+P)", null, (_, _) => ShowMainModal("plugins", new { page = "plugins", plugins = ListLocalPlugins(), canManagePlugins = true }));
        trayMenu.Items.Add("设置  (Ctrl+Shift+S)", null, (_, _) => ShowMainModal("settings"));
        trayMenu.Items.Add("dsh-manager  (Ctrl+Shift+M)", null, (_, _) => _ = ShowManagerAsync());
        _pendingInteractionsMenu = new ToolStripMenuItem("待处理确认 (0)", null, (_, _) => ShowPendingInteraction());
        _pendingInteractionsMenu.Enabled = false;
        trayMenu.Items.Add(_pendingInteractionsMenu);
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
            _notifications.Dispose();
            try { _managerInteractions?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            _managerFrontend.Dispose();
            try { _interactions.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
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
        win = new ConnectionWindow(conn, this);
        _remoteWindows.Add(win);
        win.FormClosed += (_, _) => _remoteWindows.Remove(win);
        win.Show();
    }

    /// <summary>Ctrl+Shift+C：循环激活 SSH 连接窗口（再次按下回到主窗口）。</summary>
    /// <summary>Ctrl+Shift+C：弹出连接选择器，列出可连接的服务器，选择后连接/打开窗口。</summary>
    public void ShowConnectionPicker()
    {
        // 打开前同步最新连接配置（设置中添加的服务器立即生效）。
        // 即使当前没有 SSH 连接也必须打开空列表弹窗，否则用户无法进入「新增 SSH」。
        _connections.SyncFrom(_settings); _interactions.RefreshConnections(_connections.Connections);
        ShowMainModal("ssh", new { page = "ssh", ssh = _settings.SshConnections });
    }

    /// <summary>连接本地（供选择器/托盘调用）。</summary>
    private async Task ConnectLocalAsync()
    {
        ShowLoading("正在启动本地 dsh…");
        try { await _current.StartAsync(); }
        catch (Exception ex) { HideLoading(); MessageBox.Show(this, GetHostFailureMessage(ex), "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
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
            MessageBox.Show(this, GetHostFailureMessage(ex), "DshLauncher 启动失败",
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
        // Keep a narrow diagnostic trace for DSH RPCs. It contains only method,
        // endpoint and status (never bodies, cookies or startup tokens), and lets
        // us prove whether WebView2 issues duplicate session-resume requests.
        cwv.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        cwv.WebResourceRequested += (_, request) =>
        {
            if (Uri.TryCreate(request.Request.Uri, UriKind.Absolute, out var uri) && uri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                Diag.Log($"[DSH API] -> {request.Request.Method} {uri.AbsolutePath}");
        };
        cwv.WebResourceResponseReceived += (_, response) =>
        {
            try
            {
                if (Uri.TryCreate(response.Request.Uri, UriKind.Absolute, out var uri) && uri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                    Diag.Log($"[DSH API] <- {(int)response.Response.StatusCode} {response.Request.Method} {uri.AbsolutePath}");
            }
            catch { }
        };
        await cwv.AddScriptToExecuteOnDocumentCreatedAsync(WebShell.Script);
        await cwv.AddScriptToExecuteOnDocumentCreatedAsync(WebShell.BrowserInteractionScript);
        await cwv.AddScriptToExecuteOnDocumentCreatedAsync(WebShell.BrowserInteractionConfigScript(_settings.HandleAgentQuestions));
        await WebModalRouter.Install(_web);
        cwv.WebMessageReceived += (_, e) =>
        {
            var raw = e.TryGetWebMessageAsString();
            Diag.Log($"WebMessageReceived raw={raw}");
            if (_browserInteractionReplies.TryComplete(raw)) return;
            if (HandleBrowserInteraction(_connections.Local, raw, ReplyMainBrowserInteractionAsync)) return;
            if (BrowserNotificationBridge.TryParse(raw, out var notice))
            {
                _notifications.Show(notice.Title, notice.Body, ShowMainWindow, notice.RequireInteraction);
                return;
            }
            if (WebModalRouter.TryHandle(raw, (action, payload) =>
            {
                if (action == "settings.save") { WebModalRouter.Apply(_settings, payload); ApplyAgentQuestionHandling(); _connections.SyncFrom(_settings); _interactions.RefreshConnections(_connections.Connections); _ = _managerAgent.RestartAsync(); return; }
                if (action == "logs.open") { ShowWebModal("logs", new { page="logs", history=ReadHistory() }); return; }
                if (action == "logs.clear") { try { File.WriteAllText(_host.LogFile, string.Empty); } catch { } ShowWebModal("logs", new { page="logs", history=string.Empty }); return; }
                if (action == "plugins.open" || action == "plugins.list") { ShowPluginsModal(); return; }
                if (action == "plugins.export") { ExportPlugins(); return; }
                if (action == "plugins.import") { _ = ImportPluginsAsync(); return; }
                if (action == "launcher.checkUpdate") { _ = ShowAboutAsync(checkUpdates: true); return; }
                if (action == "dsh.update") { _ = UpdateDshAsync(); return; }
                if (action == "manager.login") { _ = LoginManagerAsync(payload); return; }
                if (action == "manager.refresh") { _ = ShowManagerAsync(); return; }
                if (action == "manager.logout") { _ = LogoutManagerAsync(); return; }
                if (action == "manager.command") { _ = RunManagerCommandAsync(payload); return; }
                if (action == "manager.open") { _ = OpenManagerInstanceAsync(payload); return; }
                if (action.StartsWith("plugins.") && payload.ValueKind == JsonValueKind.Object)
                {
                    var pkg = payload.TryGetProperty("package", out var q) ? q.GetString() : null;
                    var verb = action[9..];
                    if (verb == "install" && !string.IsNullOrWhiteSpace(pkg)) _ = _host.RunPluginAsync(new[] { "add", pkg }, x => _host.AppendLog(x));
                    else if (verb == "remove" && !string.IsNullOrWhiteSpace(pkg)) _ = _host.RunPluginAsync(new[] { "remove", pkg }, x => _host.AppendLog(x));
                    else if (verb == "update") _ = _host.RunPluginAsync(string.IsNullOrWhiteSpace(pkg) ? new[] { "update" } : new[] { "update", pkg }, x => _host.AppendLog(x));
                    return;
                }
                if (action == "ssh.form")
                {
                    var name = payload.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var cfg = string.IsNullOrWhiteSpace(name)
                        ? new SshConnectionConfig()
                        : _settings.SshConnections.FirstOrDefault(x => x.Name == name) ?? new SshConnectionConfig();
                    WebModalRouter.Open(_web, "ssh-edit", new { page = "ssh-edit", mode = string.IsNullOrWhiteSpace(name) ? "add" : "edit", originalName = name ?? "", config = cfg });
                    return;
                }
                if (action == "ssh.save")
                {
                    try
                    {
                        var cfg = JsonSerializer.Deserialize<SshConnectionConfig>(payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        var original = payload.TryGetProperty("originalName", out var oldName) ? oldName.GetString() : null;
                        if (cfg == null || string.IsNullOrWhiteSpace(cfg.Host) || string.IsNullOrWhiteSpace(cfg.User))
                        {
                            MessageBox.Show(this, "请填写主机和用户名。", "SSH 配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        else
                        {
                            SaveSshConnection(cfg, original);
                            WebModalRouter.Open(_web, "ssh", new { page = "ssh", ssh = _settings.SshConnections });
                        }
                    }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "SSH 配置保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    return;
                }
                if (action == "ssh.delete")
                {
                    var name = payload.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        DeleteSshConnection(name);
                        WebModalRouter.Open(_web, "ssh", new { page = "ssh", ssh = _settings.SshConnections });
                    }
                    return;
                }
                if (action == "ssh.connect")
                {
                    var name = payload.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name)) OpenSshConnection(name);
                    return;
                }
            })) return;
            WebShellBridge.TryHandleWindowCommand(this, raw, action =>
            {
                switch (action)
                {
                    case "settings": ShowWebModal("settings"); break;
                    case "logs": ShowLogForm(); break;
                    case "plugins": ShowPluginsForm(); break;
                    case "ssh": ShowConnectionPicker(); break;
                    case "manager": _ = ShowManagerAsync(); break;
                    case "restart": _ = RestartHostAsync(); break;
                    case "about": _ = ShowAboutAsync(); break;
                }
            });
        };
        // 深色背景防加载白闪（与 dsh 深色 UI 一致）
        _web.DefaultBackgroundColor = Color.FromArgb(18, 20, 24);
        cwv.NavigationStarting += OnNavigationStarting;
        cwv.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternalLink(e.Uri);
        };
        WebView2PermissionPolicy.Attach(cwv);
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
                var displayTitle = WebShellBridge.FormatSessionTitle(title);
                if (Text != displayTitle) Text = displayTitle;
                try
                {
                    var encoded = JsonSerializer.Serialize(displayTitle);
                    _ = cwv.ExecuteScriptAsync($"window.__dshLauncherSetTitle && window.__dshLauncherSetTitle({encoded})");
                }
                catch { }
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
        var target = new Uri(url);
        _host.AppendLog("导航到: " + target.GetLeftPart(UriPartial.Path) + (string.IsNullOrEmpty(target.Query) ? "" : "?[redacted]"));
        var sameOrigin = _web.Source != null
            && string.Equals(_web.Source.GetLeftPart(UriPartial.Authority),
                target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
        if (sameOrigin && string.IsNullOrEmpty(target.Query))
        {
            _web.Reload();
        }
        else
        {
            _web.Source = target;
        }
    }

    /// <summary>拦截 dsh 外部链接：按设置打开独立 WebView2 或系统浏览器。</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)) return;
        var loopback = (u.Host == "127.0.0.1" || u.Host == "localhost") && u.Scheme == "http";
        if (loopback) return;
        e.Cancel = true;
        OpenExternalLink(e.Uri);
    }

    internal void OpenExternalLink(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target)) return;
        SafeUi(() =>
        {
            if (_settings.OpenLinksInWebView && (target.Scheme is "http" or "https"))
            {
                var window = new LinkWindow(this, target);
                _linkWindows.Add(window);
                window.FormClosed += (_, _) => _linkWindows.Remove(window);
                window.Show();
                return;
            }
            try { Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true }); } catch { }
        });
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
            MessageBox.Show(this, GetHostFailureMessage(ex), "重启失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 快捷键（Ctrl+Shift 组合，避免与 dsh Web UI / 浏览器冲突）：
    /// Ctrl+Shift+R 重启 / Ctrl+Shift+L 日志 / Ctrl+Shift+P 插件 / Ctrl+Shift+S 设置 / Ctrl+Shift+Q 退出 / Ctrl+Shift+C 连接切换（SSH 模式）。
    /// </summary>
    internal void SetWorkAreaMaximizedBounds(Rectangle bounds) => MaximizedBounds = bounds;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // FormBorderStyle.None 不一定生成 WS_MINIMIZEBOX；补上后 Shell 会把任务栏点击
            // 转换为 SC_MINIMIZE，而不是仅激活当前窗口。
            cp.Style |= 0x00020000; // WS_MINIMIZEBOX
            cp.Style |= 0x00080000; // WS_SYSMENU
            cp.ExStyle |= 0x00040000; // WS_EX_APPWINDOW
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0112 && (m.WParam.ToInt64() & 0xFFF0L) == 0xF020L) // WM_SYSCOMMAND/SC_MINIMIZE
        {
            WindowState = FormWindowState.Minimized;
            return;
        }
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
            case Keys.Control | Keys.Shift | Keys.M: _ = ShowManagerAsync(); return true;
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
    private void ShowLogForm() => ShowMainModal("logs", new { page = "logs", history = ReadLocalHistory() });

    /// <summary>打开（或聚焦）插件管理窗口。</summary>
    private void ShowPluginsForm() => ShowMainModal("plugins", new { page = "plugins", plugins = ListLocalPlugins(), canManagePlugins = true });

    private void ShowWebModal(string page) => WebModalRouter.Open(_web, _settings, page);

    private void ShowWebModal(string page, object data) => WebModalRouter.Open(_web, page, data);

    /// <summary>打开关于窗口；检查按钮会并行检查 DshLauncher 与当前 dsh。</summary>
    private async Task ShowAboutAsync(bool checkUpdates = false)
    {
        var requestId = Interlocked.Increment(ref _aboutRequestId);
        ShowMainModal("about", BuildAboutData("loading", null, null, null, checkUpdates, true));

        string? dshVersion = null;
        LauncherUpdater.UpdateCheckResult? launcherUpdate = null;
        DshUpdater.UpdateCheckResult? dshUpdate = null;
        if (checkUpdates)
        {
            // 两个检查彼此独立并行执行，避免网络/进程探测串行叠加等待时间。
            var launcherTask = LauncherUpdater.CheckForUpdateAsync();
            var dshTask = DshUpdater.CheckForUpdateAsync();
            try { launcherUpdate = await launcherTask; }
            catch (Exception ex) { Diag.Log("检查 DshLauncher 更新失败: " + ex.Message); }
            try { dshUpdate = await dshTask; }
            catch (Exception ex) { Diag.Log("检查 dsh 更新失败: " + ex.Message); }
            dshVersion = dshUpdate?.InstalledVersion;
        }
        else
        {
            try
            {
                dshVersion = await _host.GetInstalledVersionAsync();
            }
            catch (Exception ex)
            {
                Diag.Log("读取 dsh 版本失败: " + ex.Message);
            }
        }

        if (IsDisposed || _quitting || requestId != Volatile.Read(ref _aboutRequestId)) return;
        ShowMainModal("about", BuildAboutData(dshVersion == null ? "missing" : "ready", dshVersion, launcherUpdate, dshUpdate, false, true));
    }

    internal static object BuildAboutData(
        string dshVersionState,
        string? dshVersion,
        LauncherUpdater.UpdateCheckResult? launcherUpdate,
        DshUpdater.UpdateCheckResult? dshUpdate,
        bool checking,
        bool canUpdateDsh)
    {
        var launcherState = checking
            ? "checking"
            : launcherUpdate == null
                ? "idle"
                : launcherUpdate.Error != null
                    ? "error"
                    : launcherUpdate.IsUpdateAvailable ? "available" : "upToDate";
        var dshState = checking
            ? "checking"
            : dshUpdate == null
                ? "idle"
                : dshUpdate.Error != null
                    ? "error"
                    : dshUpdate.IsUpdateAvailable ? "available" : "upToDate";

        var launcherMessage = launcherUpdate?.Error;
        if (launcherMessage == null && launcherUpdate?.IsUpdateAvailable == true)
            launcherMessage = $"发现新版本 {launcherUpdate.LatestVersion}";
        if (launcherMessage == null && launcherUpdate != null)
            launcherMessage = launcherUpdate.LatestVersion == null
                ? "无法获取最新版本"
                : $"当前已是最新版本（{launcherUpdate.LatestVersion}）";

        var dshMessage = dshUpdate?.Error;
        if (dshMessage == null && dshUpdate?.IsUpdateAvailable == true)
            dshMessage = $"发现新版本 {dshUpdate.LatestVersion}（当前 {dshUpdate.InstalledVersion}）";
        if (dshMessage == null && dshUpdate != null)
            dshMessage = dshUpdate.LatestVersion == null
                ? "无法获取最新版本"
                : $"当前已是最新版本（{dshUpdate.LatestVersion}）";

        return new
        {
            page = "about",
            version = VersionHelper.Current,
            projectUrl = LauncherUpdater.ProjectUrl,
            dshVersion,
            dshVersionState,
            launcherUpdateState = launcherState,
            launcherLatestVersion = launcherUpdate?.LatestVersion,
            launcherReleaseUrl = launcherUpdate?.ReleaseUrl,
            launcherDownloadUrl = launcherUpdate?.DownloadUrl,
            launcherReleaseName = launcherUpdate?.ReleaseName,
            launcherUpdateMessage = launcherMessage,
            dshUpdateState = dshState,
            dshLatestVersion = dshUpdate?.LatestVersion,
            dshUpdateMessage = dshMessage,
            canUpdateDsh,
        };
    }


    private void ExportPlugins()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出插件列表",
            Filter = "DshLauncher 插件列表 (*.json)|*.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            DefaultExt = "json",
            AddExtension = true,
            FileName = $"dsh-plugins-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var manager = new PluginManager();
            var count = manager.ListPlugins().Count;
            manager.ExportToFile(dialog.FileName);
            MessageBox.Show(this, $"已导出 {count} 个插件的信息（包含实际安装版本）。", "导出完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出插件列表失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ImportPluginsAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "导入插件列表",
            Filter = "DshLauncher 插件列表 (*.json)|*.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        PluginManager.PluginListDocument document;
        try
        {
            document = PluginManager.ReadFromFile(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导入插件列表失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var items = PluginManager.GetImportItems(document);
        if (items.Count == 0)
        {
            MessageBox.Show(this, "文件中没有可安装的插件（内置模板会自动跳过）。", "导入插件列表",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var preview = string.Join(Environment.NewLine, items.Take(12).Select(x =>
            "• " + x.InstallSpecifier));
        if (items.Count > 12) preview += Environment.NewLine + $"…以及另外 {items.Count - 12} 个插件";
        var confirm = MessageBox.Show(this,
            $"将按导出文件中的版本安装 {items.Count} 个插件：\n\n{preview}\n\n是否继续？",
            "确认导入插件列表", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        ShowLogForm();
        _host.AppendLog($">>> 导入插件列表: {dialog.FileName}");
        _host.AppendLog($"待安装插件: {items.Count} 个（按导出版本优先）");
        var succeeded = 0;
        var failed = new List<string>();
        foreach (var item in items)
        {
            var spec = item.InstallSpecifier;
            if (string.IsNullOrWhiteSpace(spec)) continue;
            _host.AppendLog($">>> dsh plugin --profile web add {spec}");
            try
            {
                var code = await _host.RunPluginAsync(new[] { "add", spec }, line => _host.AppendLog(line));
                if (code == 0)
                {
                    succeeded++;
                    _host.AppendLog($"插件 {item.Package} ✓");
                }
                else
                {
                    failed.Add($"{item.Package}（exit {code}）");
                    _host.AppendLog($"插件 {item.Package} 失败（exit {code}）");
                }
            }
            catch (Exception ex)
            {
                failed.Add($"{item.Package}（{ex.Message}）");
                _host.AppendLog($"插件 {item.Package} 异常: {ex.Message}");
            }
        }

        var restartFailed = false;
        if (succeeded > 0)
        {
            ShowLoading("正在重启 dsh 使插件生效…");
            try
            {
                await _host.RestartAsync();
            }
            catch (Exception ex)
            {
                restartFailed = true;
                HideLoading();
                _host.AppendLog("重启 dsh 失败: " + ex.Message);
            }
        }
        ShowPluginsModal();

        var summary = $"导入完成：成功 {succeeded} 个，失败 {failed.Count} 个。";
        if (restartFailed) summary += "\n\ndsh 重启失败，请查看日志。";
        if (failed.Count > 0)
            summary += "\n\n失败项：\n" + string.Join("\n", failed.Take(12));
        MessageBox.Show(this, summary, "导入插件列表", MessageBoxButtons.OK,
            failed.Count == 0 && !restartFailed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    internal void ShowSystemNotification(string title, string body, Action activate, bool requireInteraction = false) =>
        SafeUi(() => _notifications.Show(title, body, activate, requireInteraction));

    internal IReadOnlyList<SshConnectionConfig> SshConnectionSnapshot() => _settings.SshConnections.ToList();

    internal SshConnectionConfig? FindSshConnection(string name) =>
        _settings.SshConnections.FirstOrDefault(x => x.Name == name);

    internal void SaveSshConnection(SshConnectionConfig config, string? originalName = null)
    {
        var old = !string.IsNullOrWhiteSpace(originalName)
            ? _settings.SshConnections.FirstOrDefault(x => x.Name == originalName)
            : _settings.SshConnections.FirstOrDefault(x => x.Name == config.Name);
        if (old != null) _settings.SshConnections.Remove(old);
        _settings.SshConnections.Add(config);
        _settings.Save();
        _connections.SyncFrom(_settings); _interactions.RefreshConnections(_connections.Connections);
    }

    internal void DeleteSshConnection(string name)
    {
        _settings.SshConnections.RemoveAll(x => x.Name == name);
        _settings.Save();
        _connections.SyncFrom(_settings); _interactions.RefreshConnections(_connections.Connections);
    }

    internal void OpenSshConnection(string name)
    {
        var connection = _connections.Connections.OfType<SshConnection>().FirstOrDefault(x => x.Config.Name == name);
        if (connection != null) ShowOrOpenRemoteWindow(connection);
    }

    private string ReadLocalHistory()
    {
        try { return File.Exists(_connections.Local.LogFile) ? File.ReadAllText(_connections.Local.LogFile) : string.Empty; }
        catch { return string.Empty; }
    }

    private object ListLocalPlugins() => new PluginManager().ListPlugins();

    private string ReadHistory() => ReadLocalHistory();

    private void ShowPluginsModal() => ShowMainModal("plugins", new { page = "plugins", plugins = ListLocalPlugins(), canManagePlugins = true });

    private async Task ShowManagerAsync(string? notice = null, string? error = null)
    {
        var requestId = Interlocked.Increment(ref _managerRequestId);
        var initial = new ManagerDashboardSnapshot
        {
            ServerUrl = _managerFrontend.CurrentServerUrl,
            Authenticated = false,
            Username = _settings.ManagerFrontend.Username,
            Notice = notice ?? (string.IsNullOrWhiteSpace(error) ? "正在连接 manager…" : null),
            Error = error,
        };
        ShowMainModal("manager", BuildManagerModalData(initial));

        ManagerDashboardSnapshot snapshot;
        try
        {
            snapshot = await _managerFrontend.LoadDashboardAsync();
        }
        catch (Exception ex)
        {
            snapshot = new ManagerDashboardSnapshot
            {
                ServerUrl = _managerFrontend.CurrentServerUrl,
                Authenticated = false,
                Username = _settings.ManagerFrontend.Username,
                Error = ex.Message,
            };
        }

        if (notice != null || error != null)
            snapshot = new ManagerDashboardSnapshot
            {
                ServerUrl = snapshot.ServerUrl,
                Authenticated = snapshot.Authenticated,
                Username = snapshot.Username,
                ManagerVersion = snapshot.ManagerVersion,
                Agents = snapshot.Agents,
                Instances = snapshot.Instances,
                Diagnostics = snapshot.Diagnostics,
                Error = error ?? snapshot.Error,
                Notice = notice,
            };
        if (snapshot.Authenticated) EnsureManagerInteractionStream();
        if (requestId == Volatile.Read(ref _managerRequestId) && !_quitting && !IsDisposed)
            ShowMainModal("manager", BuildManagerModalData(snapshot));
    }

    private object BuildManagerModalData(ManagerDashboardSnapshot snapshot)
    {
        return new
        {
            page = "manager",
            manager = new
            {
                serverUrl = snapshot.ServerUrl,
                authenticated = snapshot.Authenticated,
                username = snapshot.Username,
                managerVersion = snapshot.ManagerVersion,
                agents = snapshot.Agents,
                instances = snapshot.Instances,
                diagnostics = snapshot.Diagnostics,
                error = snapshot.Error,
                notice = snapshot.Notice,
            },
        };
    }

    private async Task LoginManagerAsync(JsonElement payload)
    {
        // Browser click events can be delivered more than once before the modal re-renders.
        // Keep exactly one request active so a failed login cannot reopen the panel repeatedly.
        if (Interlocked.Exchange(ref _managerLoginInProgress, 1) != 0) return;

        var serverUrl = GetPayloadString(payload, "serverUrl");
        var username = GetPayloadString(payload, "username");
        var password = GetPayloadString(payload, "password");
        Interlocked.Increment(ref _managerRequestId); // discard an older dashboard load while signing in
        try
        {
            await _managerFrontend.LoginAsync(serverUrl, username, password);
            EnsureManagerInteractionStream();
            await ShowManagerAsync(notice: "manager 登录成功");
        }
        catch (Exception ex)
        {
            // Do not call ShowManagerAsync here: it first opens a loading modal and makes
            // another auth request, which made one failed login look like repeated popups.
            ShowManagerLoginError(serverUrl, username, ex.Message);
        }
        finally
        {
            Volatile.Write(ref _managerLoginInProgress, 0);
        }
    }

    private void ShowManagerLoginError(string serverUrl, string username, string error)
    {
        var snapshot = new ManagerDashboardSnapshot
        {
            ServerUrl = string.IsNullOrWhiteSpace(_managerFrontend.CurrentServerUrl) ? serverUrl : _managerFrontend.CurrentServerUrl,
            Authenticated = false,
            Username = username,
            Error = error,
        };
        ShowMainModal("manager", BuildManagerModalData(snapshot));
    }

    private void EnsureManagerInteractionStream()
    {
        if (_managerInteractions != null) return;
        var client = _managerFrontend.CreateInteractionEventClient(_interactions.PublishExternal, _interactions.CancelExternal);
        if (client == null) return;
        _managerInteractions = client;
        client.Start();
    }

    private async Task LogoutManagerAsync()
    {
        try
        {
            if (_managerInteractions != null) { await _managerInteractions.DisposeAsync(); _managerInteractions = null; }
            await _managerFrontend.LogoutAsync();
            await ShowManagerAsync(notice: "已退出 manager");
        }
        catch (Exception ex)
        {
            await ShowManagerAsync(error: ex.Message);
        }
    }

    private async Task RunManagerCommandAsync(JsonElement payload)
    {
        var agentId = GetPayloadString(payload, "agentId");
        var instanceId = GetPayloadString(payload, "instanceId");
        var action = GetPayloadString(payload, "action");
        try
        {
            var result = await _managerFrontend.SendCommandAsync(agentId, instanceId, action);
            await Task.Delay(250);
            await ShowManagerAsync(notice: $"已提交 {action} 命令" + (string.IsNullOrWhiteSpace(result.RequestId) ? "" : $"（{result.RequestId}）"));
        }
        catch (Exception ex)
        {
            await ShowManagerAsync(error: ex.Message);
        }
    }

    private async Task OpenManagerInstanceAsync(JsonElement payload)
    {
        var agentId = GetPayloadString(payload, "agentId");
        var instanceId = GetPayloadString(payload, "instanceId");
        try
        {
            var result = await _managerFrontend.OpenInstanceAsync(agentId, instanceId);
            if (string.IsNullOrWhiteSpace(result.AbsoluteUrl)) throw new InvalidOperationException("manager 未返回可用的 dsh 地址。");
            var target = new Uri(result.AbsoluteUrl, UriKind.Absolute);
            var existing = _managerWindows.FirstOrDefault(x => string.Equals(x.TargetKey, target.AbsoluteUri, StringComparison.OrdinalIgnoreCase));
            if (existing != null && !existing.IsDisposed)
            {
                existing.Show();
                existing.Activate();
                return;
            }
            var window = new ManagerConnectionWindow(this, target, $"{agentId}/{instanceId}", _managerFrontend.GetBrowserCookies());
            _managerWindows.Add(window);
            window.FormClosed += (_, _) => _managerWindows.Remove(window);
            window.Show();
        }
        catch (Exception ex)
        {
            await ShowManagerAsync(error: ex.Message);
        }
    }

    internal Task ShowManagerFromChildAsync() => ShowManagerAsync();

    /// <summary>供 SSH/manager 子窗口打开唯一的本机 Launcher 设置页。</summary>
    internal void ShowSettingsFromChild() => ShowMainModal("settings");

    internal void ShowAboutFromChild() => _ = ShowAboutAsync();

    internal void ShowLogsFromChild() => ShowLogForm();

    internal void ShowPluginsFromChild() => ShowPluginsForm();

    private static string GetPayloadString(JsonElement payload, string name)
    {
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    /// <summary>显示主窗口并在 UI 队列中打开本地 modal，避免隐藏窗口上的 WebView 调用。</summary>
    private void ShowMainModal(string page, object? data = null)
    {
        SafeUi(() =>
        {
            if (IsDisposed || Disposing) return;
            var restoreFromMinimized = WindowState == FormWindowState.Minimized;
            Show();
            if (restoreFromMinimized) WindowState = FormWindowState.Normal;
            Activate();
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    if (data == null) WebModalRouter.Open(_web, _settings, page);
                    else WebModalRouter.Open(_web, page, data);
                }
            }));
        });
    }

    /// <summary>打开设置窗口；保存后把设置应用到宿主。</summary>
    private void ShowSettingsForm()
    {
        using var dlg = new SettingsForm(_settings);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            dlg.Apply();
            ApplyAgentQuestionHandling();
            // 同步连接列表（复用运行中实例，新增/删除的服务器生效）
            _connections.SyncFrom(_settings); _interactions.RefreshConnections(_connections.Connections);
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
            var check = await DshUpdater.CheckForUpdateAsync();
            if (check.IsUpdateAvailable)
            {
                _pendingUpdate = check.LatestVersion;
                Diag.Log($"dsh update available: {check.InstalledVersion} -> {check.LatestVersion}");
                _tray.ShowBalloonTip(6000, "DeepSeek Harness",
                    $"dsh 有新版本 {check.LatestVersion}（当前 {check.InstalledVersion}）。右键托盘菜单「更新 dsh」即可升级。",
                    ToolTipIcon.Info);
            }
            else if (check.Error != null)
            {
                Diag.Log("dsh update check failed: " + check.Error);
            }
        }
        catch (Exception ex)
        {
            Diag.Log("dsh update check exception: " + ex.Message);
        }
    }

    /// <summary>检查并更新 dsh（npm install -g @deepseek-ai/dsh@latest），完成后重启宿主。</summary>
    private async Task UpdateDshAsync()
    {
        DshUpdater.UpdateCheckResult check;
        try
        {
            check = await DshUpdater.CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "dsh 更新检查失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (check.Error != null)
        {
            MessageBox.Show(this, check.Error, "dsh 更新检查失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!check.IsUpdateAvailable)
        {
            MessageBox.Show(this, $"dsh 已是最新版本（{check.InstalledVersion}）",
                "dsh 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var res = MessageBox.Show(this,
            $"当前 dsh {check.InstalledVersion}，最新 {check.LatestVersion}。是否现在更新？",
            "dsh 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (res != DialogResult.Yes) return;

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

    internal bool HandleBrowserInteraction(IDshConnection connection, string raw, Func<string, string, DshInteractionDecision, Task> reply)
    {
        if (!BrowserDshInteractionBridge.TryParse(raw, out var browser)) return false;
        var sourceKey = ConnectionManager.IdOf(connection);
        if (browser.Type == "cancel") { _interactions.CancelExternal(sourceKey, browser.EventId); return true; }
        if (browser.Kind == null) return true;
        if (browser.Kind == DshInteractionKind.Question && !_settings.HandleAgentQuestions) return false;
        var interaction = new DshPendingInteraction(sourceKey, connection.DisplayName, browser.EventId, browser.ClientId, browser.AgentId, browser.Kind.Value, browser.ToolName, browser.Reason, browser.Questions, new BrowserDshInteractionResponder(reply));
        _interactions.PublishExternal(interaction);
        return true;
    }

    internal bool HandleAgentQuestions => _settings.HandleAgentQuestions;

    private void ApplyAgentQuestionHandling()
    {
        var script = WebShell.BrowserInteractionConfigScript(_settings.HandleAgentQuestions);
        if (_web.CoreWebView2 != null) _ = _web.CoreWebView2.ExecuteScriptAsync(script);
        foreach (var window in _remoteWindows.Where(window => !window.IsDisposed)) window.ApplyAgentQuestionHandling(_settings.HandleAgentQuestions);
    }

    private async Task ReplyMainBrowserInteractionAsync(string eventId, string clientId, DshInteractionDecision decision)
    {
        var web = _web.CoreWebView2 ?? throw new InvalidOperationException("dsh WebView 不可用");
        var pending = _browserInteractionReplies.Begin();
        try
        {
            var outcome = DshInteractionOutcome.Build(decision);
            var call = $"if(!window.__dshLauncherResolveRemoteEvent)throw new Error('dsh 交互桥接未就绪');window.__dshLauncherResolveRemoteEvent({JsonSerializer.Serialize(eventId)},{JsonSerializer.Serialize(clientId)},{JsonSerializer.Serialize(outcome)},{JsonSerializer.Serialize(pending.RequestId)});";
            await web.ExecuteScriptAsync(call);
            await pending.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { _browserInteractionReplies.Cancel(pending.RequestId); }
    }

    private void ShowInteractionOverlay(DshPendingInteraction interaction)
    {
        if (_quitting) return;
        if (_interactionOverlay is { IsDisposed: false }) return;

        var overlay = new DshInteractionWebOverlayForm(interaction);
        _interactionOverlay = overlay;
        overlay.DecisionSelected += decision => SubmitInteractionDecisionAsync(interaction, decision);
        overlay.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_interactionOverlay, overlay)) _interactionOverlay = null;
            overlay.Dispose();
        };
        overlay.Show();
        overlay.BringToFront();
        _notifications.Show(
            interaction.Kind == DshInteractionKind.Approval ? "需要确认 Agent 操作" : "Agent 正在等待回答",
            interaction.Kind == DshInteractionKind.Approval
                ? $"{interaction.SourceName}：{interaction.ToolName ?? "未命名工具"}"
                : $"{interaction.SourceName}：{interaction.Questions.Count} 个问题待回答",
            () => SafeUi(() => { if (!overlay.IsDisposed) { overlay.Show(); overlay.BringToFront(); } }),
            requireInteraction: true);
    }

    private async Task SubmitInteractionDecisionAsync(DshPendingInteraction interaction, DshInteractionDecision decision)
    {
        try { await _interactions.SubmitAsync(interaction, decision); }
        catch (Exception ex)
        {
            SafeUi(() => _notifications.Show("未能提交 Agent 回答", ex.Message, ShowPendingInteraction, requireInteraction: true));
            throw;
        }
    }

    private void ShowPendingInteraction()
    {
        if (_interactionOverlay is { IsDisposed: false } overlay)
        {
            overlay.Show();
            overlay.BringToFront();
            return;
        }
        if (_interactions.PendingCount > 0)
            _notifications.Show("Agent 请求排队中", $"还有 {_interactions.PendingCount} 个请求等待显示。", null, requireInteraction: true);
    }

    private void UpdatePendingInteractionMenu(int count)
    {
        if (_pendingInteractionsMenu == null) return;
        _pendingInteractionsMenu.Text = $"待处理确认 ({count})";
        _pendingInteractionsMenu.Enabled = count > 0;
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
            try { _interactions.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            Application.Exit();
        }
        else
        {
            Hide();
            // 最小化到托盘保持静默，不发送系统通知。
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
        try { await _interactions.DisposeAsync(); } catch { }
        Application.Exit();
    }

    private string GetHostFailureMessage(Exception ex) => FormatConnectionFailure(_host, ex);

    internal static string FormatConnectionFailure(IDshConnection connection, Exception ex)
    {
        var detail = (connection as HostSupervisor)?.LastFailureDetails;
        var message = string.IsNullOrWhiteSpace(detail) ? ex.Message : detail;
        if (string.IsNullOrWhiteSpace(detail))
        {
            var tail = ReadLogTail(connection.LogFile, 80);
            if (!string.IsNullOrWhiteSpace(tail)) message += "\n\n最近启动日志：\n" + tail;
        }
        if (!message.Contains(connection.LogFile, StringComparison.OrdinalIgnoreCase))
            message += "\n\n完整日志：" + connection.LogFile;
        return message;
    }

    private static string ReadLogTail(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var lines = File.ReadAllLines(path);
            return string.Join(Environment.NewLine, lines.TakeLast(maxLines));
        }
        catch { return ""; }
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}