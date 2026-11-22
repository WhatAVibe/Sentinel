using System.Windows;
using System.Windows.Threading;
using Sentinel.Models;
using Sentinel.Services;

namespace Sentinel.Views;

public partial class DeviceConfirmWindow : Window
{
    private readonly AgentHost _host;
    private readonly PhoneDto _device;
    private readonly DispatcherTimer _timer;
    private bool _busy;

    public DeviceConfirmWindow(AgentHost host, PhoneDto device)
    {
        InitializeComponent();
        _host = host;
        _device = device;
        var kind = string.Equals(device.Kind, "watch", StringComparison.OrdinalIgnoreCase) ? "watch" : "phone";
        TitleText.Text = $"Confirm this {kind}";
        DescriptionText.Text = string.IsNullOrWhiteSpace(device.AppVersion)
            ? $"{device.Name} approved this PC. Enter the 6-digit code shown on it to finish linking."
            : $"{device.Name} ({kind} {device.AppVersion}) approved this PC. Enter the 6-digit code shown on it.";
        Loaded += (_, _) =>
        {
            Activate();
            CodeBox.Focus();
            _timer.Start();
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            HintText.Text = $"Code refreshes in {Totp.SecondsRemaining()}s · check the device, not this PC";
        };
        Closed += (_, _) => _timer.Stop();
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var code = CodeBox.Text.Trim().Replace(" ", "");
        if (code.Length != 6)
        {
            ShowError("Enter the six digits shown on the device.");
            return;
        }
        _busy = true;
        ConfirmButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            await _host.Api.ConfirmPhoneAsync(_device.Id, code, CancellationToken.None);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex is ApiException api && api.Code == "bad_code"
                ? "That code is not correct. Wait for the next code and try again."
                : ex.Message);
            CodeBox.SelectAll();
            CodeBox.Focus();
        }
        finally
        {
            _busy = false;
            ConfirmButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
