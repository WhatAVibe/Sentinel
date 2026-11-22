using System.Windows;
using System.Windows.Threading;

namespace Sentinel.Views;

public partial class PowerConfirmWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _remaining = 30;

    public PowerConfirmWindow(string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            CountdownText.Text = $"Auto-deny in {Math.Max(0, _remaining)}s";
            if (_remaining <= 0) Close();
        };
        CountdownText.Text = $"Auto-deny in {_remaining}s";
        _timer.Start();
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
        };
        Closed += (_, _) => _timer.Stop();
    }

    public bool Accepted { get; private set; }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }
}
