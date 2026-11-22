using System.Windows;

namespace Sentinel.Views;

public partial class FullscreenMessageWindow : Window
{
    private readonly string? _messageId;
    private readonly bool _allowReply;
    private bool _replied;

    public FullscreenMessageWindow(string title, string body, bool allowReply = false, string? messageId = null)
    {
        InitializeComponent();
        _messageId = messageId;
        _allowReply = allowReply;
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Message from your phone" : title;
        BodyText.Text = body;
        ReplyPanel.Visibility = allowReply ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = Math.Max(200, SystemParameters.VirtualScreenWidth);
            Height = Math.Max(200, SystemParameters.VirtualScreenHeight);
            Activate();
            if (_allowReply) ReplyBox.Focus();
        };
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var reply = ReplyBox.Text.Trim();
        if (reply.Length == 0) return;
        SendButton.IsEnabled = false;
        if (!string.IsNullOrEmpty(_messageId))
            await App.Host.SendMessageReplyAsync(_messageId, reply, "replied");
        _replied = true;
        Close();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (!_replied && !string.IsNullOrEmpty(_messageId))
            _ = App.Host.SendMessageReplyAsync(_messageId, null, "dismissed");
        base.OnClosed(e);
    }
}
