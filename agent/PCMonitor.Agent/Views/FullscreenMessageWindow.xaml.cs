using System.Windows;

namespace PCMonitor.Agent.Views;

public partial class FullscreenMessageWindow : Window
{
    public FullscreenMessageWindow(string title, string body)
    {
        InitializeComponent();
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Message from your phone" : title;
        BodyText.Text = body;
        Loaded += (_, _) =>
        {
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = Math.Max(200, SystemParameters.VirtualScreenWidth);
            Height = Math.Max(200, SystemParameters.VirtualScreenHeight);
            Activate();
        };
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Close();
}
