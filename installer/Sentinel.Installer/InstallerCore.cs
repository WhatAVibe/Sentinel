using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Sentinel.Installer;

public sealed class InstallResult
{
    public string DeviceId { get; set; } = "";
    public string AgentToken { get; set; } = "";
    public string TotpSecret { get; set; } = "";
    public string DeviceName { get; set; } = "";
}

public sealed class PhoneEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Confirmed { get; set; }
}

public static class InstallerCore
{
    public const string ProductVersion = "2.1.1";
    public const string Repository = "https://github.com/WhatAVibe/Sentinel";
    public const string ReleasesUrl = Repository + "/releases/latest";
    public const string RelayGuideUrl = Repository + "/blob/main/docs/SETUP.md";
    public const string ContactEmail = "admin@whatavibe.net";
    public const string RunValueName = "SentinelAgent";
    public const string SupervisorValueName = "SentinelSupervisor";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Sentinel";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Sentinel.v1");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sentinel");

    public static string DataDir => DefaultInstallDir;

    public static string AgentExe(string dir) => Path.Combine(dir, "Sentinel.exe");

    public static string ConfigPath(string dir) => Path.Combine(dir, "config.json");

    public static string? FindInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            var location = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(location) && File.Exists(AgentExe(location))) return location;
        }
        catch
        {
            // ignored
        }
        return File.Exists(AgentExe(DefaultInstallDir)) ? DefaultInstallDir : null;
    }

    public static bool HasConfig(string dir) => File.Exists(ConfigPath(dir));

    public static bool ReadConfigBool(string dir, string key, bool fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath(dir)));
            return document.RootElement.TryGetProperty(key, out var value) ? value.GetBoolean() : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(RunValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void InstallPayload(string dir)
    {
        Directory.CreateDirectory(dir);
        var resource = typeof(InstallerCore).Assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
        if (resource == null)
            throw new InvalidOperationException("Installer payload is missing. Build the installer with build.ps1.");

        using var stream = typeof(InstallerCore).Assembly.GetManifestResourceStream(resource)!;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var target = Path.Combine(dir, entry.FullName);
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            entry.ExtractToFile(target, true);
        }
    }

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var raw = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(raw);
    }

    private static string Unprotect(string value)
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

    public static string ProtectMasterPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return "";
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return Protect($"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}");
    }

    public static void WriteConfig(
        string dir,
        string serverUrl,
        string deviceId,
        string agentToken,
        string totpSecret,
        string masterPassword,
        bool checkForUpdates,
        bool startWithWindows,
        bool tutorialDone)
    {
        var config = new Dictionary<string, object?>
        {
            ["DeviceId"] = deviceId,
            ["DeviceName"] = Environment.MachineName,
            ["ServerUrl"] = serverUrl,
            ["ProtectedAgentToken"] = Protect(agentToken),
            ["ProtectedTotpSecret"] = Protect(totpSecret),
            ["ProtectedLocalToken"] = "",
            ["ProtectedSecurityKey"] = "",
            ["ProtectedMasterPassword"] = ProtectMasterPassword(masterPassword),
            ["CheckForUpdates"] = checkForUpdates,
            ["StartWithWindows"] = startWithWindows,
            ["TutorialDone"] = tutorialDone
        };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath(dir), json);
    }

    public static (string ServerUrl, string Token) ReadConfig(string dir)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath(dir)));
            var root = document.RootElement;
            var url = root.TryGetProperty("ServerUrl", out var u) ? u.GetString() ?? "" : "";
            var token = root.TryGetProperty("ProtectedAgentToken", out var t) ? Unprotect(t.GetString() ?? "") : "";
            return (url, token);
        }
        catch
        {
            return ("", "");
        }
    }

    public static void ConfigureStartup(string dir, bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;
        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{AgentExe(dir)}\" --tray");
            key.SetValue(SupervisorValueName, $"\"{AgentExe(dir)}\" --supervise");
        }
        else
        {
            key.DeleteValue(RunValueName, false);
            key.DeleteValue(SupervisorValueName, false);
        }
    }

    public static void RegisterUninstall(string dir)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey)!;
        key.SetValue("DisplayName", "Sentinel");
        key.SetValue("DisplayVersion", ProductVersion);
        key.SetValue("Publisher", "WhatAVibe");
        key.SetValue("InstallLocation", dir);
        key.SetValue("DisplayIcon", AgentExe(dir));
        key.SetValue("UninstallString", $"\"{AgentExe(dir)}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public static void RemoveUninstall()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue(RunValueName, false);
            key?.DeleteValue(SupervisorValueName, false);
            key?.DeleteValue("Sentinel", false);
        }
        catch
        {
            // ignored
        }
    }

    public static void CopySelfIntoInstall(string dir)
    {
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self)) return;
            var target = Path.Combine(dir, "Sentinel Setup.exe");
            if (!string.Equals(self, target, StringComparison.OrdinalIgnoreCase))
                File.Copy(self, target, true);
        }
        catch
        {
            // uninstaller copy is best-effort
        }
    }

    public static void StopAgent()
    {
        foreach (var process in Process.GetProcessesByName("Sentinel"))
        {
            try { process.Kill(true); } catch { }
        }
    }

    public static void LaunchAgent(string dir)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = AgentExe(dir),
            WorkingDirectory = dir,
            UseShellExecute = true
        });
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }

    // ------------------------------------------------------------------ pairing

    public static async Task<JsonElement> PairRequestAsync(string baseUrl, string deviceName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/api/v1/pair/request");
        request.Headers.UserAgent.ParseAdd($"SentinelSetup/{ProductVersion}");
        request.Content = JsonContent(new
        {
            device_name = deviceName,
            platform = "windows",
            fingerprint = "sentinel-installer",
            agent_version = ProductVersion
        });
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ErrorCode(body) ?? $"http_{(int)response.StatusCode}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    public static async Task<JsonElement> PairStatusAsync(string baseUrl, string requestId, string token)
    {
        var url = baseUrl.TrimEnd('/') + "/api/v1/pair/status?request_id=" + Uri.EscapeDataString(requestId) +
                  "&token=" + Uri.EscapeDataString(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"SentinelSetup/{ProductVersion}");
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ErrorCode(body) ?? $"http_{(int)response.StatusCode}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    public static async Task<List<PhoneEntry>> PhonesAsync(string baseUrl, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/api/v1/agent/phones");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd($"SentinelSetup/{ProductVersion}");
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ErrorCode(body) ?? $"http_{(int)response.StatusCode}");
        using var document = JsonDocument.Parse(body);
        var list = new List<PhoneEntry>();
        if (document.RootElement.TryGetProperty("phones", out var phones))
        {
            foreach (var phone in phones.EnumerateArray())
            {
                list.Add(new PhoneEntry
                {
                    Id = phone.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Name = phone.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    Confirmed = !phone.TryGetProperty("confirmed", out var c) || c.GetBoolean()
                });
            }
        }
        return list;
    }

    public static async Task ConfirmPhoneAsync(string baseUrl, string token, string phoneId, string code)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, baseUrl.TrimEnd('/') + $"/api/v1/agent/phones/{phoneId}/confirm");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd($"SentinelSetup/{ProductVersion}");
        request.Content = JsonContent(new { code });
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ErrorCode(body) ?? $"http_{(int)response.StatusCode}");
    }

    public static async Task PurgeRelayAsync(string baseUrl, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, baseUrl.TrimEnd('/') + "/api/v1/agent/device");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd($"SentinelSetup/{ProductVersion}");
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"http_{(int)response.StatusCode}");
    }

    private static HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string? ErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
