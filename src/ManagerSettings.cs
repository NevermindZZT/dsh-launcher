using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DshLauncher;

public sealed class ManagerSettings
{
    public bool Enabled { get; set; }
    public string ServerUrl { get; set; } = "";
    public string AgentName { get; set; } = Environment.MachineName;
    public string AgentId { get; set; } = "";
    public string PairingCode { get; set; } = "";
    public string ServerCertificateFingerprint { get; set; } = "";
    public string? AgentTokenProtected { get; set; }

    [JsonIgnore]
    public string AgentToken
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AgentTokenProtected)) return "";
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(AgentTokenProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) { AgentTokenProtected = null; return; }
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            AgentTokenProtected = Convert.ToBase64String(bytes);
        }
    }
}
