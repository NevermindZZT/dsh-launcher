using System.Drawing;

namespace DshLauncher;

/// <summary>Small themed prompt for a user-supplied DSH process startup token.</summary>
internal sealed class DshStartupTokenDialog : ThemedForm
{
    private readonly Label _intro = new()
    {
        AutoSize = true,
        MaximumSize = new Size(500, 0),
        Margin = new Padding(0, 0, 0, 16),
        Text = DshStartupAuthMessages.TokenPromptIntro,
    };
    private readonly Label _tokenLabel = new()
    {
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 4),
        Text = DshStartupAuthMessages.TokenInputLabel,
    };
    private readonly InputBox _token = new(42, DshStartupAuthMessages.TokenInputHint)
    {
        Dock = DockStyle.Top,
        Margin = new Padding(0, 0, 0, 8),
    };
    private readonly Label _error = new()
    {
        AutoSize = true,
        MaximumSize = new Size(500, 0),
        Margin = new Padding(0, 0, 0, 4),
    };
    private readonly RoundedButton _continue = new() { Text = DshStartupAuthMessages.ContinueButton, Width = 96, Height = 38 };
    private readonly RoundedButton _cancel = new() { Text = DshStartupAuthMessages.CancelButton, Width = 96, Height = 38, DialogResult = DialogResult.Cancel };

    public string? Token { get; private set; }

    public DshStartupTokenDialog()
    {
        Text = DshStartupAuthMessages.TokenPromptTitle;
        ClientSize = new Size(560, 290);
        MinimumSize = new Size(520, 270);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AcceptButton = _continue;
        CancelButton = _cancel;

        _token.Inner.UseSystemPasswordChar = true;
        _token.Inner.TabIndex = 0;
        _continue.TabIndex = 1;
        _cancel.TabIndex = 2;
        _continue.Click += (_, _) => AcceptToken();
        _cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0),
        };
        actions.Controls.Add(_continue);
        actions.Controls.Add(_cancel);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(24),
            BackColor = ThemeHelper.CurrentPagePalette.WindowBack,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_intro, 0, 0);
        layout.Controls.Add(_tokenLabel, 0, 1);
        layout.Controls.Add(_token, 0, 2);
        layout.Controls.Add(_error, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);
    }

    protected override void ApplyPalette(ThemeHelper.Palette p)
    {
        ApplyPaletteTree(this, p);
        _intro.ForeColor = p.Text;
        _tokenLabel.ForeColor = p.MutedText;
        _error.ForeColor = p.Accent;
        _token.SetWindowBack(p.WindowBack);
        _token.BackColor = p.Surface;
        _token.Inner.BackColor = p.Surface;
        _token.Inner.ForeColor = p.Text;
        _continue.BackColor = p.Accent;
        _continue.ForeColor = ThemeHelper.ContrastingText(p.Accent);
        _continue.SetWindowBack(p.WindowBack);
        _cancel.BackColor = p.Surface;
        _cancel.ForeColor = p.Text;
        _cancel.SetWindowBack(p.WindowBack);
    }

    private void AcceptToken()
    {
        if (!DshStartupUrl.TryExtractToken(_token.Inner.Text, out var token))
        {
            _error.Text = DshStartupAuthMessages.TokenInputInvalid;
            _token.Inner.Focus();
            return;
        }

        Token = token;
        _token.Inner.Clear();
        DialogResult = DialogResult.OK;
        Close();
    }
}
