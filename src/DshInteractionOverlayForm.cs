using System.Drawing;

namespace DshLauncher;

/// <summary>Compact native, always-on-top surface for one DSH approval or question request.</summary>
internal sealed class DshInteractionOverlayForm : ThemedForm
{
    private const int ContentWidth = 520;
    private const int InputWidth = 500;
    private readonly DshPendingInteraction _interaction;
    private readonly FlowLayoutPanel _body = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(22, 18, 22, 8) };
    private readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 12), WrapContents = false };
    private readonly List<QuestionInputs> _questionInputs = new();
    private bool _settled;
    private bool _submitting;

    public event Func<DshInteractionDecision, Task>? DecisionSelected;

    public bool Matches(DshPendingInteraction interaction) =>
        string.Equals(_interaction.SourceKey, interaction.SourceKey, StringComparison.Ordinal) &&
        string.Equals(_interaction.EventId, interaction.EventId, StringComparison.Ordinal);

    public void DismissCancelled()
    {
        if (_settled) return;
        _settled = true;
        Close();
    }

    public DshInteractionOverlayForm(DshPendingInteraction interaction)
    {
        _interaction = interaction;
        Text = interaction.Kind == DshInteractionKind.Approval ? "需要确认 · DeepSeek Harness" : "Agent 正在等待回答";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(410, 260);
        MaximumSize = new Size(720, Math.Max(360, Screen.PrimaryScreen?.WorkingArea.Height - 40 ?? 760));
        Width = interaction.Kind == DshInteractionKind.Approval ? 510 : 620;
        Height = interaction.Kind == DshInteractionKind.Approval ? 310 : Math.Min(680, 290 + interaction.Questions.Count * 160);
        PositionAtBottomRight();
        var initialPalette = Palette;
        BackColor = initialPalette.WindowBack;
        ForeColor = initialPalette.Text;
        Controls.Add(_body);
        Controls.Add(_actions);
        _body.HandleCreated += (_, _) => ThemeHelper.ApplyScrollableControlTheme(_body, IsDark);
        Build();
        ApplyPalette(initialPalette);
        FormClosing += (_, e) =>
        {
            if (_settled) return;
            e.Cancel = true;
            if (!_submitting) BeginSubmit(interaction.Kind == DshInteractionKind.Approval ? DshInteractionDecision.CancelApproval() : DshInteractionDecision.CancelQuestion());
        };
        Load += (_, _) => PositionAtBottomRight();
    }

    private void PositionAtBottomRight()
    {
        const int margin = 16;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            Math.Max(area.Left + margin, area.Right - Width - margin),
            Math.Max(area.Top + margin, area.Bottom - Height - margin));
    }

    private void Build()
    {
        AddLabel(_interaction.Kind == DshInteractionKind.Approval ? "Agent 请求执行需要你确认的操作" : "Agent 正在等待你的回答", 17f, FontStyle.Bold, 12);
        AddLabel($"来源：{_interaction.SourceName}" + (string.IsNullOrWhiteSpace(_interaction.AgentId) ? "" : $"  ·  Agent {Short(_interaction.AgentId)}"), 10f, FontStyle.Regular, 3, muted: true);
        if (_interaction.Kind == DshInteractionKind.Approval) BuildApproval();
        else BuildQuestions();
    }

    private void BuildApproval()
    {
        AddLabel("工具：" + (_interaction.ToolName ?? "未命名工具"), 11f, FontStyle.Bold, 18);
        if (!string.IsNullOrWhiteSpace(_interaction.Reason)) AddDetail(_interaction.Reason!);
        var allow = NewButton("允许一次", DialogResult.None); allow.Click += (_, _) => BeginSubmit(DshInteractionDecision.AllowOnce());
        var reject = NewButton("拒绝", DialogResult.None); reject.Click += (_, _) => BeginSubmit(DshInteractionDecision.Reject());
        var cancel = NewButton("取消", DialogResult.None); cancel.Click += (_, _) => BeginSubmit(DshInteractionDecision.CancelApproval());
        _actions.Controls.Add(allow); _actions.Controls.Add(reject); _actions.Controls.Add(cancel);
    }

    private void BuildQuestions()
    {
        foreach (var question in _interaction.Questions)
        {
            var group = new Panel { Width = ContentWidth, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 8, 0, 8), BackColor = Palette.WindowBack };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, Width = ContentWidth, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Palette.WindowBack };
            group.Controls.Add(flow);
            if (!string.IsNullOrWhiteSpace(question.Header)) flow.Controls.Add(NewLabel(question.Header!, 10f, FontStyle.Regular, 0, muted: true));
            flow.Controls.Add(NewLabel(question.Question, 11f, FontStyle.Bold, 2));
            if (!string.IsNullOrWhiteSpace(question.Detail)) flow.Controls.Add(NewLabel(question.Detail!, 10f, FontStyle.Regular, 2, muted: true));
            var inputs = new QuestionInputs(question);
            foreach (var option in question.Options)
            {
                ButtonBase check = question.MultiSelect ? new ThemedCheckBox() : new ThemedRadioButton();
                check.Text = string.IsNullOrWhiteSpace(option.Description) ? option.Label : option.Label + "\n" + option.Description;
                check.Tag = option.Label;
                check.AutoSize = false;
                check.Width = InputWidth;
                check.Height = string.IsNullOrWhiteSpace(option.Description) ? 32 : 50;
                check.TextAlign = ContentAlignment.MiddleLeft;
                check.Padding = new Padding(8, 0, 6, 0);
                check.Margin = new Padding(0, 1, 0, 1);
                flow.Controls.Add(check);
                inputs.Options.Add(check);
            }
            var custom = new InputBox(44, "可选：填写自定义回答") { Width = InputWidth, Margin = new Padding(0, 6, 0, 2) };
            flow.Controls.Add(custom);
            inputs.Custom = custom;
            _questionInputs.Add(inputs);
            _body.Controls.Add(group);
        }
        var submit = NewButton("提交回答", DialogResult.None); submit.Click += (_, _) => SubmitQuestions();
        var cancel = NewButton("取消", DialogResult.None); cancel.Click += (_, _) => BeginSubmit(DshInteractionDecision.CancelQuestion());
        _actions.Controls.Add(submit); _actions.Controls.Add(cancel);
    }

    private void SubmitQuestions()
    {
        var answers = new List<DshQuestionAnswer>();
        foreach (var inputs in _questionInputs)
        {
            var selected = inputs.Options.Where(input => input is CheckBox { Checked: true } || input is RadioButton { Checked: true }).Select(input => input.Tag?.ToString() ?? "").Where(value => value.Length > 0).ToArray();
            var custom = inputs.Custom.Inner.Text.Trim();
            if (selected.Length == 0 && custom.Length == 0)
            {
                MessageBox.Show(this, "请为每个问题选择选项或输入回答。", "尚未完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            answers.Add(new DshQuestionAnswer(inputs.Question.Id, selected, custom.Length == 0 ? null : custom));
        }
        BeginSubmit(DshInteractionDecision.Answer(answers));
    }

    private async void BeginSubmit(DshInteractionDecision decision)
    {
        if (_settled || _submitting) return;
        _submitting = true;
        foreach (Control control in _actions.Controls) control.Enabled = false;
        try
        {
            var handlers = DecisionSelected;
            if (handlers != null)
                foreach (var handler in handlers.GetInvocationList())
                    await ((Func<DshInteractionDecision, Task>)handler)(decision);
            _settled = true;
            Close();
        }
        catch (Exception ex)
        {
            _submitting = false;
            foreach (Control control in _actions.Controls) control.Enabled = true;
            MessageBox.Show(this, "未能将回答提交给 Agent。请检查连接后重试。\n\n" + ex.Message, "提交失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void AddLabel(string text, float size, FontStyle style, int topMargin, bool muted = false) => _body.Controls.Add(NewLabel(text, size, style, topMargin, muted));

    private Label NewLabel(string text, float size, FontStyle style, int topMargin, bool muted = false) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(ContentWidth, 0), MinimumSize = new Size(0, 20), Font = new Font(Font.FontFamily, size, style), Margin = new Padding(0, topMargin, 0, 3), BackColor = Palette.WindowBack, ForeColor = muted ? Palette.MutedText : Palette.Text
    };

    private void AddDetail(string text)
    {
        var detail = new TextBox { Text = text, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, Width = InputWidth, Height = 76, Margin = new Padding(0, 10, 0, 3), TabStop = false, BackColor = Palette.Surface, ForeColor = Palette.Text };
        _body.Controls.Add(detail);
    }

    private RoundedButton NewButton(string text, DialogResult result) => new() { Text = text, DialogResult = result, AutoSize = false, Width = 96, Height = 34, CornerRadius = 8, Margin = new Padding(6, 0, 0, 0), BackColor = Palette.Surface, ForeColor = Palette.Text };

    protected override void ApplyPalette(ThemeHelper.Palette p)
    {
        ApplyPaletteTree(this, p);
        ApplyOpaqueBackgrounds(this, p);
        ThemeHelper.ApplyScrollableControlTheme(_body, IsDark);
        _body.BackColor = p.WindowBack;
        _actions.BackColor = p.WindowBack;
        foreach (var control in _actions.Controls.OfType<Button>())
        {
            control.BackColor = p.Surface;
            control.ForeColor = p.Text;
        }
        if (_actions.Controls.Count > 0 && _actions.Controls[0] is Button primary)
        {
            primary.BackColor = p.Accent;
            primary.ForeColor = Color.White;
            primary.FlatAppearance.BorderColor = p.Accent;
        }
    }

    private static void ApplyOpaqueBackgrounds(Control root, ThemeHelper.Palette p)
    {
        foreach (Control control in root.Controls)
        {
            if (control is Label || control is FlowLayoutPanel || (control is Panel && control is not InputBox))
                control.BackColor = p.WindowBack;
            ApplyOpaqueBackgrounds(control, p);
        }
    }

    private static string Short(string value) => value.Length <= 12 ? value : value[..8] + "…";

    private sealed class QuestionInputs
    {
        public DshUserQuestion Question { get; }
        public List<ButtonBase> Options { get; } = new();
        public InputBox Custom { get; set; } = null!;
        public QuestionInputs(DshUserQuestion question) => Question = question;
    }
}
