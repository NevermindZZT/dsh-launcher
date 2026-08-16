using System.Drawing;

namespace DshLauncher;

/// <summary>
/// 远端目录浏览器（SSH）：通过 ssh ls 导航服务器目录树，选择目录作为远端 dsh 工作区。
/// </summary>
public sealed class RemoteFolderBrowserForm : ThemedForm
{
    private readonly Func<string, List<string>> _listDirs;
    private string _current = "~";
    private readonly RemoteDirList _dirList = new() { Dock = DockStyle.Fill };
    private readonly InputBox _pathBox = new(34) { Width = 420 };
    private readonly RoundedButton _btnUp = new() { Text = "上级", Width = 64, Height = 34 };
    private readonly RoundedButton _btnHome = new() { Text = "~", Width = 48, Height = 34 };
    private readonly RoundedButton _btnRoot = new() { Text = "/", Width = 48, Height = 34 };
    private readonly RoundedButton _btnGo = new() { Text = "跳转", Width = 64, Height = 34 };
    private readonly RoundedButton _btnOpen = new() { Text = "选择此目录", Width = 120, Height = 36, DialogResult = DialogResult.OK };
    private readonly RoundedButton _btnCancel = new() { Text = "取消", Width = 96, Height = 36, DialogResult = DialogResult.Cancel };
    private readonly Label _hint = new() { Dock = DockStyle.Top, Height = 32, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(20, 4, 8, 0), Text = "选择服务器上的目录作为 dsh 工作区：" };

    /// <summary>选中的远端目录路径（DialogResult.OK 时有效）。</summary>
    public string? SelectedPath { get; private set; }

    public RemoteFolderBrowserForm(Func<string, List<string>> listDirs)
    {
        _listDirs = listDirs;
        Text = "打开远端文件夹";
        Width = 660;
        Height = 560;
        MinimumSize = new Size(600, 480);
        StartPosition = FormStartPosition.CenterParent;

        BackColor = Color.FromArgb(0x1E, 0x20, 0x24);
        _dirList.DirectoryActivated += path => NavigateTo(path);
        _dirList.DirectorySelected += _ => { }; // 单击仅选中

        var pathBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(20, 8, 20, 4), WrapContents = false };
        pathBar.Controls.Add(_pathBox);
        pathBar.Controls.Add(_btnGo);
        pathBar.Controls.Add(_btnUp);
        pathBar.Controls.Add(_btnHome);
        pathBar.Controls.Add(_btnRoot);

        _btnGo.Click += (_, _) => NavigateTo(_pathBox.Inner.Text);
        _btnUp.Click += (_, _) => NavigateTo(UpPath(_current));
        _btnHome.Click += (_, _) => NavigateTo("~");
        _btnRoot.Click += (_, _) => NavigateTo("/");
        _pathBox.Inner.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { _btnGo.PerformClick(); e.Handled = true; } };
        _btnOpen.Click += (_, _) =>
        {
            SelectedPath = _dirList.SelectedPath ?? _current;
        };

        var btnWrap = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Dock = DockStyle.Bottom, Padding = new Padding(0, 12, 20, 18)
        };
        btnWrap.Controls.Add(_btnOpen);
        btnWrap.Controls.Add(_btnCancel);

        Controls.Add(_dirList);
        Controls.Add(btnWrap);
        Controls.Add(pathBar);
        Controls.Add(_hint);
        AcceptButton = _btnOpen;
        CancelButton = _btnCancel;
        NavigateTo(_current);
    }

    private void NavigateTo(string path)
    {
        try
        {
            var dirs = _listDirs(path);
            _current = string.IsNullOrWhiteSpace(path) ? "~" : path.Trim();
            _pathBox.Inner.Text = _current;
            _dirList.SetItems(dirs);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "读取目录失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void EnterDir(string dir) => NavigateTo(dir);

    private static string UpPath(string path)
    {
        var p = path == "~" ? "/home" : path;
        var idx = p.TrimEnd('/').LastIndexOf('/');
        return idx <= 0 ? "/" : p.Substring(0, idx);
    }

    protected override void ApplyPalette(ThemeHelper.Palette p)
    {
        ApplyPaletteTree(this, p);
        _dirList.Surface = p.Surface;
        _dirList.Text = p.Text;
        _dirList.Accent = p.Accent;
        _dirList.Invalidate();
        _pathBox.SetWindowBack(p.WindowBack);
    }
}
