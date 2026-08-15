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
    private readonly RoundedButton _btnSave = new() { Text = "保存", DialogResult = DialogResult.OK, Width = 96, Height = 36 };
    private readonly RoundedButton _btnCancel = new() { Text = "取消", DialogResult = DialogResult.Cancel, Width = 96, Height = 36 };

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        Text = "设置";
        Width = 700;
        Height = 600;
        MinimumSize = new Size(620, 520);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(32, 30, 32, 14),
            AutoSize = false,
            RowCount = 6,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _port.Inner.Text = settings.AttachPort > 0 ? settings.AttachPort.ToString() : "0";
        _cwd.Inner.Text = settings.WorkingDirectory ?? "";
        _rbTray.Checked = !settings.CloseExits;
        _rbExit.Checked = settings.CloseExits;
        _chkAutoStart.Checked = settings.AutoStart;
        var dshHome = Environment.GetEnvironmentVariable("DSH_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        _dshHome.Text = dshHome;
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
        return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 12, 16, 0), Tag = muted ? "muted" : null };
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
        _settings.AttachPort = int.TryParse(_port.Inner.Text.Trim(), out var port) ? port : 0;
        _settings.WorkingDirectory = string.IsNullOrWhiteSpace(_cwd.Inner.Text) ? null : _cwd.Inner.Text.Trim();
        _settings.CloseExits = _rbExit.Checked;
        _settings.AutoStart = _chkAutoStart.Checked;
        _settings.Save();
        _settings.ApplyAutoStart();
    }
}
