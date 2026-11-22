using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Sentinel.Models;
using static Sentinel.Services.NativeMethods;

namespace Sentinel.Services;

public static class SystemMetrics
{
    private static ulong _prevIdle;
    private static ulong _prevKernel;
    private static ulong _prevUser;

    public static MetricsDto Collect()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);

        double cpu = 0;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var idleTicks = idle.ToUInt64();
            var kernelTicks = kernel.ToUInt64();
            var userTicks = user.ToUInt64();
            var idleDelta = idleTicks - _prevIdle;
            var kernelDelta = kernelTicks - _prevKernel;
            var userDelta = userTicks - _prevUser;
            var total = kernelDelta + userDelta;
            if (total > 0 && _prevKernel > 0)
            {
                cpu = Math.Round((total - idleDelta) * 100.0 / total, 1);
                cpu = Math.Clamp(cpu, 0, 100);
            }
            _prevIdle = idleTicks;
            _prevKernel = kernelTicks;
            _prevUser = userTicks;
        }

        return new MetricsDto
        {
            CpuPercent = cpu,
            MemUsedMb = (long)((mem.ullTotalPhys - mem.ullAvailPhys) / 1024 / 1024),
            MemTotalMb = (long)(mem.ullTotalPhys / 1024 / 1024),
            UptimeSeconds = (long)(GetTickCount64() / 1000),
            User = Environment.UserName,
            Hostname = Environment.MachineName
        };
    }

    public static int IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        var ticks = (uint)GetTickCount64();
        return (int)((ticks - info.dwTime) / 1000);
    }

    public static bool IsWorkstationLocked()
    {
        var desktop = OpenInputDesktop(0, false, 0x0100);
        if (desktop == IntPtr.Zero) return true;
        try
        {
            var sb = new StringBuilder(256);
            if (GetUserObjectInformation(desktop, 2, sb, sb.Capacity * 2, out _))
                return !string.Equals(sb.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
            return true;
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }
}

public static class PowerActions
{
    public static void Lock() => LockWorkStation();

    public static void Shutdown(int delaySeconds = 10) =>
        Run("shutdown.exe", $"/s /t {delaySeconds} /c \"Sentinel remote shutdown\"");

    public static void Restart(int delaySeconds = 10) =>
        Run("shutdown.exe", $"/r /t {delaySeconds} /c \"Sentinel remote restart\"");

    public static void Logoff() => Run("shutdown.exe", "/l");

    public static void CancelShutdown() => Run("shutdown.exe", "/a");

    public static void Sleep() => SetSuspendState(false, false, false);

    public static void Hibernate() => SetSuspendState(true, false, false);

    public static void ScreenOff() =>
        PostMessage(new IntPtr(0xFFFF), WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2);

    public static void ScreenOn()
    {
        PostMessage(new IntPtr(0xFFFF), WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)(-1));
        keybd_event(0x86, 0, 0, IntPtr.Zero);
        keybd_event(0x86, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
    }

    public static void MediaPlayPause()
    {
        keybd_event(0xB3, 0, 0, IntPtr.Zero);
        keybd_event(0xB3, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
    }

    public static bool TriggerScreensaver(out string detail)
    {
        detail = "";
        try
        {
            using var desk = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var path = desk?.GetValue("SCRNSAVE.EXE") as string;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                detail = "No screensaver configured";
                return false;
            }
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "/s",
                UseShellExecute = true
            });
            detail = $"Screensaver started ({System.IO.Path.GetFileName(path)})";
            return process != null;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    public static ActivityEventData SessionEvent(string title, string category = "session", string severity = "notice") =>
        new() { Category = category, Severity = severity, Title = title };

    private static void Run(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            // ignored, surfaced through command result
        }
    }
}
