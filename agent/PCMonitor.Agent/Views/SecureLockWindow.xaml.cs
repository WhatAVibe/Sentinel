using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PCMonitor.Agent.Services;

namespace PCMonitor.Agent.Views;

public partial class SecureLockWindow : Window
{
    private readonly DispatcherTimer _guard;
    private bool _allowClose;

    public SecureLockWindow(string message)
    {
        InitializeComponent();
        _guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _guard.Tick += Guard_Tick;
        MessageText.Text = string.IsNullOrWhiteSpace(message)
            ? "Sentinel locked this PC from your phone."
            : message;
        Loaded += (_, _) =>
        {
            ApplyVirtualScreenBounds();
            Activate();
            CodeBox.Focus();
            _guard.Start();
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SecureLock.OverlayHandle = handle;
        };
        Closed += (_, _) => _guard.Stop();
        Closing += (_, e) =>
        {
            if (!_allowClose) e.Cancel = true;
        };
        KeyButton.Visibility = SecurityKey.Current(App.Host.Config) == null
            ? Visibility.Collapsed
            : Visibility.Visible;
        MasterPanel.Visibility = App.Host.HasMasterPassword ? Visibility.Visible : Visibility.Collapsed;
    }

    public void CloseAuthorized()
    {
        _allowClose = true;
        try
        {
            _guard?.Stop();
        }
        catch
        {
            // ignored
        }
        Close();
    }

    private void ApplyVirtualScreenBounds()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = Math.Max(200, SystemParameters.VirtualScreenWidth);
        Height = Math.Max(200, SystemParameters.VirtualScreenHeight);
        DrawWallpaper();
    }

    private void DrawWallpaper()
    {
        WallpaperCanvas.Children.Clear();
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.UriSource = new Uri("pack://application:,,,/Assets/wallpaper.png");
        image.EndInit();
        image.Freeze();

        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0) return;

        var minX = screens.Min(screen => screen.Bounds.Left);
        var minY = screens.Min(screen => screen.Bounds.Top);
        var maxX = screens.Max(screen => screen.Bounds.Right);
        var maxY = screens.Max(screen => screen.Bounds.Bottom);
        var scaleX = SystemParameters.VirtualScreenWidth / Math.Max(1, maxX - minX);
        var scaleY = SystemParameters.VirtualScreenHeight / Math.Max(1, maxY - minY);

        foreach (var screen in screens)
        {
            var bounds = screen.Bounds;
            var width = bounds.Width * scaleX;
            var height = bounds.Height * scaleY;
            var left = (bounds.Left - minX) * scaleX;
            var top = (bounds.Top - minY) * scaleY;

            var tile = new System.Windows.Controls.Border
            {
                Width = width,
                Height = height,
                ClipToBounds = true,
                Background = System.Windows.Media.Brushes.Black,
                Child = new System.Windows.Controls.Image
                {
                    Source = image,
                    Stretch = System.Windows.Media.Stretch.UniformToFill
                }
            };
            System.Windows.Controls.Canvas.SetLeft(tile, left);
            System.Windows.Controls.Canvas.SetTop(tile, top);
            WallpaperCanvas.Children.Add(tile);
        }
    }

    private void Guard_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (!SecureLock.Active) return;
            Topmost = false;
            Topmost = true;
            if (!IsActive) Activate();
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch
        {
            // ignored
        }
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (code.Length != 6)
        {
            ShowError("Enter all six digits.");
            return;
        }
        if (!App.Host.VerifyCode(code))
        {
            ShowError("That code is not valid right now.");
            CodeBox.SelectAll();
            CodeBox.Focus();
            return;
        }
        ErrorText.Visibility = Visibility.Collapsed;
        App.Host.ExitSecureLock("desktop code");
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void MasterUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Host.VerifyCode(MasterBox.Password))
        {
            ShowError("That recovery password is not correct.");
            MasterBox.SelectAll();
            MasterBox.Focus();
            return;
        }
        if (App.Host.Config.RequirePhonePresence && !App.Host.Adb.TrustedPresent)
        {
            ShowError("Phone presence is required. Connect your phone over ADB first.");
            return;
        }
        ErrorText.Visibility = Visibility.Collapsed;
        App.Host.ExitSecureLock("recovery password");
    }

    private async void Key_Click(object sender, RoutedEventArgs e)
    {
        KeyButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        KeyHintText.Text = "Touch your security key…";
        KeyHintText.Visibility = Visibility.Visible;
        var (ok, error) = await SecurityKey.VerifyAsync(App.Host);
        KeyButton.IsEnabled = true;
        if (!ok)
        {
            KeyHintText.Text = error == "cancelled"
                ? "Key touch cancelled. Try again."
                : $"Security key check failed ({error}).";
            return;
        }
        if (App.Host.Config.RequirePhonePresence && !App.Host.Adb.TrustedPresent)
        {
            KeyHintText.Text = "Phone presence is required. Connect your phone over ADB first.";
            return;
        }
        KeyHintText.Visibility = Visibility.Collapsed;
        App.Host.ExitSecureLock("security key");
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
