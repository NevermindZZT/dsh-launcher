using System.Drawing;

namespace DshLauncher;

/// <summary>
/// 插件管理窗口：列出 profile 已装插件，支持安装/卸载/更新（走官方 dsh plugin = pnpm 转发），成功后自动重启宿主。
/// Win11 Fluent 风格（Mica + 大留白 + 圆角控件）；ListView 列宽随窗口自适应。
/// </summary>
public sealed class PluginsForm : ThemedForm
{
    private readonly PluginManager _manager;
    private readonly Func<Task> _restartHost;
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.None, OwnerDraw = true };
    private readonly InputBox _pkg = new(36, "插件包名，如 dshmarket") { Width = 260 };
    private readonly RoundedButton _btnInstall = new() { Text = "安装", Width = 84, Height = 36 };
    private readonly RoundedButton _btnRemove = new() { Text = "卸载选中", Width = 96, Height = 36 };
    private readonly RoundedButton _btnUpdate = new() { Text = "更新选中", Width = 96, Height = 36 };
    private readonly RoundedButton _btnRefresh = new() { Text = "刷新", Width = 84, Height = 36 };
    private readonly RichTextBox _output = new()
    {
        ReadOnly = true,
        Dock = DockStyle.Bottom,
        Height = 180,
        Font = new Font("Consolas", 9.5f),
        BorderStyle = BorderStyle.None,
        Margin = new Padding(0, 12, 0, 0),
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 36,
        Padding = new Padding(20, 0, 20, 0),
        TextAlign = ContentAlignment.MiddleLeft,
    };
    private readonly ToolTip _statusTip = new() { AutoPopDelay = 8000 };

    public PluginsForm(PluginManager manager, Func<Task> restartHost)
    {
        _manager = manager;
        _restartHost = restartHost;
        Text = "插件管理";
        Width = 1000;
        Height = 720;
        MinimumSize = new Size(860, 560);

        _list.Columns.Add("插件", 360);
        _list.Columns.Add("类型", 130);
        _list.Columns.Add("规格", 260);
        _list.Resize += (_, _) => ResizeColumns();
        _list.DrawColumnHeader += OnDrawColumnHeader;
        _list.DrawSubItem += (_, e) => e.DrawDefault = true;
        _list.DrawItem += (_, e) => { }; // 与 DrawSubItem 配合：Details 模式不绘制整行背景（由子项默认绘制）

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(20, 20, 20, 14),
            WrapContents = true,
        };
        _pkg.Inner.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { _ = RunAsync("add", _pkg.Inner.Text); } };
        _btnInstall.Click += (_, _) => _ = RunAsync("add", _pkg.Inner.Text);
        _btnRemove.Click += (_, _) => _ = RunRemoveAsync();
        _btnUpdate.Click += (_, _) => _ = RunUpdateAsync();
        _btnRefresh.Click += (_, _) => RefreshList();
        top.Controls.Add(_pkg);
        top.Controls.Add(_btnInstall);
        top.Controls.Add(_btnRemove);
        top.Controls.Add(_btnUpdate);
        top.Controls.Add(_btnRefresh);

        Controls.Add(_list);
        Controls.Add(_output);
        Controls.Add(_status);
        Controls.Add(top);

        RefreshList();
    }

    protected override void ApplyPalette(ThemeHelper.Palette p)
    {
        ApplyPaletteTree(this, p);
        _status.ForeColor = p.MutedText;
        _list.Invalidate();
    }

    /// <summary>自绘列头：深色背景 + 主题文字 + 分隔线（WinUI 3 风格表头）。</summary>
    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var p = Palette;
        var rect = e.Bounds;
        using var fill = new SolidBrush(p.SurfaceAlt);
        e.Graphics.FillRectangle(fill, rect);
        using var pen = new Pen(p.Border);
        e.Graphics.DrawLine(pen, rect.Right - 1, rect.Top + 3, rect.Right - 1, rect.Bottom - 3);
        var col = e.ColumnIndex >= 0 && e.ColumnIndex < _list.Columns.Count ? _list.Columns[e.ColumnIndex] : null;
        var text = col?.Text ?? "";
        var textRect = new Rectangle(rect.X + 8, rect.Y, Math.Max(0, rect.Width - 16), rect.Height);
        TextRenderer.DrawText(e.Graphics, text, Font, textRect, p.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>ListView 列宽随窗口自适应（比例分配，防止内容溢出被裁剪）。</summary>
    private void ResizeColumns()
    {
        if (_list.Columns.Count < 3 || _list.ClientSize.Width <= 0) return;
        var total = _list.ClientSize.Width;
        var w0 = (int)(total * 0.48);
        var w1 = (int)(total * 0.16);
        var w2 = total - w0 - w1;
        _list.BeginUpdate();
        _list.Columns[0].Width = Math.Max(140, w0);
        _list.Columns[1].Width = Math.Max(90, w1);
        _list.Columns[2].Width = Math.Max(120, w2);
        _list.EndUpdate();
    }

    /// <summary>压缩路径显示：保留前 2 段与后 2 段，中间用 … 省略。</summary>
    private static string CompactPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= 44) return path;
        var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 4) return path;
        var head = string.Join(Path.DirectorySeparatorChar.ToString(), parts.Take(2));
        var tail = string.Join(Path.DirectorySeparatorChar.ToString(), parts.TakeLast(2));
        return head + "…" + Path.DirectorySeparatorChar + tail;
    }

    private void RefreshList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var pl in _manager.ListPlugins())
        {
            var kind = (pl.IsBundle ? "bundle" : "依赖") + (pl.IsTemplate ? "·模板" : "");
            var item = new ListViewItem(pl.Package) { Tag = pl.Package };
            item.SubItems.Add(kind);
            item.SubItems.Add(pl.Spec ?? "");
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        ResizeColumns();
        // 路径压缩显示（完整路径放 ToolTip，避免长路径被裁剪）
        var shortProfile = CompactPath(_manager.ProfileDir);
        _status.Text = $"已装 {_list.Items.Count} 个插件 · profile: {shortProfile}";
        _statusTip.SetToolTip(_status, $"profile: {_manager.ProfileDir}");
    }

    private string? SelectedPackage()
    {
        var sel = _list.SelectedItems.Cast<ListViewItem>().FirstOrDefault();
        return sel?.Tag as string;
    }

    private async Task RunAsync(string action, string? pkg)
    {
        if (string.IsNullOrWhiteSpace(pkg))
        {
            _status.Text = "请输入插件包名";
            return;
        }
        AppendOutput($">>> dsh plugin --profile web {action} {pkg.Trim()}");
        _status.Text = $"正在{action} {pkg.Trim()} …";
        try
        {
            var code = await _manager.RunAsync(new[] { action, pkg.Trim() }, AppendOutput);
            if (code == 0)
            {
                _status.Text = $"{action} 成功，正在重启宿主使插件生效…";
                await _restartHost();
                _status.Text = $"{action} 成功，宿主已重启";
            }
            else
            {
                _status.Text = $"{action} 失败（exit {code}），详见上方输出";
            }
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            AppendOutput("[err] " + ex.Message);
        }
        RefreshList();
    }

    private async Task RunRemoveAsync()
    {
        var pkg = SelectedPackage();
        if (pkg == null) { _status.Text = "请先在列表中选择要卸载的插件"; return; }
        await RunAsync("remove", pkg);
    }

    private async Task RunUpdateAsync()
    {
        var pkg = SelectedPackage();
        if (pkg == null) { _status.Text = "请先在列表中选择要更新的插件"; return; }
        await RunAsync("update", pkg);
    }

    private void AppendOutput(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => AppendOutput(line)); return; }
        _output.AppendText(line + Environment.NewLine);
        _output.SelectionStart = _output.TextLength;
        _output.ScrollToCaret();
    }
}
