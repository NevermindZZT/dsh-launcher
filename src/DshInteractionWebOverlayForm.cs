using System.Drawing;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

/// <summary>Standalone always-on-top WebView2 window for an Agent interaction.</summary>
internal sealed class DshInteractionWebOverlayForm : Form
{
    private readonly DshPendingInteraction _interaction;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private bool _settled;
    private bool _submitting;
    private ThemeHelper.Palette _palette = ThemeHelper.CurrentPagePalette;

    public event Func<DshInteractionDecision, Task>? DecisionSelected;

    public DshInteractionWebOverlayForm(DshPendingInteraction interaction)
    {
        _interaction = interaction;
        Text = interaction.Kind == DshInteractionKind.Approval
            ? $"需要确认 · {interaction.SourceName}"
            : $"Agent 正在等待回答 · {interaction.SourceName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        LauncherIconTheme.Attach(this);
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var maximumWidth = Math.Max(520, workingArea.Width - 32);
        var maximumHeight = Math.Max(460, workingArea.Height - 32);
        MinimumSize = new Size(520, 340);
        MaximumSize = new Size(Math.Min(860, maximumWidth), maximumHeight);
        Width = Math.Min(maximumWidth, interaction.Kind == DshInteractionKind.Approval ? 600 : 760);
        Height = interaction.Kind == DshInteractionKind.Approval
            ? Math.Min(maximumHeight, 400)
            : Math.Min(maximumHeight, EstimateQuestionHeight(interaction));
        PositionAtBottomRight();
        BackColor = _palette.WindowBack;
        ForeColor = _palette.Text;
        _web.DefaultBackgroundColor = _palette.WindowBack;
        Controls.Add(_web);
        Load += (_, _) => PositionAtBottomRight();
        Shown += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) => ThemeHelper.PagePaletteChanged -= OnPagePaletteChanged;
        FormClosing += (_, e) =>
        {
            if (_settled) return;
            e.Cancel = true;
            if (!_submitting) BeginSubmit(interaction.Kind == DshInteractionKind.Approval ? DshInteractionDecision.CancelApproval() : DshInteractionDecision.CancelQuestion());
        };
    }

    public bool Matches(DshPendingInteraction interaction) =>
        string.Equals(_interaction.SourceKey, interaction.SourceKey, StringComparison.Ordinal) &&
        string.Equals(_interaction.EventId, interaction.EventId, StringComparison.Ordinal);

    public void DismissCancelled()
    {
        if (_settled) return;
        _settled = true;
        Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeHelper.PagePaletteChanged += OnPagePaletteChanged;
        ApplyWindowPalette(_palette);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        ThemeHelper.PagePaletteChanged -= OnPagePaletteChanged;
        base.OnHandleDestroyed(e);
    }

    private void OnPagePaletteChanged(ThemeHelper.Palette palette)
    {
        _palette = palette;
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(() => ApplyWindowPalette(palette));
        else ApplyWindowPalette(palette);
    }

    private void ApplyWindowPalette(ThemeHelper.Palette palette)
    {
        _palette = palette;
        BackColor = palette.WindowBack;
        ForeColor = palette.Text;
        _web.DefaultBackgroundColor = palette.WindowBack;
        if (IsHandleCreated)
        {
            ThemeHelper.ApplyWindowTheme(Handle, palette.WindowBack.GetBrightness() < 0.55f);
            ThemeHelper.ApplyTitleBarPalette(Handle, palette);
        }
        WebThemeBridge.Apply(_web, _palette);
    }

    private void ApplyWebPalette() => WebThemeBridge.Apply(_web, _palette);

    private static int EstimateQuestionHeight(DshPendingInteraction interaction)
    {
        // Give a typical four-choice question enough room for its choices, optional custom answer, and action row without relying on the WebView scrollbar.
        var questionsHeight = interaction.Questions.Sum(question =>
            84 + Math.Max(1, question.Options.Count) * 62 + (string.IsNullOrWhiteSpace(question.Detail) ? 0 : 24));
        return Math.Max(540, 210 + questionsHeight);
    }

    private void PositionAtBottomRight()
    {
        const int margin = 16;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(Math.Max(area.Left + margin, area.Right - Width - margin), Math.Max(area.Top + margin, area.Bottom - Height - margin));
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async();
            var core = _web.CoreWebView2 ?? throw new InvalidOperationException("WebView2 初始化失败");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.WebMessageReceived += (_, message) => HandleWebMessage(message.TryGetWebMessageAsString());
            core.NavigationCompleted += (_, _) => ApplyWebPalette();
            core.NavigateToString(BuildDocument());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法显示 Agent 问题窗口：" + ex.Message, "初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void HandleWebMessage(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (TextOf(root, "type") != "interaction") return;
            var action = TextOf(root, "action");
            if (action == "cancel") BeginSubmit(_interaction.Kind == DshInteractionKind.Approval ? DshInteractionDecision.CancelApproval() : DshInteractionDecision.CancelQuestion());
            else if (action == "allow") BeginSubmit(DshInteractionDecision.AllowOnce());
            else if (action == "reject") BeginSubmit(DshInteractionDecision.Reject());
            else if (action == "submit" && root.TryGetProperty("answers", out var answers)) SubmitAnswers(answers);
        }
        catch { }
    }

    private void SubmitAnswers(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return;
        var answers = new List<DshQuestionAnswer>();
        foreach (var answer in value.EnumerateArray())
        {
            var id = TextOf(answer, "id");
            if (string.IsNullOrWhiteSpace(id)) return;
            var selected = answer.TryGetProperty("selected", out var options) && options.ValueKind == JsonValueKind.Array
                ? options.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                : Array.Empty<string>();
            var custom = TextOf(answer, "custom");
            if (selected.Length == 0 && string.IsNullOrWhiteSpace(custom))
            {
                SetError("请为每个问题选择选项或填写回答。");
                return;
            }
            answers.Add(new DshQuestionAnswer(id, selected, string.IsNullOrWhiteSpace(custom) ? null : custom));
        }
        if (answers.Count != _interaction.Questions.Count) { SetError("回答数量与问题不一致。"); return; }
        BeginSubmit(DshInteractionDecision.Answer(answers));
    }

    private async void BeginSubmit(DshInteractionDecision decision)
    {
        if (_settled || _submitting) return;
        _submitting = true;
        SetBusy(true);
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
            SetBusy(false);
            SetError("提交失败：" + ex.Message);
        }
    }

    private void SetBusy(bool busy) => Execute($"window.__dshInteractionSetBusy && window.__dshInteractionSetBusy({(busy ? "true" : "false")});");
    private void SetError(string text) => Execute("window.__dshInteractionSetError && window.__dshInteractionSetError(" + JsonSerializer.Serialize(text) + ");");
    private void Execute(string script) { if (_web.CoreWebView2 != null) _ = _web.CoreWebView2.ExecuteScriptAsync(script); }

    private string BuildDocument()
    {
        var data = new { kind = _interaction.Kind.ToString(), source = _interaction.SourceName, agentId = _interaction.AgentId, toolName = _interaction.ToolName, reason = _interaction.Reason, questions = _interaction.Questions };
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
        return Html.Replace("__PAYLOAD__", encoded, StringComparison.Ordinal);
    }

    private static string? TextOf(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private const string Html = """
<!doctype html><html><head><meta charset="utf-8"><style>
:root{color-scheme:dark}*{box-sizing:border-box}body{margin:0;background:#1e1e1e;color:#f5f7fb;font:14px/1.45 Segoe UI,system-ui,sans-serif}.card{padding:20px 22px 18px;max-height:100vh;overflow:auto}.question{margin:14px 0}.header,.detail{font-size:13px;color:#b8c0cc}.prompt{font-size:15px;font-weight:700;margin:4px 0 8px}.choice{display:flex;gap:10px;width:100%;border:0;border-radius:10px;padding:8px;background:transparent;color:inherit;text-align:left;cursor:pointer}.choice:hover,.choice.sel{background:#2d3948}.mark{width:18px;height:18px;box-sizing:border-box;flex:0 0 18px;margin-top:2px;border:2px solid #667085;border-radius:50%}.multi .mark{border-radius:5px}.sel .mark{border-color:#4cc2ff;background:#4cc2ff;box-shadow:inset 0 0 0 4px #2d3948}.label{font-weight:600}.desc{font-size:13px;color:#b8c0cc;margin-top:2px}textarea{width:100%;min-height:72px;resize:vertical;margin-top:10px;padding:10px 12px;border:1px solid #434958;border-radius:10px;outline:none;background:#29292d;color:#f5f7fb;font:inherit}textarea:focus{border-color:#4cc2ff;box-shadow:0 0 0 3px #4cc2ff33}.error{min-height:20px;color:#ffb4ab;margin-top:12px}.actions{display:flex;justify-content:flex-end;gap:8px;margin-top:12px}.action{border:1px solid #454b57;border-radius:9px;background:#29292d;color:#f5f7fb;padding:8px 16px;cursor:pointer;font:inherit}.primary{border-color:#4cc2ff;background:#4cc2ff;color:#062033}.action:disabled{opacity:.55;cursor:wait}
</style></head><body><main class="card" id="app"></main><script>
const raw=atob('__PAYLOAD__'),bytes=Uint8Array.from(raw,c=>c.charCodeAt(0)),data=JSON.parse(new TextDecoder().decode(bytes)),app=document.querySelector('#app');let busy=false;const esc=v=>String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));const send=v=>chrome.webview.postMessage(JSON.stringify(v));
function setBusy(v){busy=v;app.querySelectorAll('button').forEach(x=>x.disabled=v)}window.__dshInteractionSetBusy=setBusy;window.__dshInteractionSetError=v=>{app.querySelector('.error').textContent=v};
if(data.kind==='Approval'){app.innerHTML='<div class="prompt">工具：'+esc(data.toolName||'未命名工具')+'</div><div class="detail">'+esc(data.reason||'')+'</div><div class="error"></div><div class="actions"><button class="action" data-a="cancel">取消</button><button class="action" data-a="reject">拒绝</button><button class="action primary" data-a="allow">允许一次</button></div>'}else{let html='';data.questions.forEach((q,qi)=>{html+='<section class="question">'+(q.header?'<div class="header">'+esc(q.header)+'</div>':'')+'<div class="prompt">'+esc(q.question)+'</div>'+(q.detail?'<div class="detail">'+esc(q.detail)+'</div>':'');(q.options||[]).forEach(o=>html+='<button class="choice '+(q.multiSelect?'multi':'')+'" data-q="'+qi+'" data-o="'+esc(o.label)+'"><span class="mark"></span><span><div class="label">'+esc(o.label)+'</div>'+(o.description?'<div class="desc">'+esc(o.description)+'</div>':'')+'</span></button>');html+='<textarea data-c="'+qi+'" placeholder="可选：填写自定义回答"></textarea></section>'});app.innerHTML=html+'<div class="error"></div><div class="actions"><button class="action" data-a="cancel">取消</button><button class="action primary" data-a="submit">提交回答</button></div>';let selected=data.questions.map(()=>[]);app.addEventListener('click',e=>{let b=e.target.closest('.choice');if(b){let qi=+b.dataset.q,q=data.questions[qi],label=b.dataset.o;if(q.multiSelect){selected[qi]=selected[qi].includes(label)?selected[qi].filter(x=>x!==label):selected[qi].concat(label);b.classList.toggle('sel',selected[qi].includes(label))}else{selected[qi]=[label];app.querySelectorAll('.choice[data-q="'+qi+'"]').forEach(x=>x.classList.toggle('sel',x===b));app.querySelector('[data-c="'+qi+'"]').value=''}return}let a=e.target.closest('[data-a]')?.dataset.a;if(!a||busy)return;if(a==='cancel'){setBusy(true);send({type:'interaction',action:'cancel'});return}if(a==='submit'){let answers=[],missing=false;data.questions.forEach((q,qi)=>{let custom=app.querySelector('[data-c="'+qi+'"]').value.trim(),sel=custom&&!q.multiSelect?[]:selected[qi];if(!custom&&!sel.length)missing=true;let answer={id:q.id,selected:sel};if(custom)answer.custom=custom;answers.push(answer)});if(missing){window.__dshInteractionSetError('请为每个问题选择选项或填写回答。');return}setBusy(true);send({type:'interaction',action:'submit',answers})}})}
app.addEventListener('click',e=>{let a=e.target.closest('[data-a]')?.dataset.a;if(!a||busy)return;if(a==='cancel'||a==='allow'||a==='reject'){setBusy(true);send({type:'interaction',action:a})}});
</script></body></html>
""";
}
