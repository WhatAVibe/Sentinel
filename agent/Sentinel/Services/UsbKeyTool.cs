using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Sentinel.Services;

public sealed class UsbKeyDrive
{
    public string Letter { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string State { get; set; } = "";
    public string StateColor { get; set; } = "#8B93A1";
}

public static class UsbKeyTool
{
    public static string? FindExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable("SENTINEL_KEY_EXE");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;
        var candidates = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "sentinel-key.exe"));
        candidates.Add(Path.Combine(baseDir, "usbkey", "sentinel-key.exe"));
        var directory = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < 7 && directory != null; depth++, directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "usbkey", "target", "release", "sentinel-key.exe"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<(bool Ok, string Output)> RunAsync(params string[] arguments)
    {
        var exe = FindExecutable();
        if (exe == null) return (false, "sentinel-key.exe was not found.");
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return (false, "Could not start sentinel-key.exe.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (await stdout).Trim();
            var error = (await stderr).Trim();
            if (!string.IsNullOrWhiteSpace(error))
                output = string.IsNullOrWhiteSpace(output) ? error : output + Environment.NewLine + error;
            return (process.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static async Task<List<UsbKeyDrive>> ListAsync()
    {
        var (ok, output) = await RunAsync("list", "--json");
        if (!ok) return new List<UsbKeyDrive>();
        var drives = new List<UsbKeyDrive>();
        try
        {
            using var document = JsonDocument.Parse(output);
            foreach (var row in document.RootElement.EnumerateArray())
            {
                var state = row.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
                var letter = row.TryGetProperty("letter", out var l) ? l.GetString() ?? "" : "";
                var label = row.TryGetProperty("label", out var n) ? n.GetString() ?? "" : "";
                var size = row.TryGetProperty("size", out var z) ? z.GetString() ?? "" : "";
                var filesystem = row.TryGetProperty("filesystem", out var f) ? f.GetString() ?? "" : "";
                var detail = row.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
                var fingerprint = row.TryGetProperty("fingerprint", out var fp) ? fp.GetString() ?? "" : "";
                drives.Add(new UsbKeyDrive
                {
                    Letter = letter,
                    Title = string.IsNullOrWhiteSpace(label) ? $"Removable drive ({letter}:)" : label,
                    Detail = $"{size} - {filesystem} - {detail}",
                    Fingerprint = fingerprint,
                    State = state switch
                    {
                        "enrolled" => "ENROLLED",
                        "available" => "NOT ENROLLED",
                        "encrypted" => "ENCRYPTED",
                        "foreign" => "OTHER PC",
                        "missing-marker" => "MARKER MISSING",
                        _ => state.ToUpperInvariant()
                    },
                    StateColor = state switch
                    {
                        "enrolled" => "#6EE7A8",
                        "available" => "#8B93A1",
                        "foreign" => "#F2C46D",
                        "encrypted" => "#F2C46D",
                        "missing-marker" => "#FF6B6B",
                        _ => "#8B93A1"
                    }
                });
            }
        }
        catch
        {
            // An empty list is safer than a half-parsed one.
        }
        return drives;
    }

    public static async Task<(bool Ok, string Output)> EnrollDriveAsync(string letter, bool force, bool startup)
    {
        var arguments = new List<string> { "enroll", letter };
        if (force) arguments.Add("--force");
        var (ok, output) = await RunAsync(arguments.ToArray());
        if (!ok) return (false, output);
        if (startup)
        {
            var (startupOk, startupOutput) = await RunAsync("startup", "on");
            if (!startupOk) return (false, output + Environment.NewLine + startupOutput);
        }
        return (true, output);
    }

    public static Task<(bool Ok, string Output)> ForgetAsync(string letter, bool force)
    {
        var arguments = new List<string> { "forget", letter };
        if (force) arguments.Add("--force");
        return RunAsync(arguments.ToArray());
    }

    public static Task<(bool Ok, string Output)> StartupAsync(bool enabled) =>
        RunAsync("startup", enabled ? "on" : "off");

    public static bool StartGuard()
    {
        var exe = FindExecutable();
        if (exe == null) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--guard",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
