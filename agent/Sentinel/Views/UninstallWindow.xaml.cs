using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Sentinel.Services;

namespace Sentinel.Views;

public partial class UninstallWindow : Window
{
    public UninstallWindow()
    {
        InitializeComponent();
        PathText.Text = $"Install folder: {App.Host.Store.Root}";
        Closed += (_, _) => Application.Current.Shutdown();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
        Application.Current.Shutdown();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        UninstallButton.IsEnabled = false;
        StatusText.Text = "Stopping Sentinel…";
        foreach (var process in Process.GetProcessesByName("Sentinel"))
        {
            if (process.Id == Environment.ProcessId) continue;
            try { process.Kill(true); } catch { }
        }

        if (UnpairCheck.IsChecked == true && App.Host.Config.Paired)
        {
            StatusText.Text = "Removing this PC from the relay…";
            try
            {
                await App.Host.Api.PurgeDeviceAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Relay purge failed (" + ex.Message + "); continuing locally.";
            }
        }

        StatusText.Text = "Removing startup and registry entries…";
        try { StartupManager.Set(false); } catch { }
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Sentinel", false);
        }
        catch
        {
            // ignored
        }

        var installDir = App.Host.Store.Root;
        StatusText.Text = "Closing…";
        await Task.Delay(300);
        Application.Current.Shutdown();
        ScheduleDelete(installDir);
    }

    private static void ScheduleDelete(string dir)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var temp = Path.GetTempPath();
            var targets = new[]
            {
                dir,
                Path.Combine(appData, "PCMonitor"),
                Path.Combine(localAppData, "Sentinel"),
                Path.Combine(localAppData, "PCMonitor"),
                Path.Combine(temp, "sentinel-webauthn")
            };
            var removals = new List<string> { "ping -n 3 127.0.0.1 >nul" };
            foreach (var target in targets)
            {
                if (Directory.Exists(target))
                    removals.Add($"rmdir /s /q \"{target}\"");
            }
            var keyLog = Path.Combine(temp, "sentinel-key.log");
            if (File.Exists(keyLog)) removals.Add($"del /f /q \"{keyLog}\"");
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + string.Join(" & ", removals),
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
            // ignored
        }
    }
}
