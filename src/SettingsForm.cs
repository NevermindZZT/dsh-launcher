using System.Drawing;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

/// <summary>
/// 独立设置窗口：窗口仅承载 WebView2，全部 Launcher 设置 UI 由 HTML/CSS/JavaScript 渲染，
/// 不嵌入或覆盖 dsh 的 Web UI。
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private SettingsDraft? _draft;
    private bool _closing;
    private ThemeHelper.Palette _palette = ThemeHelper.CurrentPagePalette;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        Text = "DshLauncher 设置";
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        MaximumSize = new Size(Math.Max(820, area.Width - 32), Math.Max(640, area.Height - 32));
        Width = Math.Min(1500, MaximumSize.Width);
        Height = Math.Min(1040, MaximumSize.Height);
        MinimumSize = new Size(1100, 780);
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = true;
        LauncherIconTheme.Attach(this);
        // WebView2 displays this color before its first document paints. Keep it aligned with dsh's dark surface.
        BackColor = _palette.WindowBack;
        _web.BackColor = BackColor;
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);
        FormClosing += (_, _) => _closing = true;
        FormClosed += (_, _) => ThemeHelper.PagePaletteChanged -= OnPagePaletteChanged;
        Shown += async (_, _) => { CenterInWorkArea(); await InitializeAsync(); };
    }

    private void CenterInWorkArea()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeHelper.PagePaletteChanged += OnPagePaletteChanged;
        ApplyWindowPalette(_palette);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshLauncher", "WebView2", "settings");
            var environment = await CoreWebView2Environment.CreateAsync(null, directory);
            if (_closing || IsDisposed) return;
            await _web.EnsureCoreWebView2Async(environment);
            if (_closing || IsDisposed) return;
            var core = _web.CoreWebView2 ?? throw new InvalidOperationException("WebView2 初始化失败");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.WebMessageReceived += (_, message) => HandleWebMessage(message.TryGetWebMessageAsString());
            core.NavigationCompleted += (_, _) => ApplyWebPalette();
            core.NavigateToString(BuildDocument());
        }
        catch (Exception ex)
        {
            if (_closing || IsDisposed) return;
            MessageBox.Show(this, "无法显示设置窗口：" + ex.Message, "初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
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

    /// <summary>由 MainForm 在 DialogResult.OK 后调用，保持既有的保存和应用顺序。</summary>
    public void Apply()
    {
        if (_draft == null) return;
        var oldManagerUrl = _settings.Manager.ServerUrl;
        _settings.AttachPort = _draft.AttachPort;
        _settings.WorkingDirectory = string.IsNullOrWhiteSpace(_draft.WorkingDirectory) ? null : _draft.WorkingDirectory.Trim();
        _settings.CloseExits = _draft.CloseExits;
        _settings.AutoStart = _draft.AutoStart;
        _settings.OpenLinksInWebView = _draft.OpenLinksInWebView;
        _settings.HandleAgentQuestions = _draft.HandleAgentQuestions;
        _settings.InterceptNativeFilePicker = _draft.InterceptNativeFilePicker;
        _settings.Manager.Enabled = _draft.Manager.Enabled;
        _settings.Manager.ServerUrl = _draft.Manager.ServerUrl?.Trim() ?? "";
        _settings.Manager.AgentName = _draft.Manager.AgentName?.Trim() ?? "";
        _settings.Manager.PairingCode = _draft.Manager.PairingCode?.Trim() ?? "";
        if (!string.Equals(oldManagerUrl, _settings.Manager.ServerUrl, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Manager.AgentId = "";
            _settings.Manager.AgentToken = "";
        }
        _settings.Save();
        _settings.ApplyAutoStart();
    }

    private void HandleWebMessage(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!string.Equals(TextOf(root, "type"), "settings", StringComparison.Ordinal)) return;
            var action = TextOf(root, "action");
            var payload = root.TryGetProperty("payload", out var value) ? value : default;
            switch (action)
            {
                case "cancel":
                    Close();
                    break;
                case "save":
                    SaveDraft(payload);
                    break;
                case "browse-directory":
                    BrowseDirectory();
                    break;
                case "browse-key":
                    BrowseKey();
                    break;
                case "ssh-save":
                    SaveSsh(payload);
                    break;
                case "ssh-delete":
                    DeleteSsh(payload);
                    break;
                case "ssh-test":
                    _ = TestSshAsync(payload);
                    break;
                case "ssh-generate-key":
                    GenerateSshKey(payload);
                    break;
                case "ssh-copy-public-key":
                    CopySshPublicKey(payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            Notify("error", ex.Message);
        }
    }

    private void SaveDraft(JsonElement payload)
    {
        var draft = JsonSerializer.Deserialize<SettingsDraft>(payload.GetRawText(), JsonOptions);
        if (draft == null) { Notify("error", "设置内容无效。"); return; }
        if (draft.AttachPort < 0 || draft.AttachPort > 65535) { Notify("error", "attach 端口必须在 0 到 65535 之间。"); return; }
        _draft = draft;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void BrowseDirectory()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择宿主进程的工作目录" };
        if (dialog.ShowDialog(this) == DialogResult.OK) Execute("window.__settingsSetWorkingDirectory(" + JsonSerializer.Serialize(dialog.SelectedPath) + ");");
    }

    private void BrowseKey()
    {
        using var dialog = new OpenFileDialog { Title = "选择 SSH 私钥", Filter = "私钥文件 (*.pem;*.key;*)|*.pem;*.key;*|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) Execute("window.__settingsSetSshKey(" + JsonSerializer.Serialize(dialog.FileName) + ");");
    }

    private void SaveSsh(JsonElement payload)
    {
        var item = JsonSerializer.Deserialize<SshEditPayload>(payload.GetRawText(), JsonOptions);
        if (item?.Config == null || string.IsNullOrWhiteSpace(item.Config.Host) || string.IsNullOrWhiteSpace(item.Config.User))
        {
            Notify("error", "SSH 主机和用户名不能为空。");
            return;
        }
        var config = new SshConnectionConfig
        {
            Name = string.IsNullOrWhiteSpace(item.Config.Name) ? $"{item.Config.User.Trim()}@{item.Config.Host.Trim()}" : item.Config.Name.Trim(),
            Host = item.Config.Host.Trim(),
            User = item.Config.User.Trim(),
            Port = item.Config.Port > 0 ? item.Config.Port : 22,
            LocalPort = Math.Max(0, item.Config.LocalPort),
            RemotePort = Math.Max(0, item.Config.RemotePort),
            KeyPath = item.Config.KeyPath?.Trim() ?? "",
            Password = item.Config.AuthMethod == "password" ? item.Config.Password : null,
            AuthMethod = item.Config.AuthMethod == "password" ? "password" : "key",
            RemoteNode = item.Config.RemoteNode?.Trim() ?? "",
            RemoteDshBin = item.Config.RemoteDshBin?.Trim() ?? "",
            StopRemoteOnClose = item.Config.StopRemoteOnClose,
            AutoConnect = item.Config.AutoConnect,
        };
        if (item.Index >= 0 && item.Index < _settings.SshConnections.Count) _settings.SshConnections[item.Index] = config;
        else _settings.SshConnections.Add(config);
        _settings.Save(); // SSH 管理保持既有的立即持久化语义。
        RefreshSsh(config.Name, item.Index >= 0 ? "已更新 SSH 连接。" : "已添加 SSH 连接。");
    }

    private void DeleteSsh(JsonElement payload)
    {
        var index = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("index", out var item) && item.TryGetInt32(out var value) ? value : -1;
        if (index < 0 || index >= _settings.SshConnections.Count) return;
        _settings.SshConnections.RemoveAt(index);
        _settings.Save();
        RefreshSsh(null, "已删除 SSH 连接。");
    }

    private void GenerateSshKey(JsonElement payload)
    {
        var requestedPath = TextOf(payload, "path");
        var overwrite = payload.TryGetProperty("overwrite", out var overwriteValue) && overwriteValue.ValueKind is JsonValueKind.True;
        var keyPath = string.IsNullOrWhiteSpace(requestedPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519")
            : requestedPath.Trim();
        if (File.Exists(keyPath) && !overwrite)
        {
            Notify("confirm-key-overwrite", keyPath);
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            var start = new System.Diagnostics.ProcessStartInfo("ssh-keygen") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-t"); start.ArgumentList.Add("ed25519"); start.ArgumentList.Add("-f"); start.ArgumentList.Add(keyPath); start.ArgumentList.Add("-N"); start.ArgumentList.Add(""); start.ArgumentList.Add("-q");
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("无法启动 ssh-keygen");
            process.WaitForExit(30000);
            if (process.ExitCode != 0 || !File.Exists(keyPath)) throw new InvalidOperationException("密钥生成失败，请检查 ssh-keygen 是否可用。");
            Execute("window.__settingsSetSshKey(" + JsonSerializer.Serialize(keyPath) + ");");
            Notify("info", "密钥已生成；请将同名 .pub 文件添加到服务器的 authorized_keys。");
        }
        catch (Exception ex) { Notify("error", ex.Message); }
    }

    private void CopySshPublicKey(JsonElement payload)
    {
        var keyPath = TextOf(payload, "path")?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(keyPath)) { Notify("error", "请先填写或生成私钥路径。"); return; }
        var publicPath = keyPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ? keyPath : keyPath + ".pub";
        try
        {
            if (!File.Exists(publicPath)) throw new FileNotFoundException("未找到公钥文件，请先生成密钥。", publicPath);
            Clipboard.SetText(File.ReadAllText(publicPath).Trim());
            Notify("info", "公钥已复制到剪贴板。");
        }
        catch (Exception ex) { Notify("error", ex.Message); }
    }

    private async Task TestSshAsync(JsonElement payload)
    {
        var config = JsonSerializer.Deserialize<SshConnectionConfig>(payload.GetRawText(), JsonOptions);
        if (config == null || string.IsNullOrWhiteSpace(config.Host) || string.IsNullOrWhiteSpace(config.User))
        {
            Notify("error", "请先填写 SSH 主机和用户名。");
            return;
        }
        try
        {
            var result = await new SshConnection(config).TestConnectionAsync();
            Execute("window.__settingsSshTestResult(" + JsonSerializer.Serialize(result) + ");");
        }
        catch (Exception ex)
        {
            Execute("window.__settingsSshTestResult(" + JsonSerializer.Serialize("测试失败：" + ex.Message) + ");");
        }
    }

    private void RefreshSsh(string? selectedName, string notice)
    {
        var payload = JsonSerializer.Serialize(new { connections = _settings.SshConnections, selectedName, notice }, JsonOptions);
        Execute("window.__settingsRefreshSsh(" + JsonSerializer.Serialize(payload) + ");");
    }

    private void Notify(string kind, string text) => Execute("window.__settingsNotice(" + JsonSerializer.Serialize(kind) + "," + JsonSerializer.Serialize(text) + ");");
    private void Execute(string script) { if (_web.CoreWebView2 != null) _ = _web.CoreWebView2.ExecuteScriptAsync(script); }
    private static string? TextOf(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private string BuildDocument()
    {
        var data = new
        {
            attachPort = _settings.AttachPort,
            workingDirectory = _settings.WorkingDirectory ?? "",
            closeExits = _settings.CloseExits,
            autoStart = _settings.AutoStart,
            openLinksInWebView = _settings.OpenLinksInWebView,
            handleAgentQuestions = _settings.HandleAgentQuestions,
            interceptNativeFilePicker = _settings.InterceptNativeFilePicker,
            dshHome = Environment.GetEnvironmentVariable("DSH_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"),
            manager = new { _settings.Manager.Enabled, _settings.Manager.ServerUrl, _settings.Manager.AgentName, _settings.Manager.PairingCode, status = string.IsNullOrWhiteSpace(_settings.Manager.AgentId) ? "尚未配对" : "已配对：" + _settings.Manager.AgentId },
            connections = _settings.SshConnections,
            sshHosts = SshConfigParser.ParseHosts(),
            version = VersionHelper.Current,
        };
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, JsonOptions)));
        return Html.Replace("__PAYLOAD__", encoded, StringComparison.Ordinal);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private sealed class SettingsDraft
    {
        public int AttachPort { get; set; }
        public string? WorkingDirectory { get; set; }
        public bool CloseExits { get; set; }
        public bool AutoStart { get; set; }
        public bool OpenLinksInWebView { get; set; }
        public bool HandleAgentQuestions { get; set; }
        public bool InterceptNativeFilePicker { get; set; }
        public ManagerDraft Manager { get; set; } = new();
    }
    private sealed class ManagerDraft { public bool Enabled { get; set; } public string? ServerUrl { get; set; } public string? AgentName { get; set; } public string? PairingCode { get; set; } }
    private sealed class SshEditPayload { public int Index { get; set; } = -1; public SshConnectionConfig? Config { get; set; } }

    private const string Html = """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><style>
:root{color-scheme:dark;--bg:#202020;--layer:#2b2b2b;--layer2:#313131;--text:#f4f4f4;--muted:#b8b8b8;--line:#454545;--accent:#60cdff;--accentText:#00344d;--danger:#ffb4ab}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}.app{min-height:100vh;display:grid;grid-template-columns:218px minmax(0,1fr)}aside{position:sticky;top:0;height:100vh;padding:28px 14px;background:color-mix(in srgb,var(--layer) 88%,#000);border-right:1px solid var(--line)}.brand{margin:0 10px 28px;color:var(--accent);font-size:12px;font-weight:700;letter-spacing:.12em}.nav{display:grid;gap:4px}.nav a{padding:10px 12px;border-radius:8px;color:var(--muted);text-decoration:none}.nav a:hover{background:var(--layer2);color:var(--text)}main{min-width:0}.header{padding:30px 38px 20px;border-bottom:1px solid var(--line)}h1{font-size:28px;line-height:1.2;margin:0 0 8px}.subtitle{margin:0;color:var(--muted)}.content{max-width:900px;padding:24px 38px 110px}.card{scroll-margin-top:16px;margin:0 0 16px;padding:20px;border:1px solid var(--line);border-radius:12px;background:linear-gradient(135deg,color-mix(in srgb,var(--layer) 94%,#fff 2%),var(--layer))}.card h2{margin:0;font-size:18px}.card>p{margin:4px 0 18px;color:var(--muted)}.row{display:grid;grid-template-columns:190px minmax(0,1fr);gap:16px;padding:12px 0;border-top:1px solid color-mix(in srgb,var(--line) 70%,transparent)}.row:first-of-type{border-top:0}.label{padding-top:9px;font-weight:600}.hint{margin:6px 0 0;color:var(--muted);font-size:12px}.inline{display:flex;flex-wrap:wrap;gap:8px;align-items:center}.stack{display:grid;gap:8px}input,select{width:100%;height:36px;padding:0 10px;border:1px solid var(--line);border-radius:7px;background:#1f1f1f;color:var(--text);font:inherit}input:focus,select:focus{outline:2px solid color-mix(in srgb,var(--accent) 60%,transparent);border-color:var(--accent)}input[type=checkbox],input[type=radio]{width:18px;height:18px;accent-color:var(--accent)}.check{display:flex;gap:9px;align-items:center;min-height:28px}.button{border:1px solid var(--line);border-radius:7px;background:var(--layer2);color:var(--text);min-height:36px;padding:0 13px;font:inherit;cursor:pointer}.button:hover{border-color:var(--accent);background:#3a3a3a}.button.primary{background:var(--accent);border-color:var(--accent);color:var(--accentText);font-weight:650}.button.danger{color:var(--danger)}.ssh-list{display:grid;gap:8px}.ssh-item{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:12px;border:1px solid var(--line);border-radius:9px;background:color-mix(in srgb,var(--bg) 45%,transparent)}.ssh-name{font-weight:650}.ssh-meta{color:var(--muted);font-size:12px;margin-top:2px}.footer{position:fixed;z-index:2;right:0;bottom:0;left:218px;display:flex;justify-content:flex-end;gap:8px;padding:14px 38px;background:color-mix(in srgb,var(--bg) 92%,transparent);border-top:1px solid var(--line);backdrop-filter:blur(12px)}.notice{position:fixed;right:24px;bottom:78px;z-index:5;max-width:420px;padding:10px 14px;border:1px solid var(--line);border-radius:8px;background:#333;box-shadow:0 8px 30px #0006}.notice.error{border-color:#b75b54;color:var(--danger)}.modal{position:fixed;z-index:10;inset:0;display:grid;place-items:center;padding:18px;background:#0008}.modal[hidden]{display:none}.dialog{width:min(720px,100%);max-height:calc(100vh - 36px);overflow:auto;padding:22px;border:1px solid var(--line);border-radius:12px;background:var(--layer);box-shadow:0 24px 80px #0009}.dialog h2{margin:0 0 16px}.dialog-grid{display:grid;grid-template-columns:1fr 1fr;gap:12px}.field{display:grid;gap:5px}.field.full{grid-column:1/-1}.dialog-actions{display:flex;justify-content:flex-end;gap:8px;margin-top:20px}@media(max-width:700px){.app{display:block}aside{display:none}.header,.content{padding-left:18px;padding-right:18px}.footer{left:0;padding:12px 18px}.row{grid-template-columns:1fr}.label{padding-top:0}.dialog-grid{grid-template-columns:1fr}}
</style></head><body><div class="app"><aside><div class="brand">DSHLAUNCHER</div><nav class="nav"><a href="#general">常规</a><a href="#experience">体验</a><a href="#ssh">SSH 连接</a><a href="#manager">dsh-manager</a><a href="#about">关于</a></nav></aside><main><header class="header"><h1>设置</h1><p class="subtitle">独立窗口中的 Web 设置界面。全局设置在保存后应用；SSH 连接操作会立即保存。</p></header><div class="content" id="content"></div></main></div><footer class="footer"><button class="button" id="cancel">取消</button><button class="button primary" id="save">保存更改</button></footer><div id="notice" class="notice" hidden></div><div id="sshModal" class="modal" hidden><form class="dialog" id="sshForm"><h2 id="sshTitle">新增 SSH 连接</h2><input id="sshIndex" type="hidden" value="-1"><div class="dialog-grid"><label class="field full">导入系统 SSH 配置<div class="inline"><select id="sshImport"></select><button class="button" type="button" id="sshImportApply">导入</button></div></label><label class="field full">名称<input id="sshName"></label><label class="field">主机<input id="sshHost" required></label><label class="field">端口<input id="sshPort" type="number" min="1" value="22"></label><label class="field">用户名<input id="sshUser" required></label><label class="field">认证方式<select id="sshAuth"><option value="key">密钥认证</option><option value="password">密码认证</option></select></label><label class="field full">私钥路径<div class="inline"><input id="sshKey"><button class="button" type="button" id="browseKey">浏览…</button><button class="button" type="button" id="sshGenerateKey">生成密钥</button><button class="button" type="button" id="sshCopyPublicKey">复制公钥</button></div></label><label class="field full">密码<input id="sshPassword" type="password"></label><label class="field">本地转发端口<input id="sshLocalPort" type="number" min="0" value="0"></label><label class="field">远端 dsh 端口<input id="sshRemotePort" type="number" min="0" value="0"></label><label class="field full">远端 Node 路径<input id="sshRemoteNode"></label><label class="field full">远端 dsh 路径<input id="sshRemoteDshBin"></label><label class="field full check"><input id="sshStop" type="checkbox" checked>关闭 Launcher 时停止远端 dsh</label><label class="field full check"><input id="sshAuto" type="checkbox" checked>启动 Launcher 时自动连接</label></div><p class="hint" id="sshTestResult"></p><div class="dialog-actions"><button class="button" type="button" id="sshTest">测试连接</button><button class="button" type="button" id="sshClose">取消</button><button class="button primary" type="submit">保存 SSH 连接</button></div></form></div><script>
const raw=atob('__PAYLOAD__'),data=JSON.parse(new TextDecoder().decode(Uint8Array.from(raw,c=>c.charCodeAt(0)))),content=document.querySelector('#content'),$=s=>document.querySelector(s),send=(action,payload={})=>chrome.webview.postMessage(JSON.stringify({type:'settings',action,payload}));let connections=data.connections||[],noticeTimer;
const esc=v=>String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));const check=(id,v)=>'<label class="check"><input id="'+id+'" type="checkbox" '+(v?'checked':'')+'><span></span></label>';const input=(id,v,type='text')=>'<input id="'+id+'" type="'+type+'" value="'+esc(v)+'">';
function page(){let m=data.manager||{};content.innerHTML='<section class="card" id="general"><h2>常规</h2><p>本机 dsh 的启动和工作区设置。</p><div class="row"><div class="label">attach 端口</div><div>'+input('port',data.attachPort,'number')+'<p class="hint">0 = 仅 spawn 模式，不探测已有实例。</p></div></div><div class="row"><div class="label">工作目录</div><div class="inline" style="align-items:stretch">'+input('wd',data.workingDirectory)+'<button class="button" id="browseDir">浏览…</button></div></div><div class="row"><div class="label">链接打开方式</div><div>'+check('links',data.openLinksInWebView)+'<span>链接在 Launcher 内的新窗口中打开</span><p class="hint">关闭时使用系统默认浏览器打开外部链接。</p></div></div><div class="row"><div class="label">文件选择器</div><div>'+check('picker',data.interceptNativeFilePicker)+'<span>使用 Launcher 的文件选择器拦截 dsh 原生选择器</span><p class="hint">支持本地 Windows 与 SSH Linux 路径；关闭后由 dsh 使用原生选择器。</p></div></div></section><section class="card" id="experience"><h2>启动与交互</h2><p>控制关闭行为、开机启动和 Agent 提问的呈现方式。</p><div class="row"><div class="label">关闭行为</div><div class="stack"><label class="check"><input type="radio" name="close" value="tray" '+(!data.closeExits?'checked':'')+'>隐藏到托盘，dsh 服务保持运行（推荐）</label><label class="check"><input type="radio" name="close" value="exit" '+(data.closeExits?'checked':'')+'>停止服务并退出</label></div></div><div class="row"><div class="label">开机自启</div><div>'+check('auto',data.autoStart)+'<span>开机自动启动</span></div></div><div class="row"><div class="label">Agent 交互浮窗</div><div>'+check('agentQuestions',data.handleAgentQuestions)+'<span>同时显示 Launcher 的 Agent 提问和权限浮窗</span><p class="hint">dsh 原生 Web UI 始终显示；关闭后仅关闭 Launcher 浮窗和通知。</p></div></div></section><section class="card" id="ssh"><h2>SSH 远程连接</h2><p>管理服务器连接；新增、编辑和删除会立即保存。</p><div class="row"><div class="label">DSH_HOME</div><div><code>'+esc(data.dshHome)+'</code></div></div><div class="row"><div class="label">已配置连接</div><div><div class="ssh-list" id="sshList"></div><p><button class="button primary" id="sshAdd">新增 SSH 连接</button></p></div></div></section><section class="card" id="manager"><h2>dsh-manager</h2><p>连接 DshLauncher Agent 到 dsh-manager；更改服务器地址会要求重新配对。</p><div class="row"><div class="label">Agent</div><div>'+check('managerEnabled',m.enabled)+'<span>启用 dsh-manager Agent</span></div></div><div class="row"><div class="label">服务器地址（HTTPS）</div><div>'+input('managerUrl',m.serverUrl)+'</div></div><div class="row"><div class="label">Agent 名称</div><div>'+input('managerName',m.agentName)+'</div></div><div class="row"><div class="label">首次配对码</div><div>'+input('managerPairing',m.pairingCode,'password')+'<p class="hint">只在首次注册时使用；已配对后可留空。</p></div></div><div class="row"><div class="label">连接状态</div><div>'+esc(m.status||'')+'</div></div></section><section class="card" id="about"><h2>关于</h2><p>DshLauncher 版本信息。</p><div class="row"><div class="label">版本</div><div>'+esc(data.version)+'</div></div></section>';renderSsh();bind();}
function renderSsh(){let list=$('#sshList');list.innerHTML=connections.length?connections.map((c,i)=>'<article class="ssh-item"><div><div class="ssh-name">'+esc(c.name||c.user+'@'+c.host)+'</div><div class="ssh-meta">'+esc(c.user)+'@'+esc(c.host)+':'+esc(c.port)+' · '+(c.authMethod==='password'?'密码认证':'密钥认证')+'</div></div><div class="inline"><button class="button" data-edit="'+i+'">编辑</button><button class="button" data-test="'+i+'">测试</button><button class="button danger" data-delete="'+i+'">删除</button></div></article>').join(''):'<p class="hint">尚未配置 SSH 连接。</p>';}
function bind(){$('#cancel').onclick=()=>send('cancel');$('#save').onclick=save;$('#browseDir').onclick=()=>send('browse-directory');$('#sshAdd').onclick=()=>openSsh(-1);$('#sshClose').onclick=closeSsh;$('#browseKey').onclick=()=>send('browse-key');$('#sshGenerateKey').onclick=()=>send('ssh-generate-key',{path:$('#sshKey').value});$('#sshCopyPublicKey').onclick=()=>send('ssh-copy-public-key',{path:$('#sshKey').value});$('#sshImportApply').onclick=importSshHost;$('#sshTest').onclick=()=>send('ssh-test',sshConfig());$('#sshForm').onsubmit=e=>{e.preventDefault();send('ssh-save',{index:+$('#sshIndex').value,config:sshConfig()})};content.onclick=e=>{let b=e.target.closest('[data-edit],[data-delete],[data-test]');if(!b)return;let i=+(b.dataset.edit??b.dataset.delete??b.dataset.test);if(b.dataset.edit!==undefined)openSsh(i);else if(b.dataset.delete!==undefined){if(confirm('删除 SSH 连接“'+(connections[i].name||connections[i].host)+'”？'))send('ssh-delete',{index:i})}else send('ssh-test',connections[i])};}
function save(){let draft={attachPort:+$('#port').value||0,workingDirectory:$('#wd').value,closeExits:document.querySelector('input[name=close]:checked').value==='exit',autoStart:$('#auto').checked,openLinksInWebView:$('#links').checked,handleAgentQuestions:$('#agentQuestions').checked,interceptNativeFilePicker:$('#picker').checked,manager:{enabled:$('#managerEnabled').checked,serverUrl:$('#managerUrl').value,agentName:$('#managerName').value,pairingCode:$('#managerPairing').value}};send('save',draft)}
function importSshHost(){let host=(data.sshHosts||[])[+$('#sshImport').value];if(!host)return;$('#sshHost').value=host.hostName||host.alias||'';$('#sshUser').value=host.user||$('#sshUser').value;$('#sshPort').value=host.port||22;$('#sshKey').value=host.identityFile||$('#sshKey').value;if(!$('#sshName').value)$('#sshName').value=host.alias||'';$('#sshAuth').value='key'}function openSsh(i){let c=i>=0?connections[i]:{port:22,authMethod:'key',localPort:0,remotePort:0,stopRemoteOnClose:true,autoConnect:true};$('#sshIndex').value=i;$('#sshImport').innerHTML='<option value="">选择系统 SSH 配置…</option>'+(data.sshHosts||[]).map((h,n)=>'<option value="'+n+'">'+esc(h.alias||h.hostName||'SSH')+(h.hostName?' ('+esc(h.hostName)+')':'')+'</option>').join('');$('#sshTitle').textContent=i>=0?'编辑 SSH 连接':'新增 SSH 连接';$('#sshName').value=c.name||'';$('#sshHost').value=c.host||'';$('#sshPort').value=c.port??22;$('#sshUser').value=c.user||'';$('#sshAuth').value=c.authMethod||'key';$('#sshKey').value=c.keyPath||'';$('#sshPassword').value=c.password||'';$('#sshLocalPort').value=c.localPort??0;$('#sshRemotePort').value=c.remotePort??0;$('#sshRemoteNode').value=c.remoteNode||'';$('#sshRemoteDshBin').value=c.remoteDshBin||'';$('#sshStop').checked=c.stopRemoteOnClose!==false;$('#sshAuto').checked=c.autoConnect!==false;$('#sshTestResult').textContent='';$('#sshModal').hidden=false}function closeSsh(){$('#sshModal').hidden=true}function sshConfig(){return{name:$('#sshName').value,host:$('#sshHost').value,port:+$('#sshPort').value||22,user:$('#sshUser').value,authMethod:$('#sshAuth').value,keyPath:$('#sshKey').value,password:$('#sshPassword').value,localPort:+$('#sshLocalPort').value||0,remotePort:+$('#sshRemotePort').value||0,remoteNode:$('#sshRemoteNode').value,remoteDshBin:$('#sshRemoteDshBin').value,stopRemoteOnClose:$('#sshStop').checked,autoConnect:$('#sshAuto').checked}}
window.__settingsSetWorkingDirectory=v=>$('#wd').value=v;window.__settingsSetSshKey=v=>$('#sshKey').value=v;window.__settingsSshTestResult=v=>$('#sshTestResult').textContent=v;window.__settingsRefreshSsh=raw=>{let x=JSON.parse(raw);connections=x.connections||[];renderSsh();if(x.selectedName!==undefined)closeSsh();notice('info',x.notice||'')};function notice(kind,text){if(kind==='confirm-key-overwrite'){if(confirm('密钥已存在：'+text+'\n是否覆盖？'))send('ssh-generate-key',{path:text,overwrite:true});return}let n=$('#notice');n.textContent=text;n.className='notice '+kind;n.hidden=!text;clearTimeout(noticeTimer);noticeTimer=setTimeout(()=>n.hidden=true,4200)}window.__settingsNotice=notice;page();
</script></body></html>
""";
}
