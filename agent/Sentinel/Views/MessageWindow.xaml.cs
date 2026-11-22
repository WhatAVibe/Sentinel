using System.Windows;
using System.Windows.Threading;

namespace Sentinel.Views;

public partial class MessageWindow : Window
{
    private readonly string? _messageId;
    private readonly bool _allowReply;
    private readonly int _autoCloseSeconds;
    private readonly DispatcherTimer _timer;
    private int _remaining;
    private bool _closed;

    public MessageWindow(
        string title,
        string body,
        string kind,
        bool allowReply,
        string? messageId,
        int autoCloseSeconds = 0)
    {
        InitializeComponent();
        _messageId = messageId;
        _allowReply = allowReply;
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Message from your phone" : title;
        BodyText.Text = body;
        KindText.Text = $"{kind.ToUpperInvariant()} Â· SENT BY SENTINEL";

        switch (kind)
        {
            case "alert":
                IconBadge.Background = (System.Windows.Media.Brush)FindResource("Danger");
                IconGlyph.Text = "\uE7BA";
                break;
            case "dialog":
                IconBadge.Background = (System.Windows.Media.Brush)FindResource("Accent");
                IconGlyph.Text = "\uE8BD";
                break;
            case "banner":
                IconBadge.Background = (System.Windows.Media.Brush)FindResource("Warning");
                IconGlyph.Foreground = System.Windows.Media.Brushes.Black;
                IconGlyph.Text = "\uE7E7";
                break;
        }

        ReplyPanel.Visibility = allowReply ? Visibility.Visible : Visibility.Collapsed;
        ReplyBox.Focus();

        _autoCloseSeconds = autoCloseSeconds;
        if (autoCloseSeconds > 0)
        {
            _remaining = autoCloseSeconds;
            TimerText.Text = $"Closing in {_remaining}s";
            CountdownText.Text = $"Closing in {_remaining}s";
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                _remaining--;
                TimerText.Text = $"Closing in {Math.Max(0, _remaining)}s";
                CountdownText.Text = $"Closing in {Math.Max(0, _remaining)}s";
                if (_remaining <= 0) Close();
            };
            _timer.Start();
        }
        else
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            _timer.Tick += (_, _) => Close();
            _timer.Start();
        }

        Loaded += (_, _) => PositionWindow();
        Closed += (_, _) => _timer.Stop();
    }

    public event Action? Dismissed;

    private void PositionWindow()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 22;
        Top = area.Bottom - ActualHeight - 22;
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        var reply = ReplyBox.Text.Trim();
        if (reply.Length == 0) return;
        _closed = true;
        _timer.Stop();
        _ = ReplyAsync(reply);
        Close();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        _closed = true;
        _timer.Stop();
        if (!_allowReply && !string.IsNullOrEmpty(_messageId))
            _ = App.Host.SendMessageReplyAsync(_messageId, null, "dismissed");
        Dismissed?.Invoke();
        Close();
    }

    private async Task ReplyAsync(string reply)
    {
        if (string.IsNullOrEmpty(_messageId)) return;
        await App.Host.SendMessageReplyAsync(_messageId, reply, "replied");
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_closed && !string.IsNullOrEmpty(_messageId))
        {
            _ = App.Host.SendMessageReplyAsync(_messageId, null, "dismissed");
        }
        base.OnClosed(e);
    }
}
