using System.Diagnostics;
using System.Drawing;

namespace DshLauncher;

/// <summary>
/// 宿主日志查看窗口：实时显示 dsh 宿主 stdout/stderr，自动滚动到尾部，带清空与打开日志目录操作。
/// Mica 材质 + 自适应主题。关闭即销毁，可随时重新打开。
/// </summary>
public sealed class LogForm : ThemedForm
{
    private readonly RichTextBox _box = new()
    {
        ReadOnly = true,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f),
        BorderStyle = BorderStyle.None,
    };
    private readonly ThemedCheckBox _autoScroll = new() { Text = "自动滚动", Checked = true };
    private readonly RoundedButton _btnOpenDir = new() { Text = "打开日志目录", Width = 128, Height = 36 };
    private readonly RoundedButton _btnClear = new() { Text = "清空", Width = 84, Height = 36 };
    private readonly HostSupervisor _host;
    private readonly int _maxLines = 4000;
    private int _lineCount;

    public LogForm(HostSupervisor host)
    {
        _host = host;
        Text = "dsh 宿主日志";
        Width = 960;
        Height = 600;
        MinimumSize = new Size(660, 380);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(20, 12, 20, 14),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        _btnOpenDir.Click += (_, _) => OpenDir();
        _btnClear.Click += (_, _) =>
        {
            _box.Clear();
            _lineCount = 0;
        };
        bottom.Controls.Add(_autoScroll);
        bottom.Controls.Add(_btnOpenDir);
        bottom.Controls.Add(_btnClear);

        Controls.Add(_box);
        Controls.Add(bottom);

        // 先加载历史日志（最后 1000 行），再订阅实时追加
        LoadHistory();
        _host.LogLine += AppendLine;
        FormClosed += (_, _) => _host.LogLine -= AppendLine;
    }

    /// <summary>加载日志文件尾部，让日志窗口一打开就能看到已有内容。</summary>
    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_host.LogFile)) return;
            var lines = File.ReadLines(_host.LogFile).TakeLast(1000).ToList();
            foreach (var line in lines)
            {
                _box.AppendText(line + Environment.NewLine);
                _lineCount++;
            }
            if (_autoScroll.Checked)
            {
                _box.SelectionStart = _box.TextLength;
                _box.ScrollToCaret();
            }
        }
        catch
        {
            // 历史加载失败不阻塞
        }
    }

    protected override void ApplyPalette(ThemeHelper.Palette p)
    {
        ApplyPaletteTree(this, p);
    }

    /// <summary>追加一行日志（线程安全；UI 线程外自动 Invoke）。</summary>
    public void AppendLine(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLine(line));
            return;
        }
        _box.AppendText(line + Environment.NewLine);
        _lineCount++;
        if (_lineCount > _maxLines)
        {
            var nl = _box.Text.IndexOf('\n');
            if (nl >= 0)
            {
                _box.Select(0, nl + 1);
                _box.SelectedText = "";
            }
            _lineCount--;
        }
        if (_autoScroll.Checked)
        {
            _box.SelectionStart = _box.TextLength;
            _box.ScrollToCaret();
        }
    }

    private void OpenDir()
    {
        try
        {
            var dir = Path.GetDirectoryName(_host.LogFile) ?? "";
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开日志目录失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
