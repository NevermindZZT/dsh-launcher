using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DshLauncher;

/// <summary>管理员前端的独立配置。Agent 配对凭据与管理员会话相互隔离。</summary>
public sealed class ManagerFrontendSettings
{
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string? SessionCookieProtected { get; set; }
    public DateTimeOffset? SessionExpiresAt { get; set; }

    [JsonIgnore]
    public string SessionCookie
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SessionCookieProtected)) return "";
            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(SessionCookieProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                SessionCookieProtected = null;
                SessionExpiresAt = null;
                return;
            }

            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            SessionCookieProtected = Convert.ToBase64String(bytes);
        }
    }

    public void ClearSession()
    {
        SessionCookieProtected = null;
        SessionExpiresAt = null;
    }
}
