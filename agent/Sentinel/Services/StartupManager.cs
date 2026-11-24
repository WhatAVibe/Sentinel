using System.IO;
using Microsoft.Win32;

namespace Sentinel.Services;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "SentinelAgent";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path)) return;
                key.SetValue(Name, $"\"{path}\" --tray");
            }
            else
            {
                key.DeleteValue(Name, false);
            }
            CleanupLegacy(key);
        }
        catch
        {
            // ignored
        }
    }

    public static void CleanupLegacy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key != null) CleanupLegacy(key);
        }
        catch
        {
            // ignored
        }
    }

    private static void CleanupLegacy(RegistryKey key)
    {
        foreach (var name in new[] { "SentinelSupervisor", "Sentinel", "PCMonitorAgent", "PCMonitorSupervisor" })
        {
            try { key.DeleteValue(name, false); } catch { }
        }
    }
}
