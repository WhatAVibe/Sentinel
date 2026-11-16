using System.IO;
using System.Text.Json;
using System.Windows;

namespace PCMonitor.Agent.Services;

public static class SecureLock
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCMonitor",
        "secure-lock.json");

    public static bool Active { get; private set; }
    public static string Message { get; private set; } = "";
    public static IntPtr OverlayHandle { get; set; }

    public static void Activate(string message)
    {
        Active = true;
        Message = message ?? "";
        Save();
    }

    public static void Deactivate()
    {
        Active = false;
        Message = "";
        OverlayHandle = IntPtr.Zero;
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // ignored
        }
    }

    public static bool Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;
            Active = root.TryGetProperty("active", out var active) && active.GetBoolean();
            Message = root.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "";
            return Active;
        }
        catch
        {
            return false;
        }
    }

    private static void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new { active = Active, message = Message }));
        }
        catch
        {
            // ignored
        }
    }
}
