using System.Windows;
using System.Windows.Threading;

namespace PCMonitor.Agent.Views;

public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;

    public ToastWindow(string title, string body)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _timer.Tick += (_, _) => Close();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 22;
            Top = area.Bottom - Height - 22;
            _timer.Start();
        };
        MouseLeftButtonUp += (_, _) => Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
