using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Sentinel.Services;

public static class UsbKeyGuard
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "SentinelKeyGuard";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void TryStart()
    {
        if (!IsEnabled()) return;
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "sentinel-key.exe");
            if (!File.Exists(exe)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--guard",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            // The guard is optional; the agent keeps working without it.
        }
    }
}
