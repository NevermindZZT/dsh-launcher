using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

/// <summary>在 launcher 内打开外部链接的独立 WebView2 窗口。</summary>
public sealed class LinkWindow : Form
{
    private readonly MainForm _main;
    private readonly Uri _initialUri;
    private readonly WebView2 _web = new();
    private bool _initialized;

    public LinkWindow(MainForm main, Uri uri)
    {
        _main = main;
        _initialUri = uri;
        Text = WebShellBridge.FormatSessionTitle(uri.Host);
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 480);
        Size = new Size(1200, 800);
        Icon = MainForm.LoadAppIconShared();
        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);
        FormClosed += (_, _) => _web.Dispose();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_initialized) return;
        _initialized = true;
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DshLauncher", "links");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(env);
            var cwv = _web.CoreWebView2;
            cwv.Settings.AreDefaultContextMenusEnabled = true;
            cwv.Settings.AreDevToolsEnabled = false;
            cwv.Settings.IsStatusBarEnabled = false;
            cwv.NavigationStarting += OnNavigationStarting;
            cwv.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                _main.OpenExternalLink(args.Uri);
            };
            cwv.DocumentTitleChanged += (_, _) =>
            {
                var title = cwv.DocumentTitle;
                if (!string.IsNullOrWhiteSpace(title)) Text = WebShellBridge.FormatSessionTitle(title);
            };
            _web.Source = _initialUri;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开链接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is "http" or "https") return;
        e.Cancel = true;
        _main.OpenExternalLink(e.Uri);
    }
}
