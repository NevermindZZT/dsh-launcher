using System.Drawing;

namespace DshLauncher;

/// <summary>
/// 连接选择器弹窗（Ctrl+Shift+C 触发）：列出本地 + 所有 SSH 连接及状态，
/// 选择后返回，由调用方执行连接/打开窗口。
/// </summary>
public sealed class ConnectionPickerForm : ThemedForm
{
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, HideSelection = false,
        BorderStyle = BorderStyle.None, OwnerDraw = true, Dock = DockStyle.Fill,
    };
    private readonly RoundedButton _btnOpen = new() { Text = "连接 / 打开", Width = 120, Height = 36, DialogResult = DialogResult.OK };
    private readonly RoundedButton _btnCancel = new() { Text = "取消", Width = 96, Height = 36, DialogResult = DialogResult.Cancel };
    private readonly Label _hint = new() { Dock = DockStyle.Top, Height = 34, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(24, 4, 8, 0), Text = "选择要连接的服务器：" };

    /// <summary>选择结果（DialogResult.OK 时有效）。</summary>
    public IDshConnection? Selected { get; private set; }

    public ConnectionPickerForm(ConnectionManager manager)
    {
        Text = "选择连接";
        Width = 560;
        Height = 440;
        MinimumSize = new Size(520, 400);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _list.Columns.Add("连接", 200);
        _list.Columns.Add("状态", 110);
        _list.Columns.Add("地址", 160);
        _list.DrawColumnHeader += (_, e) =>
        {
            var p = Palette;
            using var fill = new SolidBrush(p.SurfaceAlt);
            // 填满整行列头区域（含列宽之外的空白），避免右侧白色残留
            e.Graphics.FillRectangle(fill, new Rectangle(0, e.Bounds.Y, _list.ClientSize.Width, e.Bounds.Height));
            var col = e.ColumnIndex >= 0 && e.ColumnIndex < _list.Columns.Count ? _list.Columns[e.ColumnIndex] : null;
            var text = col?.Text ?? "";
            TextRenderer.DrawText(e.Graphics, text, Font,
                new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 12), e.Bounds.Height), p.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };
        _list.DrawSubItem += (_, e) => e.DrawDefault = true;
        _list.DrawItem += (_, _) => { };

        foreach (var c in manager.Connections)
        {
            var item = new ListViewItem(c.DisplayName) { Tag = c };
            item.SubItems.Add(StateText(c));
            item.SubItems.Add(c is SshConnection sc ? $"{sc.Config.Host}:{sc.Config.Port}" : "本地");
            _list.Items.Add(item);
        }
        _list.DoubleClick += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0)
            {
                Selected = _list.SelectedItems[0].Tag as IDshConnection;
                DialogResult = DialogResult.OK;
                Close();
            }
        };
        _btnOpen.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0) Selected = _list.SelectedItems[0].Tag as IDshConnection;
        };

        var btnWrap = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Dock = DockStyle.Bottom, Padding = new Padding(0, 12, 24, 18)
        };
        btnWrap.Controls.Add(_btnOpen);
        btnWrap.Controls.Add(_btnCancel);
        Controls.Add(_list);
        Controls.Add(btnWrap);
        Controls.Add(_hint);
        AcceptButton = _btnOpen;
        CancelButton = _btnCancel;
    }

    private static string StateText(IDshConnection c) => c.State switch
    {
        HostState.Running => "● 运行中",
        HostState.Starting => "● 连接中…",
        _ => "○ 未连接",
    };

    protected override void ApplyPalette(ThemeHelper.Palette p) => ApplyPaletteTree(this, p);
}
