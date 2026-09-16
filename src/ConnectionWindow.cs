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
    private readonly BrowserDshInteractionReplyTracker _browserInteractionReplies = new();
    private readonly Panel _loadingOverlay = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 20, 24), Visible = true };
    private readonly LoadingSpinner _spinner = new() { Size = new Size(56, 56) };
    private readonly Label _loadingText = new()
    {
        AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 11f), ForeColor = Color.FromArgb(0x9E, 0x9E, 0x9E),
    };
    private bool _quitting;
    private bool _syncing;
    private int _aboutRequestId;

    public IDshConnection Connection => _conn;

    public ConnectionWindow(IDshConnection conn, MainForm main)
    {
        _conn = conn;
        _main = main;
        Text = conn.DisplayName;
        FormBorderStyle = FormBorderStyle.None;
        MinimizeBox = true;
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
            MessageBox.Show(this, MainForm.FormatConnectionFailure(_conn, ex), "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ShowAboutAsync(bool checkUpdates = false)
    {
        var requestId = Interlocked.Increment(ref _aboutRequestId);
        _main.ShowAboutFromChild();

        string? dshVersion = null;
        LauncherUpdater.UpdateCheckResult? launcherUpdate = null;
        DshUpdater.UpdateCheckResult? dshUpdate = null;
        if (checkUpdates)
        {
            // 远程 dsh 的当前版本由 SSH 探测，最新版本仍从 npm registry 获取。
            var launcherTask = LauncherUpdater.CheckForUpdateAsync();
            var remoteVersionTask = Task.Run(async () => await _conn.GetInstalledVersionAsync());
            var latestDshTask = DshUpdater.GetLatestVersionAsync();
            try { launcherUpdate = await launcherTask; }
            catch (Exception ex) { Diag.Log("检查 DshLauncher 更新失败: " + ex.Message); }
            try { dshVersion = await remoteVersionTask; }
            catch (Exception ex) { Diag.Log($"读取 {_conn.DisplayName} 的 dsh 版本失败: {ex.Message}"); }
            string? latestDsh = null;
            try { latestDsh = await latestDshTask; } catch (Exception ex) { Diag.Log("检查远程 dsh 更新失败: " + ex.Message); }
            dshUpdate = DshUpdater.CreateCheckResult(dshVersion, latestDsh);
        }
        else
        {
            try
            {
                dshVersion = await Task.Run(async () => await _conn.GetInstalledVersionAsync());
            }
            catch (Exception ex)
            {
                Diag.Log($"读取 {_conn.DisplayName} 的 dsh 版本失败: {ex.Message}");
            }
        }

        if (IsDisposed || _quitting || requestId != Volatile.Read(ref _aboutRequestId)) return;
        _main.ShowAboutFromChild();
    }


    private async Task UpdateDshFromAboutAsync()
    {
        string? installed = null;
        string? latest = null;
        try
        {
            installed = await Task.Run(async () => await _conn.GetInstalledVersionAsync());
            latest = await DshUpdater.GetLatestVersionAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "dsh 更新检查失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var check = DshUpdater.CreateCheckResult(installed, latest);
        if (check.Error != null)
        {
            MessageBox.Show(this, check.Error, "dsh 更新检查失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!check.IsUpdateAvailable)
        {
            MessageBox.Show(this, $"dsh 已是最新版本（{check.InstalledVersion}）", "dsh 更新",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"当前 dsh {check.InstalledVersion}，最新 {check.LatestVersion}。是否现在更新？",
            "dsh 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        ShowLoading("正在更新远端 dsh…");
        _conn.AppendLog(">>> npm install -g @deepseek-ai/dsh@latest");
        try
        {
            var code = await _conn.UpdateDshAsync(line => _conn.AppendLog(line));
            if (code != 0)
            {
                HideLoading();
                MessageBox.Show(this, $"更新失败（exit {code}），详见日志。", "dsh 更新失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _conn.AppendLog("dsh 更新成功，正在重启远端 dsh…");
            await _conn.RestartAsync();
            HideLoading();
            MessageBox.Show(this, "dsh 更新成功，远端实例已重启。", "更新完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _ = ShowAboutAsync(checkUpdates: true);
        }
        catch (Exception ex)
        {
            HideLoading();
            MessageBox.Show(this, MainForm.FormatConnectionFailure(_conn, ex), "dsh 更新失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        await cwv.AddScriptToExecuteOnDocumentCreatedAsync(WebShell.BrowserInteractionScript);
        await cwv.AddScriptToExecuteOnDocumentCreatedAsync(WebShell.BrowserInteractionConfigScript(_main.HandleAgentQuestions));
        await cwv.AddScriptToExecuteOnDocumentCreatedAsync(WebShell.FilePickerInterceptorScript);
        await cwv.AddScriptToExecuteOnDocumentCreatedAsync(WebShell.FilePickerConfigScript(_main.InterceptNativeFilePicker));
        await WebModalRouter.Install(_web);
        WebView2PermissionPolicy.Attach(cwv);
        cwv.WebMessageReceived += (_, e) =>
        {
            var raw = e.TryGetWebMessageAsString();
            if (_browserInteractionReplies.TryComplete(raw)) return;
            if (_main.HandleBrowserInteraction(_conn, raw, ReplyBrowserInteractionAsync)) return;
            if (TryHandleRemotePicker(raw)) return;
            if (BrowserNotificationBridge.TryParse(raw, out var notice))
            {
                _main.ShowSystemNotification(
                    string.IsNullOrWhiteSpace(notice.Title) ? "DeepSeek Harness" : $"DeepSeek Harness · {notice.Title}",
                    notice.Body,
                    () => SafeUi(() => { Show(); Activate(); }),
                    notice.RequireInteraction);
                return;
            }
            if (WebModalRouter.TryHandle(raw, (action, payload) =>
            {
                // SSH 窗口共享本机 Launcher 的设置；不允许在远端 WebView 中产生一份空白或脱节的设置副本。
                if (action == "settings.save") { _main.ShowSettingsFromChild(); return; }
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
                if (action == "logs.open") { OpenRemoteLogs(); return; }
                if (action == "plugins.open") { _main.ShowPluginsFromChild(); return; }
                if (action == "manager.open") { _ = _main.ShowManagerFromChildAsync(); return; }
                if (action == "launcher.checkUpdate") { _ = ShowAboutAsync(checkUpdates: true); return; }
                 if (action == "dsh.update") { _ = UpdateDshFromAboutAsync(); return; }
            })) return;
            WebShellBridge.TryHandleWindowCommand(this, raw, action =>
            {
                switch (action)
                {
                    // 设置和 Manager 是 Launcher 全局功能，统一由本地主窗口展示和执行。
                    case "settings": _main.ShowSettingsFromChild(); break;
                    case "manager": _ = _main.ShowManagerFromChildAsync(); break;
                    // 日志是 SSH 会话专属功能，仍在当前窗口显示对应连接的日志。
                    case "logs": OpenRemoteLogs(); break;
                    case "plugins": _main.ShowPluginsFromChild(); break;
                    case "ssh":
                        WebModalRouter.Open(_web, "ssh", new { page = "ssh", ssh = _main.SshConnectionSnapshot() });
                        break;
                    case "restart": _ = RestartAsync(); break;
                    case "about": _ = ShowAboutAsync(); break;
                }
            });
        };
        _web.DefaultBackgroundColor = Color.FromArgb(18, 20, 24);
        cwv.NavigationStarting += OnNavigationStarting;
        cwv.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            _main.OpenExternalLink(e.Uri);
        };
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

        // Shared picker interception watches the authenticated directoryPicker/pick RPC.
        // The browser request remains pending until the standalone SSH picker resolves or cancels it.

        cwv.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) SafeUi(HideLoading);
            // Verify the shared, gated picker interceptor without logging page traffic.
            try
            {
                _ = cwv.ExecuteScriptAsync("window.__dshLauncherPickerInstalled === true ? 'installed' : 'missing'")
                    .ContinueWith(t => _conn.AppendLog($"[SSH页面] shared picker: {t.Result ?? "err"}"));
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

    private bool TryHandleRemotePicker(string raw)
    {
        string? requestId = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "launcher-picker") return false;
            requestId = root.TryGetProperty("requestId", out var id) ? id.GetString() : null;
            if (string.IsNullOrWhiteSpace(requestId)) return true;
            if (!_main.InterceptNativeFilePicker)
            {
                ResolvePickerResult(requestId, null);
                return true;
            }
            OpenRemotePicker(requestId);
            return true;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(requestId)) ResolvePickerResult(requestId, null);
            return true;
        }
    }

    private void OpenRemotePicker(string? requestId, Action<string>? onSelected = null)
    {
        if (_conn is not SshConnection ssh)
        {
            if (!string.IsNullOrWhiteSpace(requestId)) ResolvePickerResult(requestId, null);
            return;
        }
        try
        {
            var picker = new WorkspacePickerWindow($"DshLauncher 选择远端文件夹 · {_conn.DisplayName}", ssh.GetRemoteHomeDirectory(), true,
                path => ssh.ListRemoteEntries(path).Select(item => new WorkspacePickerWindow.Entry(item.Path, item.IsDirectory)).ToList());
            var settled = false;
            picker.PathConfirmed += path =>
            {
                if (settled) return;
                settled = true;
                if (!string.IsNullOrWhiteSpace(requestId)) ResolvePickerResult(requestId, path);
                else onSelected?.Invoke(path);
            };
            picker.FormClosed += (_, _) =>
            {
                if (!settled && !string.IsNullOrWhiteSpace(requestId)) ResolvePickerResult(requestId, null);
            };
            picker.Show(this);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(requestId)) ResolvePickerResult(requestId, null);
        }
    }

    private void ResolvePickerResult(string requestId, string? path)
    {
        if (_web.CoreWebView2 == null) return;
        var script = "if(typeof window.__dshLauncherResolvePicker==='function')window.__dshLauncherResolvePicker(" +
            JsonSerializer.Serialize(requestId) + "," + JsonSerializer.Serialize(path) + ");";
        _ = _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    internal void ApplyAgentQuestionHandling(bool enabled)
    {
        if (_web.CoreWebView2 != null) _ = _web.CoreWebView2.ExecuteScriptAsync(WebShell.BrowserInteractionConfigScript(enabled));
    }

    internal void ApplyFilePickerInterception(bool enabled)
    {
        if (_web.CoreWebView2 != null) _ = _web.CoreWebView2.ExecuteScriptAsync(WebShell.FilePickerConfigScript(enabled));
    }

    private async Task ReplyBrowserInteractionAsync(string eventId, string clientId, DshInteractionDecision decision)
    {
        var web = _web.CoreWebView2 ?? throw new InvalidOperationException("远程 dsh WebView 不可用");
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

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is "http" or "https") return;
        e.Cancel = true;
        _main.OpenExternalLink(e.Uri);
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

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000; // WS_MINIMIZEBOX
            cp.Style |= 0x00080000; // WS_SYSMENU
            cp.ExStyle |= 0x00040000; // WS_EX_APPWINDOW
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0112 && (m.WParam.ToInt64() & 0xFFF0L) == 0xF020L)
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

    private bool HandleShortcut(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Shift | Keys.R: _ = RestartAsync(); return true;
            case Keys.Control | Keys.Shift | Keys.L: OpenRemoteLogs(); return true;
            case Keys.Control | Keys.Shift | Keys.P: _main.ShowPluginsFromChild(); return true;
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

    /// <summary>打开独立的 POSIX 远端目录选择器。</summary>
    private void OpenRemoteFolder()
    {
        OpenRemotePicker(null, path => _ = AddFolderAsync(path));
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

    private void OpenRemoteLogs() => _main.ShowLogsFromChild();

    private string ReadHistory()
    {
        try { return File.Exists(_conn.LogFile) ? File.ReadAllText(_conn.LogFile) : ""; }
        catch (Exception ex) { Diag.Log($"读取 SSH 日志失败: {ex.Message}"); return ""; }
    }


    private async Task AddFolderAsync(string path)
    {
        if (_conn is not SshConnection sc) return;
        path = path?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(path)) return;

        ShowLoading($"正在添加工作区 {path} …");
        try
        {
            var (ok, error) = await sc.CreateWorkspaceRpcAsync(path, line => SafeUi(() => _loadingText.Text = line));
            if (!ok) throw new InvalidOperationException("工作区创建失败：" + (error ?? "dsh 未接受工作区创建请求"));
            // workspace/create updates the dsh workspace feed; do not restart or reload the SSH session.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "添加工作区失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            HideLoading();
        }
    }
}
