using System.Windows;
using System.Windows.Threading;
using PCMonitor.Agent.Services;

namespace PCMonitor.Agent.Views;

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
    }

    public string Code => CodeBox.Text.Trim().Replace(" ", "");

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
            ShowError("That code is not valid right now. Wait for the next code and try again.");
            CodeBox.SelectAll();
            CodeBox.Focus();
            return;
        }
        if (_host.Config.RequirePhonePresence && !_host.Adb.TrustedPresent)
        {
            ShowError("Phone presence is required. Connect your phone over ADB first.");
            return;
        }
        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
