using System.Drawing;

namespace DshLauncher;

/// <summary>
/// 输入服务器目录路径的对话框（替代完整目录浏览弹窗，更快）：
/// 直接输入路径（如 /home/zander/project），也可点「浏览…」用目录浏览器选择。
/// </summary>
public sealed class RemotePathInputForm : ThemedForm
{
    private readonly InputBox _path = new(34) { Width = 360 };
    private readonly RoundedButton _btnBrowse = new() { Text = "浏览…", Width = 84, Height = 34 };
    private readonly RoundedButton _btnOk = new() { Text = "确定", Width = 96, Height = 36, DialogResult = DialogResult.OK };
    private readonly RoundedButton _btnCancel = new() { Text = "取消", Width = 96, Height = 36, DialogResult = DialogResult.Cancel };
    private readonly Func<string, List<string>> _listDirs;

    /// <summary>用户输入的服务器目录路径（DialogResult.OK 时有效）。</summary>
    public string? SelectedPath => string.IsNullOrWhiteSpace(_path.Inner.Text) ? null : _path.Inner.Text.Trim();

    public RemotePathInputForm(Func<string, List<string>>? listDirs = null)
    {
        _listDirs = listDirs ?? (_ => new List<string>());
        Text = "添加工作区";
        Width = 520;
        Height = 180;
        MinimumSize = new Size(460, 170);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(24, 18, 24, 12),
        };
        var label = new Label { Text = "输入服务器上的目录路径：", AutoSize = true, ForeColor = Color.FromArgb(0xE0, 0xE0, 0xE0) };
        _path.Inner.PlaceholderText = "/home/zander/project";
        _path.Inner.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { _btnOk.PerformClick(); e.Handled = true; } };
        _btnBrowse.Click += (_, _) =>
        {
            using var browser = new RemoteFolderBrowserForm(_listDirs);
            if (browser.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(browser.SelectedPath))
            {
                _path.Inner.Text = browser.SelectedPath;
            }
        };
        panel.Controls.Add(label);
        panel.Controls.Add(_path);

        var btnWrap = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Dock = DockStyle.Bottom, Padding = new Padding(0, 10, 24, 16),
        };
        btnWrap.Controls.Add(_btnOk);
        btnWrap.Controls.Add(_btnCancel);
        btnWrap.Controls.Add(_btnBrowse);

        Controls.Add(panel);
        Controls.Add(btnWrap);
        AcceptButton = _btnOk;
        CancelButton = _btnCancel;
    }

    protected override void ApplyPalette(ThemeHelper.Palette p)
    {
        ApplyPaletteTree(this, p);
        _path.SetWindowBack(p.WindowBack);
    }
}
