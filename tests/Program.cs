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

// LauncherStartupOptions checks
var defaultOpt = LauncherStartupOptions.Parse(Array.Empty<string>());
Check(defaultOpt.ModeOverride == null && defaultOpt.TargetConnectionName == null, "parse empty arguments");

var localOpt = LauncherStartupOptions.Parse(new[] { "--local" });
Check(localOpt.ModeOverride == LauncherStartupOptions.ModeLocal, "parse --local argument");

var pickerOpt = LauncherStartupOptions.Parse(new[] { "--picker" });
Check(pickerOpt.ModeOverride == LauncherStartupOptions.ModePicker, "parse --picker argument");

var trayOpt = LauncherStartupOptions.Parse(new[] { "--tray" });
Check(trayOpt.ModeOverride == LauncherStartupOptions.ModeTray, "parse --tray argument");

var sshOpt = LauncherStartupOptions.Parse(new[] { "--ssh", "Server Alpha" });
Check(sshOpt.ModeOverride == LauncherStartupOptions.ModeSsh && sshOpt.TargetConnectionName == "Server Alpha", "parse --ssh <name> argument");

var sshEqOpt = LauncherStartupOptions.Parse(new[] { "--ssh=Server Beta" });
Check(sshEqOpt.ModeOverride == LauncherStartupOptions.ModeSsh && sshEqOpt.TargetConnectionName == "Server Beta", "parse --ssh=<name> argument");

var remoteOpt = LauncherStartupOptions.Parse(new[] { "--remote", "Server Gamma" });
Check(remoteOpt.ModeOverride == LauncherStartupOptions.ModeSsh && remoteOpt.TargetConnectionName == "Server Gamma", "parse --remote <name> argument");

// Shortcut defaults and persisted overrides must agree in every window.
Check(LauncherShortcuts.Matches(null, "connections", LauncherShortcuts.Control | LauncherShortcuts.Shift | '7'), "connection picker defaults to Ctrl+Shift+7");
Check(!LauncherShortcuts.Matches(null, "connections", LauncherShortcuts.Control | LauncherShortcuts.Shift | 'C'), "terminal copy is not intercepted by default");
var shortcuts = new Dictionary<string, string> { ["connections"] = "Ctrl+Alt+F2", ["logs"] = "" };
Check(LauncherShortcuts.Validate(shortcuts, out _), "valid custom and disabled shortcuts");
Check(LauncherShortcuts.Matches(shortcuts, "connections", LauncherShortcuts.Control | LauncherShortcuts.Alt | 0x71), "custom shortcut matches exact modifiers");
Check(!LauncherShortcuts.Matches(shortcuts, "connections", LauncherShortcuts.Control | LauncherShortcuts.Alt | LauncherShortcuts.Shift | 0x71), "additional modifiers do not match");
Check(!LauncherShortcuts.Matches(shortcuts, "logs", LauncherShortcuts.Control | LauncherShortcuts.Shift | '2'), "disabled shortcut does not match");
shortcuts["logs"] = "Ctrl+Alt+F2";
Check(!LauncherShortcuts.Validate(shortcuts, out _), "duplicate shortcuts rejected");
shortcuts["logs"] = "Ctrl+Bogus+X";
Check(!LauncherShortcuts.Validate(shortcuts, out _), "invalid shortcuts rejected");

var expectedDefaults = new Dictionary<string, char> {
    ["connections"] = '7', ["restart"] = '1', ["logs"] = '2', ["plugins"] = '3',
    ["settings"] = '4', ["manager"] = '5', ["quit"] = '6', ["sync"] = '8', ["folder"] = '9',
};
Check(LauncherShortcuts.Validate(null, out _), "numeric default shortcuts are unique");
Check(LauncherShortcuts.Actions.Length == expectedDefaults.Count, "all actions have expected numeric defaults");
foreach (var (id, digit) in expectedDefaults)
    Check(LauncherShortcuts.Matches(null, id, LauncherShortcuts.Control | LauncherShortcuts.Shift | digit), $"{id} defaults to Ctrl+Shift+{digit}");
foreach (var letter in "CRLPSMQYO")
    Check(LauncherShortcuts.Actions.All(a => !LauncherShortcuts.Matches(null, a.Id, LauncherShortcuts.Control | LauncherShortcuts.Shift | letter)), $"Ctrl+Shift+{letter} is free by default");

Check(LauncherShortcuts.Actions.Select((action, index) => action.Default == $"Ctrl+Shift+{index + 1}").All(x => x), "settings list defaults follow numerical order");
