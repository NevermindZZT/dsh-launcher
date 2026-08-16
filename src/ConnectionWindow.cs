using System.Drawing;
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
    private readonly WebView2 _web = new();
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

    public ConnectionWindow(IDshConnection conn)
    {
        _conn = conn;
        Text = conn.DisplayName;
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
        _web.DefaultBackgroundColor = Color.FromArgb(18, 20, 24);
        cwv.DocumentTitleChanged += (_, _) =>
        {
            var title = cwv.DocumentTitle;
            if (string.IsNullOrWhiteSpace(title)) return;
            SafeUi(() => { if (Text != title) Text = title; });
        };
        cwv.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) SafeUi(HideLoading);
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
            case Keys.Control | Keys.Shift | Keys.L: ShowLogForm(); return true;
            case Keys.Control | Keys.Shift | Keys.P: ShowPluginsForm(); return true;
            case Keys.Control | Keys.Shift | Keys.C: OpenPicker(); return true;
            case Keys.Control | Keys.Shift | Keys.Y: _ = SyncFromLocalAsync(); return true;
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
        if (Owner is MainForm mf)
        {
            mf.ShowConnectionPicker();
        }
        else
        {
            Activate();
        }
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
                    var url = await _conn.RestartAsync();
                    Navigate(url);
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
        try { var url = await _conn.RestartAsync(); Navigate(url); HideLoading(); }
        catch (Exception ex) { HideLoading(); MessageBox.Show(this, ex.Message, "重启失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ShowLogForm()
    {
        using var f = new LogForm(_conn); f.ShowDialog(this);
    }

    private void ShowPluginsForm()
    {
        using var f = new PluginsForm(_conn, RestartAsync); f.ShowDialog(this);
    }
}
