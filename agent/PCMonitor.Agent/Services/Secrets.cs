using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PCMonitor.Agent.Services;

public static class Secrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PCMonitor.Agent.v1");

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var raw = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(raw);
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch
        {
            return "";
        }
    }
}
