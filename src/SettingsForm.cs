using System.Drawing;

namespace DshLauncher;

/// <summary>设置窗口：端口策略 / 工作目录 / 关闭行为 / 开机自启 / DSH_HOME 显示。Win11 Fluent 风格（Mica + 大留白 + 圆角控件）。</summary>
public sealed class SettingsForm : ThemedForm
{
    private readonly AppSettings _settings;
    private readonly InputBox _port = new(34) { Width = 120 };
    private readonly InputBox _cwd = new(34);
    private readonly RoundedButton _btnBrowse = new() { Text = "浏览…", Width = 88, Height = 34 };
    private readonly ThemedRadioButton _rbTray = new() { Text = "隐藏到托盘，dsh 服务保持运行（推荐）" };
    private readonly ThemedRadioButton _rbExit = new() { Text = "停止服务并退出" };
    private readonly ThemedCheckBox _chkAutoStart = new() { Text = "开机自动启动" };
    private readonly Label _dshHome = new() { AutoSize = true };
    // 连接模式
    private readonly ThemedRadioButton _rbLocalMode = new() { Text = "本地" };
    private readonly ThemedRadioButton _rbSshMode = new() { Text = "SSH 远程" };
    private readonly ThemedComboBox _cmbSsh = new() { Width = 220, Height = 40 };
    private readonly RoundedButton _btnSshAdd = new() { Text = "新增", Width = 72, Height = 40 };
    private readonly RoundedButton _btnSshEdit = new() { Text = "编辑", Width = 72, Height = 40 };
    private readonly RoundedButton _btnSshDel = new() { Text = "删除", Width = 72, Height = 40 };
    private readonly RoundedButton _btnSshTest = new() { Text = "测试连接", Width = 108, Height = 40 };
    private readonly Label _sshStatus = new() { AutoSize = true, Tag = "muted" };
    private readonly ThemedCheckBox _chkManager = new() { Text = "启用 dsh-manager Agent" };
    private readonly InputBox _managerUrl = new(34);
    private readonly InputBox _managerName = new(34);
    private readonly InputBox _managerPairing = new(34);
    private readonly InputBox _managerFingerprint = new(34);
    private readonly Label _managerStatus = new() { AutoSize = true, Tag = "muted" };
    private readonly RoundedButton _btnSave = new() { Text = "保存", DialogResult = DialogResult.OK, Width = 96, Height = 36 };
    private readonly RoundedButton _btnCancel = new() { Text = "取消", DialogResult = DialogResult.Cancel, Width = 96, Height = 36 };

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        Text = "设置";
        Width = 840;
        Height = 1020;
        MinimumSize = new Size(740, 700);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(44, 40, 44, 20),
            AutoSize = false,
            RowCount = 14,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 14; i++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _port.Inner.Text = settings.AttachPort > 0 ? settings.AttachPort.ToString() : "0";
        _cwd.Inner.Text = settings.WorkingDirectory ?? "";
        _rbTray.Checked = !settings.CloseExits;
        _rbExit.Checked = settings.CloseExits;
        _chkAutoStart.Checked = settings.AutoStart;
        var dshHome = Environment.GetEnvironmentVariable("DSH_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        _dshHome.Text = dshHome;
        _chkManager.Checked = settings.Manager.Enabled;
        _managerUrl.Inner.Text = settings.Manager.ServerUrl;
        _managerName.Inner.Text = settings.Manager.AgentName;
        _managerPairing.Inner.Text = settings.Manager.PairingCode;
        _managerFingerprint.Inner.Text = settings.Manager.ServerCertificateFingerprint;
        _managerStatus.Text = string.IsNullOrWhiteSpace(settings.Manager.AgentId) ? "尚未配对" : "已配对：" + settings.Manager.AgentId;

        // SSH 连接列表（多连接；本地连接始终存在）
        _cmbSsh.SetItems(settings.SshConnections.Select(c => c.Name));
        if (_cmbSsh.ItemCount > 0) _cmbSsh.SelectedIndex = 0;
        _btnSshAdd.Click += (_, _) => AddSshConnection();
        _btnSshEdit.Click += (_, _) => EditSshConnection();
        _btnSshDel.Click += (_, _) => DeleteSshConnection();
        _btnSshTest.Click += (_, _) => _ = TestSshConnectionAsync();
        _btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "选择宿主进程的工作目录" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _cwd.Inner.Text = dlg.SelectedPath;
        };

        int row = 0;
        panel.Controls.Add(MkLabel("attach 端口"), 0, row);
        var portWrap = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        portWrap.Controls.Add(_port);
        portWrap.Controls.Add(MkLabel("0 = 仅 spawn 模式（不探测已有实例）", muted: true));
        panel.Controls.Add(portWrap, 1, row);
        row++;
        panel.Controls.Add(MkLabel("工作目录"), 0, row);
        var cwdWrap = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        cwdWrap.Controls.Add(_cwd);
        cwdWrap.Controls.Add(_btnBrowse);
        panel.Controls.Add(cwdWrap, 1, row);
        row++;
        panel.Controls.Add(MkLabel("关闭行为"), 0, row);
        var closeWrap = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown };
        closeWrap.Controls.Add(_rbTray);
        closeWrap.Controls.Add(_rbExit);
        panel.Controls.Add(closeWrap, 1, row);
        row++;
        panel.Controls.Add(MkLabel("开机自启"), 0, row);
        panel.Controls.Add(_chkAutoStart, 1, row);
        row++;
        panel.Controls.Add(MkLabel("DSH_HOME"), 0, row);
        panel.Controls.Add(_dshHome, 1, row);
        row++;

        // SSH 连接管理（多连接，本地连接始终存在）
        panel.Controls.Add(MkLabel("SSH 连接"), 0, row);
        var sshWrap = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        _cmbSsh.Margin = new Padding(0, 0, 8, 8);
        _btnSshAdd.Margin = new Padding(0, 0, 8, 8);
        _btnSshEdit.Margin = new Padding(0, 0, 8, 8);
        _btnSshDel.Margin = new Padding(0, 0, 8, 8);
        _btnSshTest.Margin = new Padding(0, 0, 0, 8);
        sshWrap.Controls.Add(_cmbSsh);
        sshWrap.Controls.Add(_btnSshAdd);
        sshWrap.Controls.Add(_btnSshEdit);
        sshWrap.Controls.Add(_btnSshDel);
        sshWrap.Controls.Add(_btnSshTest);
        panel.Controls.Add(sshWrap, 1, row);
        row++;

        // 测试状态
        panel.Controls.Add(MkLabel(""), 0, row);
        panel.Controls.Add(_sshStatus, 1, row);
        row++;
        // dsh-manager Agent
        panel.Controls.Add(MkLabel("dsh-manager"), 0, row);
        panel.Controls.Add(_chkManager, 1, row); row++;
        panel.Controls.Add(MkLabel("服务器地址（HTTPS）"), 0, row);
        panel.Controls.Add(_managerUrl, 1, row); row++;
        panel.Controls.Add(MkLabel("Agent 名称"), 0, row);
        panel.Controls.Add(_managerName, 1, row); row++;
        panel.Controls.Add(MkLabel("配对码"), 0, row);
        panel.Controls.Add(_managerPairing, 1, row); row++;
        panel.Controls.Add(MkLabel("TLS 指纹（64 位 SHA-256）"), 0, row);
        panel.Controls.Add(_managerFingerprint, 1, row); row++;
        panel.Controls.Add(MkLabel("连接状态"), 0, row);
        panel.Controls.Add(_managerStatus, 1, row); row++;

        // 版本（最后一项）
        panel.Controls.Add(MkLabel("版本"), 0, row);
        panel.Controls.Add(MkLabel(VersionHelper.Current), 1, row);
        row++;

        var btnWrap = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Dock = DockStyle.Bottom,
            Padding = new Padding(0, 18, 32, 24),
        };
        btnWrap.Controls.Add(_btnSave);
        btnWrap.Controls.Add(_btnCancel);

        Controls.Add(panel);
        Controls.Add(btnWrap);
        AcceptButton = _btnSave;
        CancelButton = _btnCancel;
    }

    private static Label MkLabel(string text, bool muted = false)
    {
        return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 11, 16, 0), Tag = muted ? "muted" : null };
    }

    protected override void ApplyPalette(ThemeHelper.Palette p)
    {
        ApplyPaletteTree(this, p);
        _dshHome.ForeColor = p.MutedText;
        ApplyMuted(this, p);
    }

    private void ApplyMuted(Control c, ThemeHelper.Palette p)
    {
        if (c is Label l && l.Tag is "muted") l.ForeColor = p.MutedText;
        foreach (Control child in c.Controls) ApplyMuted(child, p);
    }

    /// <summary>保存设置到 settings 对象并持久化。DialogResult.OK 时调用。</summary>
    public void Apply()
    {
        var oldManagerUrl = _settings.Manager.ServerUrl;
        var oldManagerFingerprint = _settings.Manager.ServerCertificateFingerprint;
        _settings.AttachPort = int.TryParse(_port.Inner.Text.Trim(), out var port) ? port : 0;
        _settings.WorkingDirectory = string.IsNullOrWhiteSpace(_cwd.Inner.Text) ? null : _cwd.Inner.Text.Trim();
        _settings.CloseExits = _rbExit.Checked;
        _settings.AutoStart = _chkAutoStart.Checked;
        _settings.Manager.Enabled = _chkManager.Checked;
        _settings.Manager.ServerUrl = _managerUrl.Inner.Text.Trim();
        _settings.Manager.AgentName = _managerName.Inner.Text.Trim();
        _settings.Manager.PairingCode = _managerPairing.Inner.Text.Trim();
        _settings.Manager.ServerCertificateFingerprint = _managerFingerprint.Inner.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_settings.Manager.PairingCode))
        {
            // 填入新配对码表示用户明确要求重新配对，清除旧 Agent 凭证。
            _settings.Manager.AgentId = "";
            _settings.Manager.AgentToken = "";
        }
        if (!string.Equals(oldManagerUrl, _settings.Manager.ServerUrl, StringComparison.OrdinalIgnoreCase) || !string.Equals(oldManagerFingerprint, _settings.Manager.ServerCertificateFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Manager.AgentId = "";
            _settings.Manager.AgentToken = "";
            _managerStatus.Text = "服务器或指纹已变更，请重新配对";
        }
        _settings.Save();
        _settings.ApplyAutoStart();
    }

    private void AddSshConnection()
    {
        using var dlg = new SshEditForm();
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.ApplyAndValidate() && dlg.Result != null)
        {
            _settings.SshConnections.Add(dlg.Result);
            _settings.Save(); // 立即持久化（即使设置窗口未点保存）
            _cmbSsh.SetItems(_settings.SshConnections.Select(c => c.Name));
            _cmbSsh.SelectedIndex = _settings.SshConnections.Count - 1;
            _sshStatus.Text = "已添加连接：" + dlg.Result.DisplayName;
        }
    }

    private void EditSshConnection()
    {
        if (_cmbSsh.SelectedIndex < 0) return;
        var idx = _cmbSsh.SelectedIndex;
        var existing = _settings.SshConnections[idx];
        using var dlg = new SshEditForm(existing);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.ApplyAndValidate() && dlg.Result != null)
        {
            _settings.SshConnections[idx] = dlg.Result;
            _settings.Save();
            _cmbSsh.SetItems(_settings.SshConnections.Select(c => c.Name));
            _cmbSsh.SelectedIndex = idx;
            _sshStatus.Text = "已更新连接：" + dlg.Result.DisplayName;
        }
    }

    private void DeleteSshConnection()
    {
        if (_cmbSsh.SelectedIndex < 0) return;
        var idx = _cmbSsh.SelectedIndex;
        var name = _settings.SshConnections[idx].Name;
        if (MessageBox.Show(this, "删除 SSH 连接「" + name + "」？", "删除连接",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _settings.SshConnections.RemoveAt(idx);
        _settings.Save();
        _cmbSsh.SetItems(_settings.SshConnections.Select(c => c.Name));
        if (_cmbSsh.ItemCount > 0) _cmbSsh.SelectedIndex = 0;
        _sshStatus.Text = "已删除连接";
    }

    private async Task TestSshConnectionAsync()
    {
        if (_cmbSsh.SelectedIndex < 0) return;
        var cfg = _settings.SshConnections[_cmbSsh.SelectedIndex];
        _btnSshTest.Enabled = false;
        _sshStatus.Text = "正在测试连接…";
        try
        {
            var result = await new SshConnection(cfg).TestConnectionAsync();
            _sshStatus.Text = result;
        }
        catch (Exception ex)
        {
            _sshStatus.Text = "测试失败: " + ex.Message;
        }
        finally
        {
            _btnSshTest.Enabled = true;
        }
    }
}
