using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCMonitor.Agent.Services;

public sealed class AppConfig
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = Environment.MachineName;
    public string ServerUrl { get; set; } = "";
    public string ProtectedAgentToken { get; set; } = "";
    public string ProtectedTotpSecret { get; set; } = "";
    public string ProtectedLocalToken { get; set; } = "";
    public string ProtectedSecurityKey { get; set; } = "";

    public string PowerActionMode { get; set; } = "confirm";
    public string PowerActionMessage { get; set; } = "Your phone asked to {action} this PC.";
    public int MessagePopupSeconds { get; set; } = 10;

    public int HeartbeatSeconds { get; set; } = 10;
    public int EventFlushSeconds { get; set; } = 6;
    public int AwayShotSeconds { get; set; } = 120;
    public int AwayShotMinSeconds { get; set; } = 45;
    public int AwayShotMaxSeconds { get; set; } = 180;
    public int LockAfterAwaySeconds { get; set; } = 0;
    public int AutoAwayIdleMinutes { get; set; } = 10;

    public bool AwayScreensaver { get; set; } = true;
    public bool MonitorOnlyInAway { get; set; } = true;
    public bool CaptureClipboard { get; set; } = true;
    public bool CaptureUsb { get; set; } = true;
    public bool CaptureProcesses { get; set; } = true;
    public bool CaptureKeystrokes { get; set; }
    public bool CaptureMouse { get; set; } = true;
    public bool CaptureNetwork { get; set; } = true;
    public bool DetectSearches { get; set; } = true;
    public bool AutoAwayOnIdle { get; set; }
    public bool AutoAwayOnPhoneDisconnect { get; set; }
    public bool AutoScreenshotInAway { get; set; } = true;
    public bool AwayShotOnActivity { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool BalloonNotifications { get; set; } = true;
    public bool RequirePhonePresence { get; set; }
    public bool AdbEnabled { get; set; }
    public bool ProtectPowerCommands { get; set; }
    public bool AllowInsecureTls { get; set; }
    public string AdbPath { get; set; } = "";
    public int LocalPort { get; set; } = 8790;

    public List<string> TrustedAdbSerials { get; set; } = new();

    [JsonIgnore] public string AgentToken => Secrets.Unprotect(ProtectedAgentToken);
    [JsonIgnore] public string TotpSecret => Secrets.Unprotect(ProtectedTotpSecret);
    [JsonIgnore] public string LocalToken => Secrets.Unprotect(ProtectedLocalToken);
    [JsonIgnore] public bool Paired => !string.IsNullOrWhiteSpace(ProtectedAgentToken) && !string.IsNullOrWhiteSpace(ServerUrl);
}

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly object _lock = new();
    public string Root { get; }
    public string ConfigPath { get; }
    public string ShotsDir { get; }
    public string LogsDir { get; }

    public ConfigStore()
    {
        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMonitor");
        ConfigPath = Path.Combine(Root, "config.json");
        ShotsDir = Path.Combine(Root, "shots");
        LogsDir = Path.Combine(Root, "logs");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ShotsDir);
        Directory.CreateDirectory(LogsDir);
    }

    public AppConfig Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                    if (cfg != null)
                    {
                        if (string.IsNullOrWhiteSpace(cfg.DeviceId)) cfg.DeviceId = Guid.NewGuid().ToString("N");
                        if (string.IsNullOrWhiteSpace(cfg.DeviceName)) cfg.DeviceName = Environment.MachineName;
                        if (string.IsNullOrWhiteSpace(cfg.ProtectedLocalToken))
                            cfg.ProtectedLocalToken = Secrets.Protect(TokenGenerator.New());
                        return cfg;
                    }
                }
            }
            catch
            {
                // corrupted config is replaced with defaults
            }

            var fresh = new AppConfig
            {
                DeviceId = Guid.NewGuid().ToString("N"),
                ProtectedLocalToken = Secrets.Protect(TokenGenerator.New())
            };
            Save(fresh);
            return fresh;
        }
    }

    public void Save(AppConfig cfg)
    {
        lock (_lock)
        {
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Options));
            File.Move(tmp, ConfigPath, true);
        }
    }
}

public static class TokenGenerator
{
    public static string New(int bytes = 32)
    {
        var buffer = new byte[bytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    public static string PairCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var buffer = new byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
        var chars = new char[6];
        for (int i = 0; i < 6; i++) chars[i] = alphabet[buffer[i] % alphabet.Length];
        return new string(chars);
    }
}
