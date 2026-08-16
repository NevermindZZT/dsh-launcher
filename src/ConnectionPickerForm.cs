using System.Drawing;

namespace DshLauncher;

/// <summary>
/// 连接选择器弹窗（Ctrl+Shift+C）：列出本地 + 所有 SSH 连接（名称/状态/地址），
/// 每个连接一个行按钮，点击即选择 —— 不用 ListView，避免自绘白色/焦点/失焦问题。
/// </summary>
public sealed class ConnectionPickerForm : ThemedForm
{
    private readonly RoundedButton _btnCancel = new() { Text = "取消", Width = 96, Height = 36, DialogResult = DialogResult.Cancel };
    private readonly Label _hint = new()
    {
        Dock = DockStyle.Top, Height = 36, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(24, 6, 8, 0), Text = "选择要连接的服务器（点击即连接）：",
    };

    /// <summary>选择结果（DialogResult.OK 时有效）。</summary>
    public IDshConnection? Selected { get; private set; }

    public ConnectionPickerForm(ConnectionManager manager)
    {
        Text = "选择连接";
        Width = 500;
        Height = Math.Min(620, 140 + manager.Connections.Count * 68);
        MinimumSize = new Size(460, 320);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(24, 16, 24, 12),
        };
        foreach (var c in manager.Connections)
        {
            var btn = new RoundedButton
            {
                Text = RowText(c),
                AutoSize = false, Width = 430, Height = 50,
                TextAlign = ContentAlignment.MiddleLeft,
                Tag = c,
            };
            btn.Click += (_, _) =>
            {
                Selected = (IDshConnection)btn.Tag!;
                DialogResult = DialogResult.OK;
                Close();
            };
            flow.Controls.Add(btn);
        }

        var btnWrap = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Dock = DockStyle.Bottom, Padding = new Padding(0, 10, 24, 18),
        };
        btnWrap.Controls.Add(_btnCancel);

        Controls.Add(flow);
        Controls.Add(btnWrap);
        Controls.Add(_hint);
        CancelButton = _btnCancel;
    }

    private static string RowText(IDshConnection c) => c switch
    {
        SshConnection sc => $"{c.DisplayName} · {sc.Config.Host}     {StatusText(c)}",
        _ => $"{c.DisplayName} · 本地     {StatusText(c)}",
    };

    private static string StatusText(IDshConnection c) => c.State switch
    {
        HostState.Running => "● 运行中",
        HostState.Starting => "● 连接中…",
        _ => "○ 未连接",
    };

    protected override void ApplyPalette(ThemeHelper.Palette p)
    {
        ApplyPaletteTree(this, p);
    }
}
