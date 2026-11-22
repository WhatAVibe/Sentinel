using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCMonitor.Agent.Views;

public partial class ImageViewerWindow : Window
{
    private readonly string _path;
    private double _zoom = 1;

    public ImageViewerWindow(string path, string caption)
    {
        InitializeComponent();
        _path = path;
        Caption.Text = caption;
        Loaded += (_, _) =>
        {
            LoadImage();
            FitToViewport();
        };
        Scroller.SizeChanged += (_, _) =>
        {
            if (_zoom <= 1.001) FitToViewport();
        };
        PreviewMouseWheel += OnWheel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void LoadImage()
    {
        try
        {
            using var stream = File.OpenRead(_path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Preview.Source = image;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Screenshot", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private void FitToViewport()
    {
        _zoom = 1;
        Preview.LayoutTransform = Transform.Identity;
        Preview.Width = Math.Max(1, Scroller.ActualWidth);
        Preview.Height = Math.Max(1, Scroller.ActualHeight);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom + (e.Delta > 0 ? 0.12 : -0.12), 0.2, 5);
        if (_zoom <= 1.001)
        {
            FitToViewport();
        }
        else
        {
            Preview.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        }
        e.Handled = true;
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.GetDirectoryName(_path) ?? App.Host.Store.ShotsDir,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignored
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
