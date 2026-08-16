using System.Drawing;

namespace DshLauncher;

/// <summary>SSH 连接配置编辑窗口（Win11 风格）。保存后返回新的 SshConnectionConfig。</summary>
public sealed class SshEditForm : ThemedForm
{
    private readonly ThemedComboBox _cmbImport = new() { Width = 260, Height = 40 };
    private readonly RoundedButton _btnImport = new() { Text = "导入", Width = 64, Height = 34 };
    private readonly InputBox _name = new(34) { Width = 240 };
    private readonly InputBox _host = new(34) { Width = 200 };
    private readonly InputBox _port = new(34) { Width = 80 };
    private readonly InputBox _user = new(34) { Width = 160 };
    private readonly ThemedRadioButton _rbKey = new() { Text = "密钥认证" };
    private readonly ThemedRadioButton _rbPassword = new() { Text = "密码认证" };
    private readonly InputBox _keyPath = new(34) { Width = 190 };
    private readonly RoundedButton _btnBrowseKey = new() { Text = "浏览…", Width = 64, Height = 34 };
    private readonly RoundedButton _btnGenKey = new() { Text = "生成密钥", Width = 72, Height = 34 };
    private readonly RoundedButton _btnCopyPub = new() { Text = "复制公钥", Width = 72, Height = 34 };
    private readonly InputBox _password = new(34) { Width = 220 };
    private readonly InputBox _localPort = new(34) { Width = 80 };
    private readonly InputBox _remotePort = new(34) { Width = 80 };
    private readonly ThemedCheckBox _chkStopOnClose = new() { Text = "关闭启动器时停止远端 dsh（不勾选 = 保持运行）" };
    private readonly ThemedCheckBox _chkAutoConnect = new() { Text = "启动启动器时自动连接（多连接并行）" };
    private readonly RoundedButton _btnSave = new() { Text = "保存", Width = 96, Height = 36, DialogResult = DialogResult.OK };
    private readonly RoundedButton _btnCancel = new() { Text = "取消", Width = 96, Height = 36, DialogResult = DialogResult.Cancel };

    /// <summary>保存后的配置（DialogResult.OK 时有效）。</summary>
    public SshConnectionConfig? Result { get; private set; }

    public SshEditForm(SshConnectionConfig? existing = null)
    {
        Text = existing == null ? "新增 SSH 连接" : "编辑 SSH 连接";
        Width = 740;
        Height = 760;
        MinimumSize = new Size(680, 680);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(28, 24, 28, 12), AutoSize = false, RowCount = 12
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 12; i++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 从系统 SSH 配置（~/.ssh/config）读取主机供导入
        var sshHosts = SshConfigParser.ParseHosts();
        var labels = new List<string>();
        foreach (var h in sshHosts)
        {
            var label = string.IsNullOrEmpty(h.HostName) ? h.Alias : $"{h.Alias} ({h.HostName})";
            labels.Add(label);
        }
        _cmbImport.SetItems(labels);
        _cmbImport.Tag = sshHosts;
        _btnImport.Click += (_, _) => ImportHost();
        _password.Inner.PasswordChar = '•';
        _btnBrowseKey.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Title = "选择 SSH 私钥", Filter = "私钥文件 (*.pem;*.key;*)|*.pem;*.key;*|所有文件 (*.*)|*.*" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _keyPath.Inner.Text = dlg.FileName;
        };
        _btnGenKey.Click += (_, _) => GenerateKey();
        _btnCopyPub.Click += (_, _) => CopyPublicKey();
        _rbKey.CheckedChanged += (_, _) => UpdateAuthEnabled();
        _rbPassword.CheckedChanged += (_, _) => UpdateAuthEnabled();

        int row = 0;
        panel.Controls.Add(MkLabel("系统 SSH 配置"), 0, row);
        var importWrap = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        importWrap.Controls.Add(_cmbImport);
        importWrap.Controls.Add(_btnImport);
        panel.Controls.Add(importWrap, 1, row);
        row++;
        panel.Controls.Add(MkLabel("名称"), 0, row); panel.Controls.Add(_name, 1, row); row++;
        panel.Controls.Add(MkLabel("主机"), 0, row); panel.Controls.Add(_host, 1, row); row++;
        panel.Controls.Add(MkLabel("端口"), 0, row); panel.Controls.Add(_port, 1, row); row++;
        panel.Controls.Add(MkLabel("用户名"), 0, row); panel.Controls.Add(_user, 1, row); row++;
        panel.Controls.Add(MkLabel("认证方式"), 0, row);
        var authWrap = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        authWrap.Controls.Add(_rbKey); authWrap.Controls.Add(_rbPassword);
        panel.Controls.Add(authWrap, 1, row); row++;
        panel.Controls.Add(MkLabel("私钥路径"), 0, row);
        var keyWrap = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        _keyPath.Margin = new Padding(0, 0, 8, 8);
        _btnBrowseKey.Margin = new Padding(0, 0, 8, 8);
        _btnGenKey.Margin = new Padding(0, 0, 8, 8);
        _btnCopyPub.Margin = new Padding(0, 0, 0, 8);
        keyWrap.Controls.Add(_keyPath); keyWrap.Controls.Add(_btnBrowseKey);
        keyWrap.Controls.Add(_btnGenKey); keyWrap.Controls.Add(_btnCopyPub);
        panel.Controls.Add(keyWrap, 1, row); row++;
        panel.Controls.Add(MkLabel("密码"), 0, row); panel.Controls.Add(_password, 1, row); row++;
        panel.Controls.Add(MkLabel("本地端口"), 0, row);
        var lpWrap = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        lpWrap.Controls.Add(_localPort);
        lpWrap.Controls.Add(MkLabel("0 = 自动分配（推荐）"));
        panel.Controls.Add(lpWrap, 1, row); row++;
        panel.Controls.Add(MkLabel("远端端口"), 0, row);
        var rpWrap = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        rpWrap.Controls.Add(_remotePort);
        rpWrap.Controls.Add(MkLabel("0 = 自动（多用户服务器建议）"));
        panel.Controls.Add(rpWrap, 1, row); row++;
        panel.Controls.Add(MkLabel("生命周期"), 0, row); panel.Controls.Add(_chkStopOnClose, 1, row); row++;
        panel.Controls.Add(MkLabel("自动连接"), 0, row); panel.Controls.Add(_chkAutoConnect, 1, row); row++;

        var btnWrap = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Dock = DockStyle.Bottom, Padding = new Padding(0, 14, 28, 22)
        };
        btnWrap.Controls.Add(_btnSave);
        btnWrap.Controls.Add(_btnCancel);
        Controls.Add(panel);
        Controls.Add(btnWrap);
        AcceptButton = _btnSave;
        CancelButton = _btnCancel;

        // 预填
        if (existing != null)
        {
            _name.Inner.Text = existing.Name;
            _host.Inner.Text = existing.Host;
            _port.Inner.Text = existing.Port.ToString();
            _user.Inner.Text = existing.User;
            _rbKey.Checked = existing.AuthMethod == "key";
            _rbPassword.Checked = existing.AuthMethod == "password";
            _keyPath.Inner.Text = existing.KeyPath;
            _password.Inner.Text = existing.Password ?? "";
            _localPort.Inner.Text = existing.LocalPort.ToString();
            _remotePort.Inner.Text = existing.RemotePort.ToString();
            _chkStopOnClose.Checked = existing.StopRemoteOnClose;
            _chkAutoConnect.Checked = existing.AutoConnect;
        }
        else
        {
            _port.Inner.Text = "22";
            _localPort.Inner.Text = "0";
            _remotePort.Inner.Text = "0";
            _rbKey.Checked = true;
            _chkStopOnClose.Checked = true;
            _chkAutoConnect.Checked = true;
        }
        UpdateAuthEnabled();
    }

    private void UpdateAuthEnabled()
    {
        var key = _rbKey.Checked;
        _keyPath.Enabled = key;
        _btnBrowseKey.Enabled = key;
        _password.Enabled = !key;
    }

    private static Label MkLabel(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 12, 16, 0) };
    /// <summary>生成 SSH 密钥对（ed25519，无密码），并回填私钥路径。</summary>
    private void GenerateKey()
    {
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var keyPath = string.IsNullOrWhiteSpace(_keyPath.Inner.Text)
            ? Path.Combine(sshDir, "id_ed25519")
            : _keyPath.Inner.Text.Trim();
        if (File.Exists(keyPath))
        {
            var overwrite = MessageBox.Show(this, $"密钥已存在：{keyPath}\n是否覆盖？", "生成密钥",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            if (!overwrite) return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            var psi = new System.Diagnostics.ProcessStartInfo("ssh-keygen")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-t"); psi.ArgumentList.Add("ed25519");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(keyPath);
            psi.ArgumentList.Add("-N"); psi.ArgumentList.Add("");
            psi.ArgumentList.Add("-q");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) throw new InvalidOperationException("无法启动 ssh-keygen");
            p.WaitForExit(30000);
            if (p.ExitCode == 0 && File.Exists(keyPath))
            {
                _keyPath.Inner.Text = keyPath;
                MessageBox.Show(this, $"密钥已生成：{keyPath}\n\n请将公钥内容添加到服务器 ~/.ssh/authorized_keys\n（点击「复制公钥」获取公钥）。", "生成密钥",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "密钥生成失败，请检查 ssh-keygen 是否可用", "生成密钥", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "生成密钥失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>复制公钥到剪贴板（供添加到服务器 authorized_keys）。</summary>
    private void CopyPublicKey()
    {
        var keyPath = _keyPath.Inner.Text.Trim();
        if (string.IsNullOrEmpty(keyPath))
        {
            MessageBox.Show(this, "请先填写或生成私钥路径", "复制公钥", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var pubPath = keyPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ? keyPath : keyPath + ".pub";
        if (!File.Exists(pubPath))
        {
            MessageBox.Show(this, $"未找到公钥文件：{pubPath}\n（请先生成密钥）", "复制公钥", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            var pub = File.ReadAllText(pubPath).Trim();
            Clipboard.SetText(pub);
            MessageBox.Show(this, "公钥已复制到剪贴板，请粘贴到服务器 ~/.ssh/authorized_keys", "复制公钥",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "复制公钥失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }



    /// <summary>从系统 SSH 配置（~/.ssh/config）导入选中主机，填充表单。</summary>
    private void ImportHost()
    {
        var hosts = _cmbImport.Tag as List<SshHostEntry>;
        if (hosts == null || hosts.Count == 0 || _cmbImport.SelectedIndex < 0)
        {
            MessageBox.Show(this, "未找到可导入的主机。\n请在 ~/.ssh/config 中配置 Host 条目（或先使用 ssh 连接过目标服务器）。",
                "导入主机", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var h = hosts[_cmbImport.SelectedIndex];
        _host.Inner.Text = !string.IsNullOrEmpty(h.HostName) ? h.HostName : h.Alias;
        if (!string.IsNullOrEmpty(h.User)) _user.Inner.Text = h.User;
        if (h.Port > 0 && h.Port != 22) _port.Inner.Text = h.Port.ToString();
        if (!string.IsNullOrEmpty(h.IdentityFile))
        {
            _keyPath.Inner.Text = h.IdentityFile;
            _rbKey.Checked = true;
        }
        if (string.IsNullOrWhiteSpace(_name.Inner.Text)) _name.Inner.Text = h.Alias;
        MessageBox.Show(this, $"已导入主机「{h.Alias}」的配置。", "导入主机", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    protected override void ApplyPalette(ThemeHelper.Palette p) => ApplyPaletteTree(this, p);

    /// <summary>校验并保存配置（DialogResult.OK 时由调用方调用）。返回是否有效。</summary>
    public bool ApplyAndValidate()
    {
        if (string.IsNullOrWhiteSpace(_host.Inner.Text) || string.IsNullOrWhiteSpace(_user.Inner.Text))
        {
            MessageBox.Show(this, "主机和用户名不能为空", "校验失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        Result = new SshConnectionConfig
        {
            Name = string.IsNullOrWhiteSpace(_name.Inner.Text) ? $"{_user.Inner.Text.Trim()}@{_host.Inner.Text.Trim()}" : _name.Inner.Text.Trim(),
            Host = _host.Inner.Text.Trim(),
            Port = int.TryParse(_port.Inner.Text.Trim(), out var p) && p > 0 ? p : 22,
            User = _user.Inner.Text.Trim(),
            AuthMethod = _rbKey.Checked ? "key" : "password",
            KeyPath = _keyPath.Inner.Text.Trim(),
            Password = _rbPassword.Checked ? _password.Inner.Text : null,
            // 0 = 自动（本地端口自动分配空闲；远端端口自动随机空闲，多用户服务器避免冲突）
            LocalPort = int.TryParse(_localPort.Inner.Text.Trim(), out var lp) && lp >= 0 ? lp : 0,
            RemotePort = int.TryParse(_remotePort.Inner.Text.Trim(), out var rp) && rp >= 0 ? rp : 0,
            StopRemoteOnClose = _chkStopOnClose.Checked,
            AutoConnect = _chkAutoConnect.Checked,
        };
        return true;
    }
}
