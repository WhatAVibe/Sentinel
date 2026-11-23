using System.Security.Cryptography;
using System.Text;

namespace Sentinel.Services;

public static class DataKey
{
    public const int Size = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string AadPrefix = "sentinel:v2:";
    private static readonly string[] Kinds = { "event", "message", "shot" };
    private static readonly object Sync = new();

    public static byte[] GetOrCreate(AppConfig cfg)
    {
        lock (Sync)
        {
            var stored = Secrets.Unprotect(cfg.ProtectedDataKey);
            if (!string.IsNullOrEmpty(stored))
            {
                try
                {
                    var existing = Convert.FromBase64String(stored);
                    if (existing.Length == Size) return existing;
                }
                catch
                {
                    // corrupted key falls through to a fresh one
                }
            }

            var key = RandomNumberGenerator.GetBytes(Size);
            cfg.ProtectedDataKey = Secrets.Protect(Convert.ToBase64String(key));
            return key;
        }
    }

    public static string Base64(AppConfig cfg) => Convert.ToBase64String(GetOrCreate(cfg));

    public static void Clear(AppConfig cfg)
    {
        lock (Sync) cfg.ProtectedDataKey = "";
    }

    public static string Encrypt(AppConfig cfg, string kind, byte[] plaintext) =>
        Encrypt(GetOrCreate(cfg), kind, plaintext);

    public static string Encrypt(byte[] key, string kind, byte[] plaintext)
    {
        var aad = Aad(kind);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        var envelope = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(envelope, 0);
        ciphertext.CopyTo(envelope, NonceSize);
        tag.CopyTo(envelope, NonceSize + ciphertext.Length);
        return Convert.ToBase64String(envelope);
    }

    public static byte[] Decrypt(AppConfig cfg, string kind, string enc) =>
        Decrypt(GetOrCreate(cfg), kind, enc);

    public static byte[] Decrypt(byte[] key, string kind, string enc)
    {
        var aad = Aad(kind);
        var envelope = Convert.FromBase64String(enc);
        if (envelope.Length < NonceSize + TagSize)
            throw new CryptographicException("invalid envelope");
        var ciphertextLength = envelope.Length - NonceSize - TagSize;
        var nonce = envelope.AsSpan(0, NonceSize);
        var ciphertext = envelope.AsSpan(NonceSize, ciphertextLength);
        var tag = envelope.AsSpan(envelope.Length - TagSize, TagSize);
        var plaintext = new byte[ciphertextLength];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        return plaintext;
    }

    private static byte[] Aad(string kind)
    {
        if (Array.IndexOf(Kinds, kind) < 0)
            throw new ArgumentException($"unknown kind '{kind}'", nameof(kind));
        return Encoding.ASCII.GetBytes(AadPrefix + kind);
    }
}
