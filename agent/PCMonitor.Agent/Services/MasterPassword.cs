using System.Security.Cryptography;
using System.Text;

namespace PCMonitor.Agent.Services;

public static class MasterPassword
{
    private const int Iterations = 120_000;

    public static bool IsSet(AppConfig config) => !string.IsNullOrWhiteSpace(config.ProtectedMasterPassword);

    public static void Set(AppConfig config, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        config.ProtectedMasterPassword = Secrets.Protect($"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}");
    }

    public static void Clear(AppConfig config) => config.ProtectedMasterPassword = "";

    public static bool Verify(AppConfig config, string? password)
    {
        if (!IsSet(config) || string.IsNullOrWhiteSpace(password)) return false;
        try
        {
            var parts = Secrets.Unprotect(config.ProtectedMasterPassword).Split(':');
            if (parts.Length != 2) return false;
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var test = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(expected, test);
        }
        catch
        {
            return false;
        }
    }
}
