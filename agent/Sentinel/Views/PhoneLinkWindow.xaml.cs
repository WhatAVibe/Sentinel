using System.IO;
using System.Text.Json;
using System.Windows;
using Sentinel.Models;
using QRCoder;

namespace Sentinel.Views;

public partial class PhoneLinkWindow : Window
{
    public PhoneLinkWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await CreateAsync();
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
            StatusText.Text = $"Server {url} Â· code {link.PairCode}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not create a pairing code: " + ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
