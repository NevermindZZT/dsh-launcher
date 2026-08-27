using Microsoft.Web.WebView2.Core;

namespace DshLauncher;

internal static class WebView2PermissionPolicy
{
    public static void Attach(CoreWebView2 webView)
    {
        webView.PermissionRequested += (_, e) =>
        {
            e.State = e.PermissionKind switch
            {
                CoreWebView2PermissionKind.ClipboardRead => CoreWebView2PermissionState.Allow,
                CoreWebView2PermissionKind.Notifications when IsTrustedDshOrigin(e.Uri) => CoreWebView2PermissionState.Allow,
                _ => CoreWebView2PermissionState.Deny,
            };
            Diag.Log($"WebView2 permission: kind={e.PermissionKind}, uri={e.Uri}, state={e.State}");
        };
    }

    private static bool IsTrustedDshOrigin(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        return parsed.Scheme == Uri.UriSchemeHttp &&
               (parsed.Host == "127.0.0.1" || parsed.Host == "localhost");
    }
}
