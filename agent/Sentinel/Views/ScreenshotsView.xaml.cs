using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Sentinel.Services;

namespace Sentinel.Views;

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

    private async void PurgeRelay_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Host.Config.Paired)
        {
            MessageBox.Show("Pair this PC first.", "Screenshots", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var result = MessageBox.Show(
            "Delete every screenshot stored on the relay for this PC? This cannot be undone. Only the copies on this PC remain.",
            "Purge relay screenshots", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            await App.Host.Api.PurgeScreenshotsAsync(CancellationToken.None);
            MessageBox.Show("Relay screenshots deleted.", "Screenshots", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Purge relay screenshots", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Shots_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var item = FindDataContext<ShotItem>(source);
        if (item == null) return;
        var viewer = new ImageViewerWindow(item.Path, item.Caption + " Â· " + item.TimeText)
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
