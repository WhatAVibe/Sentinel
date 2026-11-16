using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCMonitor.Agent.Services;

public static class ExitGuard
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PCMonitor.Exit.v1");

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCMonitor",
        "authorized-exit.dat");

    public static void Authorize()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var payload = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                nonce = Guid.NewGuid().ToString("N")
            });
            var raw = ProtectedData.Protect(Encoding.UTF8.GetBytes(payload), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, raw);
        }
        catch
        {
            // watchdog will simply restart; never throw from exit handling
        }
    }

    public static bool IsAuthorized()
    {
        try
        {
            if (!File.Exists(FilePath)) return false;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            using var document = JsonDocument.Parse(raw);
            var timestamp = document.RootElement.GetProperty("ts").GetInt64();
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp < 120_000;
        }
        catch
        {
            return false;
        }
    }

    public static bool Consume()
    {
        var authorized = IsAuthorized();
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // ignored
        }
        return authorized;
    }
}
