using System.Windows;
using System.Windows.Controls;
using PCMonitor.Agent.Services;

namespace PCMonitor.Agent.Views;

public partial class DashboardView : UserControl
{
    private readonly System.Windows.Threading.DispatcherTimer _codeTimer;

    public DashboardView()
    {
        InitializeComponent();
        DataContext = UiState.I;
        UiState.I.UpdateFromDevice(App.Host.BuildDeviceSnapshot());
        _codeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _codeTimer.Tick += (_, _) => UpdateCode();
        Loaded += (_, _) =>
        {
            UpdateCode();
            _codeTimer.Start();
        };
        Unloaded += (_, _) => _codeTimer.Stop();
    }

    private void UpdateCode()
    {
        var code = Totp.Now(App.Host.Config.TotpSecret);
        if (string.IsNullOrEmpty(code))
        {
            CodeText.Text = "------";
            CodeSecondsText.Text = "not paired";
            CodeProgress.Value = 0;
            CopyCodeButton.IsEnabled = false;
            return;
        }
        var seconds = Totp.SecondsRemaining();
        CodeText.Text = code;
        CodeSecondsText.Text = $"{seconds}s left";
        CodeProgress.Value = seconds;
        CopyCodeButton.IsEnabled = true;
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var code = Totp.Now(App.Host.Config.TotpSecret);
            if (string.IsNullOrEmpty(code)) return;
            Clipboard.SetText(code);
            App.Tray.ShowBalloon("Sentinel", $"2FA code {code} copied to the clipboard.");
        }
        catch
        {
            // ignored
        }
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var tag = button.Tag as string ?? "";
        var host = App.Host;

        switch (tag)
        {
            case "lock":
                await host.RunLocalAsync("lock");
                break;
            case "screenshot":
            {
                button.IsEnabled = false;
                try
                {
                    var outcome = await host.CaptureAndUploadAsync("desktop");
                    App.Tray.ShowBalloon("Screenshot", outcome.Ok
                        ? "Captured all displays — open the Screenshots tab."
                        : outcome.Error ?? "Capture failed");
                }
                finally
                {
                    button.IsEnabled = true;
                }
                break;
            }
            case "away":
                await host.EnableAwayAsync("desktop");
                break;
            case "disarm":
                App.Current.RequestDisarm();
                break;
            case "screenoff":
                await host.RunLocalAsync("screen_off");
                break;
            case "screenon":
                await host.RunLocalAsync("screen_on");
                break;
            case "restart":
                if (Confirm("Restart this PC in 10 seconds?", "Restart"))
                    await host.RunLocalAsync("restart");
                break;
            case "shutdown":
                if (Confirm("Shut down this PC in 10 seconds?", "Shut down"))
                    await host.RunLocalAsync("shutdown");
                break;
        }
    }

    private static bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        UiState.I.UpdateFromDevice(App.Host.BuildDeviceSnapshot());
        UiState.I.LastSeen = DateTime.Now.ToString("HH:mm:ss");
    }

    private void Disarm_Click(object sender, RoutedEventArgs e) => App.Current.RequestDisarm();

    private void ViewAll_Click(object sender, RoutedEventArgs e)
    {
        (Window.GetWindow(this) as MainWindow)?.NavigateTo("activity");
    }

    private void Pair_Click(object sender, RoutedEventArgs e)
    {
        var window = new PairWindow { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true)
        {
            UiState.I.UpdateFromDevice(App.Host.BuildDeviceSnapshot());
            App.Tray.Update();
        }
    }
}
