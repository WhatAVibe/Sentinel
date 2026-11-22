using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Sentinel.Services;

public static class Watchdog
{
    private const int SM_SHUTTINGDOWN = 0x2000;
    private const string AgentMutexName = @"Local\Sentinel.Instance";

    public static void Run(int agentPid)
    {
        using var mutex = new Mutex(true, @"Local\Sentinel.Watchdog", out var created);
        if (!created) return;

        var logs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sentinel",
            "logs");
        var log = new ActivityLog(logs);
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            log.Write("error", "supervisor cannot resolve its own path");
            return;
        }

        log.Write("info", "supervisor armed", new { pid = agentPid, detached = true });
        var agentWasRunning = true;
        while (true)
        {
            if (GetSystemMetrics(SM_SHUTTINGDOWN) != 0)
            {
                log.Write("info", "system is shutting down - supervisor stops");
                break;
            }

            var running = IsAgentRunning();
            if (running)
            {
                if (!agentWasRunning) log.Write("info", "agent is back online");
                agentWasRunning = true;
                Thread.Sleep(1500);
                continue;
            }

            if (agentWasRunning) log.Write("warning", "agent is not running");
            agentWasRunning = false;

            if (ExitGuard.Consume())
            {
                log.Write("info", "authorized exit accepted - supervisor stops");
                break;
            }

            try
            {
                var started = StartAgent(exe);
                if (started != null)
                {
                    log.Write("warning", "agent exited without authorization - restarting", new { pid = started.Id });
                    agentWasRunning = true;
                }
            }
            catch (Exception ex)
            {
                log.Write("error", "supervisor restart failed", ex.Message);
            }
            Thread.Sleep(2000);
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch
        {
            // ignored
        }
    }

    private static bool IsAgentRunning()
    {
        try
        {
            using var existing = Mutex.OpenExisting(AgentMutexName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Process? StartAgent(string exe) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--tray",
            UseShellExecute = false,
            CreateNoWindow = true
        });

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
