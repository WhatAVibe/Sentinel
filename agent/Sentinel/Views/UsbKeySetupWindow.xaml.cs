using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Sentinel.Services;

namespace Sentinel.Views;

public partial class UsbKeySetupWindow : Window
{
    private readonly ObservableCollection<UsbKeyDrive> _drives = new();
    private bool _busy;

    public UsbKeySetupWindow()
    {
        InitializeComponent();
        DriveList.ItemsSource = _drives;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private UsbKeyDrive? SelectedDrive =>
        DriveList.SelectedItem as UsbKeyDrive;

    private async Task RefreshAsync()
    {
        if (_busy) return;
        if (UsbKeyTool.FindExecutable() == null)
        {
            StatusText.Text = "sentinel-key.exe was not found. Build or install the Sentinel Key component first.";
            _drives.Clear();
            return;
        }
        _busy = true;
        StatusText.Text = "Reading removable drives...";
        try
        {
            var selected = SelectedDrive?.Letter;
            var drives = await UsbKeyTool.ListAsync();
            _drives.Clear();
            foreach (var drive in drives) _drives.Add(drive);
            if (!string.IsNullOrEmpty(selected))
                DriveList.SelectedItem = _drives.FirstOrDefault(d => d.Letter == selected);
            if (DriveList.SelectedItem == null && _drives.Count > 0)
                DriveList.SelectedIndex = 0;
            StatusText.Text = _drives.Count == 0
                ? "No removable drives found. Plug the USB drive in and press Refresh."
                : $"{_drives.Count} removable drive(s) found.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not read drives: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Enroll_Click(object sender, RoutedEventArgs e)
    {
        var drive = SelectedDrive;
        if (drive == null)
        {
            StatusText.Text = "Select a removable drive first.";
            return;
        }
        if (MessageBox.Show(
                $"Enroll {drive.Title} as a Sentinel key? This writes SENTINEL.KEY and a small README to the drive root.",
                "Sentinel USB key", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _busy = true;
        StatusText.Text = "Enrolling...";
        try
        {
            var (ok, output) = await UsbKeyTool.EnrollDriveAsync(
                drive.Letter + ":",
                ForceCheck.IsChecked == true,
                StartupCheck.IsChecked == true);
            StatusText.Text = output;
            if (ok && UsbKeyTool.StartGuard())
                StatusText.Text += Environment.NewLine + "Guard started.";
            _busy = false;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Enroll failed: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void Forget_Click(object sender, RoutedEventArgs e)
    {
        var drive = SelectedDrive;
        if (drive == null)
        {
            StatusText.Text = "Select a removable drive first.";
            return;
        }
        if (MessageBox.Show(
                $"Forget the Sentinel key on {drive.Title}? The encrypted marker and README will be deleted.",
                "Sentinel USB key", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _busy = true;
        StatusText.Text = "Removing key...";
        try
        {
            var (_, output) = await UsbKeyTool.ForgetAsync(
                drive.Letter + ":",
                ForceCheck.IsChecked == true);
            StatusText.Text = output;
            _busy = false;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Forget failed: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        var exe = UsbKeyTool.FindExecutable();
        if (exe == null)
        {
            StatusText.Text = "sentinel-key.exe was not found.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not open Sentinel Key: " + ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
