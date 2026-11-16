using System.IO;
using Microsoft.Win32;

namespace PCMonitor.Agent.Services;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PCMonitorAgent";
    private const string WatchdogName = "PCMonitorSupervisor";

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
                key.SetValue(WatchdogName, $"\"{path}\" --supervise");
            }
            else
            {
                key.DeleteValue(Name, false);
                key.DeleteValue(WatchdogName, false);
            }
        }
        catch
        {
            // ignored
        }
    }
}
