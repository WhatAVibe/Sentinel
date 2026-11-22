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
    private string _serverUrl = "";
    private bool _polling;
    private long _expiresAt;

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
            var payload = JsonSerializer.Serialize(new
            {
                v = 1,
                u = _serverUrl,
                r = _request.RequestId,
                c = _request.PairCode,
                n = Environment.MachineName
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
                StatusText.Text = "Paired successfully.";
                App.Tray.Update();
                DialogResult = true;
                Close();
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
