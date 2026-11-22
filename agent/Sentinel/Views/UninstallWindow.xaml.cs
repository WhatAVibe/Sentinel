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
        ScheduleDelete(installDir, WipeCheck.IsChecked == true);
    }

    private static void ScheduleDelete(string dir, bool wipe)
    {
        try
        {
            var command = wipe
                ? $"ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{dir}\""
                : $"ping -n 3 127.0.0.1 >nul & del /f /q \"{Path.Combine(dir, "Sentinel.exe")}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
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
