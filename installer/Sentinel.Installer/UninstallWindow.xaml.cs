using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Sentinel.Installer;

public partial class UninstallWindow : Window
{
    private readonly string _installDir;

    public UninstallWindow(string installDir)
    {
        InitializeComponent();
        _installDir = installDir;
        PathText.Text = $"Install folder: {installDir}";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
        Application.Current.Shutdown();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        UninstallButton.IsEnabled = false;
        StatusText.Text = "Stopping Sentinelâ€¦";
        InstallerCore.StopAgent();

        if (UnpairCheck.IsChecked == true)
        {
            var (url, token) = InstallerCore.ReadConfig(_installDir);
            if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text = "Removing this PC from the relayâ€¦";
                try
                {
                    await InstallerCore.PurgeRelayAsync(url, token);
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Relay purge failed (" + ex.Message + "); continuing locally.";
                }
            }
        }

        StatusText.Text = "Removing startup and registry entriesâ€¦";
        InstallerCore.RemoveUninstall();

        if (WipeCheck.IsChecked == true)
        {
            StatusText.Text = "Deleting dataâ€¦";
            TryDelete(_installDir);
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sentinel");
            if (!string.Equals(legacy, _installDir, StringComparison.OrdinalIgnoreCase)) TryDelete(legacy);
        }
        else
        {
            TryDelete(Path.Combine(_installDir, "Sentinel.exe"));
            TryDelete(Path.Combine(_installDir, "Sentinel.dll"));
            TryDelete(Path.Combine(_installDir, "Sentinel.deps.json"));
            TryDelete(Path.Combine(_installDir, "Sentinel.runtimeconfig.json"));
        }

        StatusText.Text = "Removing the install folderâ€¦";
        await Task.Delay(400);
        Close();
        if (WipeCheck.IsChecked == true) ScheduleDelete(_installDir);
        Application.Current.Shutdown();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // retried by ScheduleDelete
        }
    }

    private static void ScheduleDelete(string dir)
    {
        try
        {
            var self = Environment.ProcessPath ?? "";
            if (!self.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{dir}\"",
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
