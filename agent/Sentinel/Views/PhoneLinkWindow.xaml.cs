using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Sentinel.Models;
using Sentinel.Services;
using QRCoder;

namespace Sentinel.Views;

public partial class PhoneLinkWindow : Window
{
    private DispatcherTimer? _poll;
    private DispatcherTimer? _close;
    private string _pendingId = "";
    private bool _busy;
    private bool _finished;

    public PhoneLinkWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await CreateAsync();
        Closed += (_, _) => StopPolling();
    }

    private async Task CreateAsync()
    {
        try
        {
            var link = await App.Host.Api.CreateLinkRequestAsync(CancellationToken.None);
            if (link == null) throw new InvalidOperationException("no response");
            CodeText.Text = link.PairCode;
            var url = App.Host.Config.ServerUrl;
            var payload = JsonSerializer.Serialize(new
            {
                v = 1,
                u = url,
                r = link.RequestId,
                c = link.PairCode,
                n = App.Host.Config.DeviceName
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
            StatusText.Text = $"Server {url} · code {link.PairCode}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not create a pairing code: " + ex.Message;
        }
        StartPolling();
    }

    private void StartPolling()
    {
        StopPolling();
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += Poll_Tick;
        _poll.Start();
    }

    private void StopPolling()
    {
        _poll?.Stop();
        _poll = null;
        _close?.Stop();
        _close = null;
    }

    private async void Poll_Tick(object? sender, EventArgs e)
    {
        if (_busy || _finished || _pendingId.Length > 0) return;
        _busy = true;
        try
        {
            var phones = await App.Host.Api.GetPhonesAsync(CancellationToken.None);
            var pending = phones.FirstOrDefault(p => !p.Confirmed);
            if (pending != null) AskForCode(pending);
        }
        catch
        {
            // relay hiccup, try again on the next tick
        }
        finally
        {
            _busy = false;
        }
    }

    private void AskForCode(PhoneDto device)
    {
        _pendingId = device.Id;
        StopPolling();
        QrPanel.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Visible;
        ConfirmTitle.Text = $"Enter the 6-digit code shown on {device.Name}";
        StatusText.Text = $"{device.Name} approved this PC. Nothing can be triggered until you confirm it here.";
        Topmost = true;
        Topmost = false;
        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        Activate();
        ConfirmBox.Focus();
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e) => await SubmitCodeAsync();

    private async void ConfirmBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ConfirmBox.Text.Length == 6) await SubmitCodeAsync();
    }

    private async Task SubmitCodeAsync()
    {
        if (_busy || _finished || _pendingId.Length == 0) return;
        var code = ConfirmBox.Text.Trim();
        if (code.Length != 6)
        {
            ShowConfirmError("Enter the six digits shown on the device.");
            return;
        }
        _busy = true;
        ConfirmButton.IsEnabled = false;
        try
        {
            await App.Host.Api.ConfirmPhoneAsync(_pendingId, code, CancellationToken.None);
            _finished = true;
            ConfirmError.Visibility = Visibility.Collapsed;
            ConfirmPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "Device confirmed. Sentinel is ready on that device.";
            _close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _close.Tick += (_, _) => Close();
            _close.Start();
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
            _busy = false;
            ConfirmButton.IsEnabled = true;
        }
    }

    private void ShowConfirmError(string message)
    {
        ConfirmError.Text = message;
        ConfirmError.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
