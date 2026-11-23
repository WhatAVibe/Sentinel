using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sentinel.Installer;

internal sealed class SilentOptions
{
    public bool Silent { get; private set; }
    public string Relay { get; private set; } = "";
    public string Recovery { get; private set; } = "";
    public string? Directory { get; private set; }
    public string? ConfirmCode { get; private set; }
    public string? ConfirmFile { get; private set; }
    public bool AutoConfirm { get; private set; }
    public bool StartWithWindows { get; private set; } = true;
    public string Report { get; private set; } =
        Path.Combine(Path.GetTempPath(), "sentinel-install.json");

    public static SilentOptions Parse(string[] args)
    {
        var options = new SilentOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : "";
            if (string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase)) options.Silent = true;
            else if (string.Equals(arg, "--relay", StringComparison.OrdinalIgnoreCase)) options.Relay = Next();
            else if (string.Equals(arg, "--recovery", StringComparison.OrdinalIgnoreCase)) options.Recovery = Next();
            else if (string.Equals(arg, "--dir", StringComparison.OrdinalIgnoreCase)) options.Directory = Next();
            else if (string.Equals(arg, "--confirm-code", StringComparison.OrdinalIgnoreCase)) options.ConfirmCode = Next();
            else if (string.Equals(arg, "--confirm-file", StringComparison.OrdinalIgnoreCase)) options.ConfirmFile = Next();
            else if (string.Equals(arg, "--auto-confirm", StringComparison.OrdinalIgnoreCase)) options.AutoConfirm = true;
            else if (string.Equals(arg, "--no-startup", StringComparison.OrdinalIgnoreCase)) options.StartWithWindows = false;
            else if (string.Equals(arg, "--report", StringComparison.OrdinalIgnoreCase)) options.Report = Next();
        }
        return options;
    }
}

internal static class SilentInstall
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = SilentOptions.Parse(args);
        var report = new Dictionary<string, object?>
        {
            ["version"] = InstallerCore.ProductVersion,
            ["machine"] = Environment.MachineName,
        };
        try
        {
            if (!Uri.TryCreate(Normalize(options.Relay), UriKind.Absolute, out var relay))
                throw new InvalidOperationException("--relay must be a valid http(s) URL");
            if (options.Recovery.Length < 8)
                throw new InvalidOperationException("--recovery requires at least 8 characters");

            var dir = options.Directory ?? InstallerCore.FindInstalled() ?? InstallerCore.DefaultInstallDir;
            var dataKey = InstallerCore.NewDataKey();
            report["install_dir"] = dir;
            report["relay"] = relay.ToString();

            var response = await InstallerCore.PairRequestAsync(relay.ToString().TrimEnd('/'), Environment.MachineName);
            var requestId = response.TryGetProperty("request_id", out var rid) ? rid.GetString() ?? "" : "";
            var requestToken = response.TryGetProperty("request_token", out var rtoken) ? rtoken.GetString() ?? "" : "";
            var pairCode = response.TryGetProperty("pair_code", out var code) ? code.GetString() ?? "" : "";
            report["pair_code"] = pairCode;
            report["status"] = "waiting_for_phone";
            WriteReport(options.Report, report);

            JsonElement status = default;
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(2000).ConfigureAwait(false);
                status = await InstallerCore.PairStatusAsync(relay.ToString().TrimEnd('/'), requestId, requestToken);
                var state = status.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                if (state == "approved") break;
                if (state is "denied" or "expired" or "claimed")
                    throw new InvalidOperationException($"pairing {state}");
            }

            var agentToken = status.TryGetProperty("agent_token", out var at) ? at.GetString() ?? "" : "";
            var totpSecret = status.TryGetProperty("totp_secret", out var ts) ? ts.GetString() ?? "" : "";
            var deviceId = status.TryGetProperty("device_id", out var did) ? did.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(agentToken) || string.IsNullOrEmpty(totpSecret))
                throw new InvalidOperationException("pairing was not approved in time");

            var confirm = options.ConfirmCode;
            if (string.IsNullOrEmpty(confirm) && !string.IsNullOrEmpty(options.ConfirmFile))
            {
                var waitUntil = DateTime.UtcNow.AddMinutes(5);
                while (DateTime.UtcNow < waitUntil && string.IsNullOrEmpty(confirm))
                {
                    try { confirm = File.ReadAllText(options.ConfirmFile).Trim(); } catch { }
                    if (confirm is { Length: 6 }) break;
                    confirm = null;
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
            if (string.IsNullOrEmpty(confirm) && options.AutoConfirm) confirm = TotpNow(totpSecret);

            InstallerCore.StopAgent();
            InstallerCore.InstallPayload(dir);
            InstallerCore.WriteConfig(
                dir,
                relay.ToString().TrimEnd('/'),
                deviceId,
                agentToken,
                totpSecret,
                options.Recovery,
                dataKey,
                checkForUpdates: true,
                startWithWindows: options.StartWithWindows,
                tutorialDone: true);
            InstallerCore.ConfigureStartup(dir, options.StartWithWindows);
            InstallerCore.RegisterUninstall(dir);

            if (!string.IsNullOrEmpty(confirm))
            {
                var baseUrl = relay.ToString().TrimEnd('/');
                var phones = await InstallerCore.PhonesAsync(baseUrl, agentToken);
                var pending = phones.FirstOrDefault(p => !p.Confirmed);
                if (pending != null)
                {
                    await InstallerCore.ConfirmPhoneAsync(baseUrl, agentToken, pending.Id, confirm);
                    report["confirmed"] = true;
                }
            }
            else
            {
                report["confirmed"] = false;
                report["pending_confirmation"] = true;
            }

            InstallerCore.LaunchAgent(dir);
            report["ok"] = true;
            report["status"] = "installed";
            report["device_id"] = deviceId;
            WriteReport(options.Report, report);
            return 0;
        }
        catch (Exception ex)
        {
            report["ok"] = false;
            report["status"] = "failed";
            report["error"] = ex.Message;
            WriteReport(options.Report, report);
            return 1;
        }
    }

    private static string Normalize(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return text;
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;
        return text.TrimEnd('/');
    }

    private static void WriteReport(string path, Dictionary<string, object?> report)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // a report failure must not fail the install
        }
    }

    private static string TotpNow(string secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = secret.ToUpperInvariant().Where(c => alphabet.Contains(c)).ToArray();
        var bytes = new List<byte>(clean.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var character in clean)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var message = new byte[8];
        for (var index = 7; index >= 0; index--)
        {
            message[index] = (byte)(counter & 0xFF);
            counter >>= 8;
        }
        using var hmac = new HMACSHA1(bytes.ToArray());
        var hash = hmac.ComputeHash(message);
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) |
                    (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6");
    }
}
