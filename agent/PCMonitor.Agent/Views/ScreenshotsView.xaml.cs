using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCMonitor.Agent.Services;

namespace PCMonitor.Agent.Views;

public partial class ScreenshotsView : UserControl
{
    public ScreenshotsView()
    {
        InitializeComponent();
        DataContext = UiState.I;
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        CaptureButton.IsEnabled = false;
        try
        {
            var outcome = await App.Host.CaptureAndUploadAsync("desktop");
            if (!outcome.Ok) MessageBox.Show(outcome.Error ?? "Capture failed", "Sentinel",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            CaptureButton.IsEnabled = true;
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = App.Host.Store.ShotsDir, UseShellExecute = true });
        }
        catch
        {
            // ignored
        }
    }

    private void Shots_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var item = FindDataContext<ShotItem>(source);
        if (item == null) return;
        var viewer = new ImageViewerWindow(item.Path, item.Caption + " · " + item.TimeText)
        {
            Owner = Window.GetWindow(this)
        };
        viewer.ShowDialog();
    }

    private static T? FindDataContext<T>(DependencyObject source) where T : class
    {
        var current = source;
        while (current != null)
        {
            if (current is FrameworkElement element && element.DataContext is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
