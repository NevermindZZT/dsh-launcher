using System.Net;
using DshLauncher;

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
}

Check(
    DshStartupUrl.HasToken("http://127.0.0.1:3080/?token=abc_DEF-123"),
    "accept a valid process startup token");
Check(
    DshStartupUrl.HasToken("http://127.0.0.1:3080/?token=abc%2D123"),
    "accept an encoded startup token");
Check(
    DshStartupUrl.TryExtractToken("abc_DEF-123", out var rawToken) && rawToken == "abc_DEF-123",
    "accept a raw token entered by the user");
Check(
    DshStartupUrl.TryExtractToken("http://127.0.0.1:3080/?token=abc_DEF-123", out var pastedUrlToken) && pastedUrlToken == "abc_DEF-123",
    "extract a token from a pasted startup URL");
Check(
    DshStartupUrl.TryExtractToken("dsh web: http://127.0.0.1:3080/?token=abc_DEF-123 (LAN: 192.0.2.1)", out var pastedLogToken) && pastedLogToken == "abc_DEF-123",
    "extract a token from a copied dsh web log line");
Check(
    DshStartupUrl.WithToken("http://127.0.0.1:3080/?old=value", "abc_DEF-123") == "http://127.0.0.1:3080/?token=abc_DEF-123",
    "replace stale query data when composing a token URL");
Check(
    !DshStartupUrl.TryExtractToken("http://127.0.0.1:3080/?token=a&token=b", out _),
    "reject ambiguous pasted startup URLs");
Check(
    !DshStartupUrl.HasToken("http://127.0.0.1:3080/?token="),
    "reject an empty startup token");
Check(
    !DshStartupUrl.HasToken("http://127.0.0.1:3080/?token=a&token=b"),
    "reject duplicate token parameters");
Check(
    !DshStartupUrl.HasToken("http://127.0.0.1:3080/?Token=abc"),
    "match DSH's exact lowercase token parameter");
Check(
    DshStartupUrl.IsDshResponse(
        HttpStatusCode.Unauthorized,
        "dsh web authentication required; reopen the URL printed by dsh web."),
    "identify DSH's token-protected response");
Check(
    !DshStartupUrl.IsDshResponse(HttpStatusCode.Unauthorized, "unrelated app"),
    "reject unrelated 401 responses");
Check(
    DshStartupUrl.IsDshResponse(HttpStatusCode.OK, "<title>DeepSeek Harness</title>"),
    "identify the DSH index response");
Check(
    !DshStartupUrl.IsDshResponse(HttpStatusCode.OK, "unrelated app"),
    "reject unrelated 200 responses");
Check(
    DshStartupUrl.Redact("http://127.0.0.1:3080/?token=abc")
        == "http://127.0.0.1:3080/?[redacted]",
    "redact token query strings");

// Mouse back/forward must be consumed as handled input, never re-dispatched to a host window.
foreach (var messageId in new[] { 0x020B, 0x020C, 0x020D })
foreach (var button in new[] { 1, 2 })
{
    var wParam = new IntPtr((long)button << 16);
    var handled = BrowserMouseNavigationGuard.TryHandle(messageId, wParam, IntPtr.Zero, out var result);
    Check(handled && result == new IntPtr(1), $"consume XBUTTON{button} message 0x{messageId:X} as handled");
}
foreach (var command in new[] { 1, 2 })
{
    var lParam = new IntPtr((long)command << 16);
    var handled = BrowserMouseNavigationGuard.TryHandle(0x0319, IntPtr.Zero, lParam, out var result);
    Check(handled && result == new IntPtr(1), $"consume browser app-command {command} as handled");
}
foreach (var messageId in new[] { 0x0100, 0x0101, 0x0104, 0x0105 })
foreach (var virtualKey in new[] { 0xA6, 0xA7 })
{
    var handled = BrowserMouseNavigationGuard.TryHandle(messageId, new IntPtr(virtualKey), IntPtr.Zero, out var result);
    Check(handled && result == IntPtr.Zero, $"consume browser navigation key 0x{virtualKey:X} message 0x{messageId:X}");
}
Check(BrowserMouseNavigationGuard.IsBrowserNavigationKey(0xA6), "identify CoreWebView2 browser-back accelerator");
Check(BrowserMouseNavigationGuard.IsBrowserNavigationKey(0xA7), "identify CoreWebView2 browser-forward accelerator");
Check(!BrowserMouseNavigationGuard.IsBrowserNavigationKey(0x25), "do not classify ordinary left-arrow as browser navigation");
Check(
    !BrowserMouseNavigationGuard.TryHandle(0x0201, new IntPtr(1), IntPtr.Zero, out _),
    "leave ordinary left-button input untouched");
Check(
    !BrowserMouseNavigationGuard.TryHandle(0x0319, IntPtr.Zero, new IntPtr(3L << 16), out _),
    "leave unrelated app-commands untouched");
Check(
    !BrowserMouseNavigationGuard.TryHandle(0x0100, new IntPtr(0x25), IntPtr.Zero, out _),
    "leave ordinary arrow keys untouched");
