using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using PCMonitor.Agent.Services;
using PCMonitor.Agent.Views;

namespace PCMonitor.Agent;

public partial class MainWindow : Window
{
    public static bool AllowClose { get; set; }

    private readonly DashboardView _dashboard = new();
    private readonly ActivityView _activity = new();
    private readonly ScreenshotsView _screenshots = new();
    private readonly LogsView _logs = new();
    private readonly SettingsView _settingsView = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = UiState.I;

        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && App.Host.Config.MinimizeToTray) Hide();
        };
        Closing += OnClosing;
        Nav.SelectedIndex = 0;
    }

    private bool _serverRevealed;

    public void NavigateTo(string key)
    {
        HideServerUrl();
        Nav.SelectedIndex = key switch
        {
            "activity" => 1,
            "screenshots" => 2,
            "logs" => 3,
            "settings" => 4,
            _ => 0
        };
    }

    private void ServerText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_serverRevealed) HideServerUrl();
        else
        {
            _serverRevealed = true;
            ServerText.Effect = null;
        }
    }

    private void HideServerUrl()
    {
        _serverRevealed = false;
        ServerText.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 };
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Body == null) return;
        Body.Content = Nav.SelectedIndex switch
        {
            1 => _activity,
            2 => _screenshots,
            3 => _logs,
            4 => _settingsView,
            _ => _dashboard
        };
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
        App.Tray.ShowBalloon("Sentinel is still running", "Use the tray icon to reopen or exit.");
    }

    private void ApplyDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var value = 1;
            if (DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
        }
        catch
        {
            // older Windows builds ignore dark chrome
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
