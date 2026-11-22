using System.Windows;

namespace Sentinel.Views;

public partial class TutorialWindow : Window
{
    private static readonly (string Title, string Body)[] Steps =
    {
        ("Dashboard",
            "The dashboard shows this PC's status, CPU and memory. The 2FA code stays on your phone and watch; this PC only verifies it. Use the quick actions to lock, take screenshots, arm away mode and more."),
        ("Activity",
            "Activity history is stored on your relay and shown here, with the device that triggered each action (for example Phone or Watch). Filter by category and load older pages."),
        ("Screenshots",
            "Captures are stored on your relay and locally. Open them full size and use Purge from relay if something sensitive was captured."),
        ("Linked devices",
            "Pair phones and watches here. Confirm a new device with the code shown on it, rename it, or mark it primary so no other device can unlink it. The list shows each device's app version and flags outdated builds."),
        ("Security and updates",
            "Set a recovery password for local unlocks, link a FIDO2 security key, and check for updates. Nothing is installed automatically; you get a link to the release page.")
    };

    private int _index;

    public TutorialWindow()
    {
        InitializeComponent();
        ShowStep();
    }

    private void ShowStep()
    {
        var (title, body) = Steps[_index];
        TitleText.Text = title;
        BodyText.Text = body;
        StepCount.Text = $"{_index + 1} of {Steps.Length}";
        NextButton.Content = _index == Steps.Length - 1 ? "Done" : "Next";
        SkipButton.Visibility = _index == Steps.Length - 1 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Finish();

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_index >= Steps.Length - 1)
        {
            Finish();
            return;
        }
        _index++;
        ShowStep();
    }

    private void Finish()
    {
        App.Host.Config.TutorialDone = true;
        App.Host.SaveConfig();
        Close();
    }
}
