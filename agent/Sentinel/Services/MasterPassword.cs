using System.Security.Cryptography;
using System.Text;

namespace Sentinel.Services;

public static class MasterPassword
{
    private const int Iterations = 600_000;
    private const int LegacyIterations = 120_000;

    public static bool IsSet(AppConfig config) => !string.IsNullOrWhiteSpace(config.ProtectedMasterPassword);

    public static void Set(AppConfig config, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        config.ProtectedMasterPassword = Secrets.Protect(
            $"{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}");
    }

    public static bool Verify(AppConfig config, string? password)
    {
        if (!IsSet(config) || string.IsNullOrWhiteSpace(password)) return false;
        try
        {
            if (!TryParse(config, out var iterations, out var salt, out var expected)) return false;
            var test = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(expected, test);
        }
        catch
        {
            return false;
        }
    }

    public static bool NeedsUpgrade(AppConfig config)
    {
        if (!IsSet(config)) return false;
        try
        {
            return !TryParse(config, out var iterations, out _, out _) || iterations < Iterations;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParse(AppConfig config, out int iterations, out byte[] salt, out byte[] expected)
    {
        iterations = 0;
        salt = Array.Empty<byte>();
        expected = Array.Empty<byte>();
        var parts = Secrets.Unprotect(config.ProtectedMasterPassword).Split(':');
        if (parts.Length == 3 && int.TryParse(parts[0], out var stored) && stored >= 10_000)
        {
            iterations = stored;
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
            return true;
        }
        if (parts.Length == 2)
        {
            iterations = LegacyIterations;
            salt = Convert.FromBase64String(parts[0]);
            expected = Convert.FromBase64String(parts[1]);
            return true;
        }
        return false;
    }
}
