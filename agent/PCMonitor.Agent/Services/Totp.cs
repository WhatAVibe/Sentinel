using System.Security.Cryptography;
using System.Text;

namespace PCMonitor.Agent.Services;

public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public const int Step = 30;

    public static string Now(string secret, int step = Step, int digits = 6)
    {
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / step;
        return Compute(secret, counter, digits);
    }

    public static string Compute(string secret, long counter, int digits = 6)
    {
        var key = Base32Decode(secret);
        if (key.Length == 0) return "";
        var msg = new byte[8];
        for (int i = 7; i >= 0; i--)
        {
            msg[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(msg);
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % (int)Math.Pow(10, digits)).ToString(new string('0', digits));
    }

    public static bool Verify(string secret, string? code, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code)) return false;
        code = code.Trim().Replace(" ", "");
        if (code.Length != 6 || !code.All(char.IsDigit)) return false;
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Step;
        for (var drift = -window; drift <= window; drift++)
        {
            if (string.Equals(Compute(secret, counter + drift), code, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static int SecondsRemaining(int step = Step) =>
        (int)(step - DateTimeOffset.UtcNow.ToUnixTimeSeconds() % step);

    private static byte[] Base32Decode(string input)
    {
        input = input.Trim().Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(input.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var c in input)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) continue;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xFF));
            }
        }
        return output.ToArray();
    }
}
