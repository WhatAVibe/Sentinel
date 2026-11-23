using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Sentinel.Models;
using Sentinel.Services;
using QRCoder;

namespace Sentinel.Views;

public partial class PairWindow : Window
{
    private ApiClient? _client;
    private PairRequestResponse? _request;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _confirmTimer;
    private string _serverUrl = "";
    private bool _polling;
    private long _expiresAt;
    private string _pendingId = "";
    private bool _confirmBusy;
    private bool _finished;

    public PairWindow()
    {
        InitializeComponent();
        ServerBox.Text = App.Host.Config.ServerUrl;
        InsecureCheck.IsChecked = App.Host.Config.AllowInsecureTls;
        Closed += (_, _) => StopTimer();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var url = Normalize(ServerBox.Text);
        if (url == null)
        {
            StatusText.Text = "Enter a valid server address, for example http://192.168.1.20:8765";
            return;
        }

        ConnectButton.IsEnabled = false;
        StatusText.Text = "Contacting server…";
        try
        {
            var insecure = InsecureCheck.IsChecked == true;
            var health = await ApiClient.PingAsync(url, insecure, CancellationToken.None);
            if (health == null) throw new InvalidOperationException("No health response");

            StatusText.Text = $"Connected to {health.Name}. Creating pairing code…";
            _client?.Dispose();
            _client = new ApiClient { BaseUrl = url, AllowInsecureTls = insecure };
            _request = await _client.CreatePairAsync(Environment.MachineName, App.Host.Config.DeviceId, CancellationToken.None);
            _serverUrl = url;
            _expiresAt = _request.ExpiresAt;

            App.Host.Config.ServerUrl = url;
            App.Host.Config.AllowInsecureTls = insecure;
            App.Host.SaveConfig();

            ShowPairing();
            StartTimer();
            StatusText.Text = "Waiting for the phone to approve…";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed: " + ex.Message;
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void ShowPairing()
    {
        if (_request == null) return;
        CodePanel.Visibility = Visibility.Visible;
        CodeText.Text = _request.PairCode;
        CountdownText.Text = "Code expires shortly";
        try
        {
            var dataKey = DataKey.Base64(App.Host.Config);
            App.Host.SaveConfig();
            var payload = JsonSerializer.Serialize(new
            {
                v = 2,
                u = _serverUrl,
                r = _request.RequestId,
                c = _request.PairCode,
                n = Environment.MachineName,
                k = dataKey
            });
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(data).GetGraphic(8);
            using var stream = new MemoryStream(png);
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            QrImage.Source = image;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not render QR code: " + ex.Message;
        }
    }

    private void StartTimer()
    {
        StopTimer();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += Poll;
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
        _confirmTimer?.Stop();
        _confirmTimer = null;
    }

    private async void Poll(object? sender, EventArgs e)
    {
        if (_polling || _client == null || _request == null) return;
        _polling = true;
        try
        {
            var remaining = TimeSpan.FromMilliseconds(_expiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            CountdownText.Text = remaining > TimeSpan.Zero
                ? $"Expires in {Math.Max(0, (int)remaining.TotalSeconds)}s"
                : "Expired";

            var status = await _client.PollPairAsync(_request.RequestId, _request.RequestToken, CancellationToken.None);
            if (status.Status == "approved" && !string.IsNullOrEmpty(status.AgentToken))
            {
                StopTimer();
                App.Host.Config.ServerUrl = _serverUrl;
                App.Host.Config.AllowInsecureTls = InsecureCheck.IsChecked == true;
                App.Host.SaveConfig();
                App.Host.ApplyPairing(status.DeviceId ?? "", status.AgentToken, status.TotpSecret ?? "");
                App.Host.Api.BaseUrl = _serverUrl;
                App.Host.Api.Token = status.AgentToken;
                App.Tray.Update();
                BeginConfirm();
            }
            else if (status.Status is "expired" or "denied" or "claimed")
            {
                StopTimer();
                StatusText.Text = status.Status == "denied"
                    ? "The phone denied this request."
                    : "This code is no longer valid. Generate a new one.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Waiting… (" + ex.Message + ")";
        }
        finally
        {
            _polling = false;
        }
    }

    private void BeginConfirm()
    {
        ConfirmPanel.Visibility = Visibility.Visible;
        ConfirmTitle.Text = "Enter the 6-digit code shown on the device";
        StatusText.Text = "This PC is paired. Confirm the device to finish.";
        CountdownText.Text = "Waiting for the device…";
        Activate();
        _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _confirmTimer.Tick += ConfirmPoll;
        _confirmTimer.Start();
    }

    private async void ConfirmPoll(object? sender, EventArgs e)
    {
        if (_confirmBusy || _pendingId.Length > 0) return;
        _confirmBusy = true;
        try
        {
            var phones = await App.Host.Api.GetPhonesAsync(CancellationToken.None);
            var pending = phones.FirstOrDefault(p => !p.Confirmed);
            if (pending != null)
            {
                _pendingId = pending.Id;
                _confirmTimer?.Stop();
                ConfirmTitle.Text = $"Enter the 6-digit code shown on {pending.Name}";
                CountdownText.Text = "Nothing can be triggered until you confirm it here.";
                ConfirmBox.Focus();
            }
        }
        catch
        {
            // retry on the next tick
        }
        finally
        {
            _confirmBusy = false;
        }
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e) => await SubmitConfirmAsync();

    private async void ConfirmBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ConfirmBox.Text.Length == 6) await SubmitConfirmAsync();
    }

    private async Task SubmitConfirmAsync()
    {
        if (_confirmBusy || _finished || _pendingId.Length == 0) return;
        var code = ConfirmBox.Text.Trim();
        if (code.Length != 6)
        {
            ShowConfirmError("Enter the six digits shown on the device.");
            return;
        }
        _confirmBusy = true;
        ConfirmButton.IsEnabled = false;
        try
        {
            await App.Host.Api.ConfirmPhoneAsync(_pendingId, code, CancellationToken.None);
            _finished = true;
            _confirmTimer?.Stop();
            ConfirmError.Visibility = Visibility.Collapsed;
            StatusText.Text = "Paired and confirmed.";
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowConfirmError(ex is ApiException api && api.Code == "bad_code"
                ? "That code is not correct. Check the device and try again."
                : ex.Message);
            ConfirmBox.SelectAll();
            ConfirmBox.Focus();
        }
        finally
        {
            _confirmBusy = false;
            ConfirmButton.IsEnabled = true;
        }
    }

    private void ShowConfirmError(string message)
    {
        ConfirmError.Text = message;
        ConfirmError.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        StopTimer();
        DialogResult = false;
        Close();
    }

    private static string? Normalize(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0) return null;
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        return uri.ToString().TrimEnd('/');
    }
}
