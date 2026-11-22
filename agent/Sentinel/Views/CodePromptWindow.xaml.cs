using System.Windows;
using System.Windows.Threading;
using Sentinel.Services;

namespace Sentinel.Views;

public partial class CodePromptWindow : Window
{
    private readonly AgentHost _host;
    private readonly DispatcherTimer _timer;

    public CodePromptWindow(string title, string description, AgentHost host)
    {
        InitializeComponent();
        _host = host;
        TitleText.Text = title;
        DescriptionText.Text = description;
        CodeBox.Focus();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            var remaining = Totp.SecondsRemaining();
            HintText.Text = $"Current code window refreshes in {remaining}s · trusted phone " +
                            (_host.Adb.TrustedPresent ? "connected" : "not connected");
        };
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        KeyButton.Visibility = SecurityKey.Current(App.Host.Config) == null
            ? Visibility.Collapsed
            : Visibility.Visible;
        MasterPanel.Visibility = host.HasMasterPassword ? Visibility.Visible : Visibility.Collapsed;
    }

    public string Code => CodeBox.Text.Trim().Replace(" ", "");

    public string Method { get; private set; } = "code";

    private void Verify_Click(object sender, RoutedEventArgs e)
    {
        var code = Code;
        if (code.Length == 0)
        {
            ShowError("Enter the 6-digit code.");
            return;
        }
        if (!_host.VerifyCode(code))
        {
            var locked = _host.CodeLockoutRemaining;
            ShowError(locked > TimeSpan.Zero
                ? $"Too many attempts. Try again in {(int)Math.Ceiling(locked.TotalSeconds)}s."
                : "That code is not valid right now. Wait for the next code and try again.");
            CodeBox.SelectAll();
            CodeBox.Focus();
            return;
        }
        if (_host.Config.RequirePhonePresence && !_host.Adb.TrustedPresent)
        {
            ShowError("Phone presence is required. Connect your phone over ADB first.");
            return;
        }
        Method = "code";
        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void MasterVerify_Click(object sender, RoutedEventArgs e)
    {
        if (!_host.VerifyCode(MasterBox.Password))
        {
            var locked = _host.CodeLockoutRemaining;
            ShowError(locked > TimeSpan.Zero
                ? $"Too many attempts. Try again in {(int)Math.Ceiling(locked.TotalSeconds)}s."
                : "That recovery password is not correct.");
            MasterBox.SelectAll();
            MasterBox.Focus();
            return;
        }
        if (_host.Config.RequirePhonePresence && !_host.Adb.TrustedPresent)
        {
            ShowError("Phone presence is required. Connect your phone over ADB first.");
            return;
        }
        Method = "recovery password";
        DialogResult = true;
        Close();
    }

    private async void Key_Click(object sender, RoutedEventArgs e)
    {
        KeyButton.IsEnabled = false;
        ErrorText.Text = "Touch your security key…";
        ErrorText.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
        ErrorText.Visibility = Visibility.Visible;
        var (ok, error) = await SecurityKey.VerifyAsync(_host);
        KeyButton.IsEnabled = true;
        ErrorText.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
        if (!ok)
        {
            ShowError(error == "cancelled"
                ? "Key touch cancelled. Try again."
                : $"Security key check failed ({error}).");
            return;
        }
        if (_host.Config.RequirePhonePresence && !_host.Adb.TrustedPresent)
        {
            ShowError("Phone presence is required. Connect your phone over ADB first.");
            return;
        }
        ErrorText.Visibility = Visibility.Collapsed;
        Method = "security key";
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
