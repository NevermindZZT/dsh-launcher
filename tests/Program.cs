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
