using System.Security.Cryptography;
using System.Text;

namespace Sentinel.Services;

public static class ClipboardPrivacy
{
    private static string _hash = "";
    private static long _untilMs;

    public static void Suppress(string text)
    {
        _hash = Hash(text);
        _untilMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000;
    }

    public static bool ShouldSkip(string text)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > _untilMs) return false;
        return Hash(text) == _hash;
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
