using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QRCoder;

namespace Sentinel.Installer;

public partial class MainWindow : Window
{
    private readonly List<string> _steps = new();
    private readonly List<TextBlock> _stepLabels = new();
    private readonly DispatcherTimer _pairTimer;
    private int _index;
    private readonly bool _updateMode;
    private string _installDir;

    private string _pairRequestId = "";
    private string _pairRequestToken = "";
    private string _agentToken = "";
    private string _totpSecret = "";
    private string _deviceId = "";
    private bool _confirmed;
    private bool _skippedPair;
    private bool _busy;
    private readonly string _dataKey = InstallerCore.NewDataKey();

    public MainWindow()
    {
        InitializeComponent();
        var existing = InstallerCore.FindInstalled();
        _updateMode = existing != null && InstallerCore.HasConfig(existing);
        _installDir = existing ?? InstallerCore.DefaultInstallDir;
        if (_updateMode)
        {
            var existingKey = InstallerCore.ReadConfigDataKey(_installDir);
            if (!string.IsNullOrEmpty(existingKey)) _dataKey = existingKey;
        }
        LocationBox.Text = _installDir;
        ServerBox.Text = TryReadServer(_installDir);
        VersionText.Text = $"Setup {InstallerCore.ProductVersion}";
        ModeText.Text = _updateMode ? "Update / Repair detected" : "";
        FinishTitle.Text = _updateMode ? "Update / Repair" : "Ready to install";
        FinishSummary.Text = _updateMode
            ? "Existing settings, pairing and the recovery password are kept. The agent files will be replaced and the app restarted."
            : "Sentinel will be installed, linked to your relay and started. You can change everything later in Settings.";

        _steps.Add("Welcome");
        if (!_updateMode)
        {
            _steps.Add("License");
            _steps.Add("Location");
            _steps.Add("Security");
            _steps.Add("Relay");
            _steps.Add("Pair");
        }
        else
        {
            _steps.Add("Location");
        }
        _steps.Add("Finish");

        foreach (var step in _steps)
        {
            var label = new TextBlock
            {
                Text = step == "Security" ? "Recovery password" : step == "Pair" ? "Link a device" : step,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10),
                Foreground = (Brush)FindResource("Muted")
            };
            _stepLabels.Add(label);
            StepList.Children.Add(label);
        }

        _pairTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pairTimer.Tick += async (_, _) => await PollPairAsync();
        if (_updateMode)
        {
            ManagePanel.Visibility = Visibility.Visible;
            StartupCheck.IsChecked = InstallerCore.IsStartupEnabled();
            UpdatesCheck.IsChecked = InstallerCore.ReadConfigBool(_installDir, "CheckForUpdates", true);
        }
        ShowStep();
    }

    private static string TryReadServer(string dir)
    {
        var (url, _) = InstallerCore.ReadConfig(dir);
        return string.IsNullOrWhiteSpace(url) ? "" : url;
    }

    private void ShowStep()
    {
        var step = _steps[Math.Clamp(_index, 0, _steps.Count - 1)];
        foreach (var panel in new[] { StepWelcome, StepLicense, StepLocation, StepSecurity, StepRelay, StepPair, StepFinish })
            panel.Visibility = Visibility.Collapsed;
        StepWelcome.Visibility = step == "Welcome" ? Visibility.Visible : Visibility.Collapsed;
        StepLicense.Visibility = step == "License" ? Visibility.Visible : Visibility.Collapsed;
        StepLocation.Visibility = step == "Location" ? Visibility.Visible : Visibility.Collapsed;
        StepSecurity.Visibility = step == "Security" ? Visibility.Visible : Visibility.Collapsed;
        StepRelay.Visibility = step == "Relay" ? Visibility.Visible : Visibility.Collapsed;
        StepPair.Visibility = step == "Pair" ? Visibility.Visible : Visibility.Collapsed;
        StepFinish.Visibility = step == "Finish" ? Visibility.Visible : Visibility.Collapsed;

        for (var i = 0; i < _stepLabels.Count; i++)
        {
            _stepLabels[i].Foreground = (Brush)FindResource(i == _index ? "Accent" : "Muted");
            _stepLabels[i].FontWeight = i == _index ? FontWeights.SemiBold : FontWeights.Normal;
        }

        BackButton.IsEnabled = _index > 0;
        NextButton.Content = step == "Finish" ? (_updateMode ? "Update" : "Install") : "Next";
        NextButton.IsEnabled = !_busy;
        FooterStatus.Text = "";
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var step = _steps[_index];
        if (step == "License" && AcceptCheck.IsChecked != true)
        {
            FooterStatus.Text = "Accept the terms to continue.";
            return;
        }
        if (step == "Location")
        {
            var path = LocationBox.Text.Trim();
            if (path.Length == 0)
            {
                FooterStatus.Text = "Choose an install folder.";
                return;
            }
            _installDir = path;
            if (_updateMode && !InstallerCore.HasConfig(_installDir))
            {
                FooterStatus.Text = "No config found there; choose the existing Sentinel folder or run a fresh install.";
                return;
            }
        }
        if (step == "Security")
        {
            if (MasterBox.Password.Length < 8)
            {
                FooterStatus.Text = "Use at least 8 characters for the recovery password.";
                return;
            }
            if (MasterBox.Password != MasterConfirmBox.Password)
            {
                FooterStatus.Text = "The passwords do not match.";
                return;
            }
        }
        if (step == "Relay")
        {
            if (!IsValidUrl(ServerBox.Text))
            {
                FooterStatus.Text = "Enter a valid http(s) relay address.";
                return;
            }
        }
        if (step == "Pair" && !_confirmed && !_skippedPair)
        {
            FooterStatus.Text = "Pair a device, or choose Skip to do it later.";
            return;
        }
        if (step == "Finish")
        {
            await InstallAsync();
            return;
        }
        _index++;
        ShowStep();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_index == 0) return;
        _index--;
        ShowStep();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the Sentinel folder" };
        if (dialog.ShowDialog() == true) LocationBox.Text = dialog.FolderName;
    }

    private void RelayHelp_Click(object sender, RoutedEventArgs e) => InstallerCore.OpenUrl(InstallerCore.RelayGuideUrl);

    private static bool IsValidUrl(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return false;
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string NormalizeUrl(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;
        return text.TrimEnd('/');
    }

    private async void StartPair_Click(object sender, RoutedEventArgs e)
    {
        if (!IsValidUrl(ServerBox.Text))
        {
            PairStatusText.Text = "Enter the relay address first.";
            return;
        }
        StartPairButton.IsEnabled = false;
        PairStatusText.Text = "Contacting the relay…";
        try
        {
            var response = await InstallerCore.PairRequestAsync(NormalizeUrl(ServerBox.Text), Environment.MachineName);
            _pairRequestId = response.TryGetProperty("request_id", out var rid) ? rid.GetString() ?? "" : "";
            _pairRequestToken = response.TryGetProperty("request_token", out var token) ? token.GetString() ?? "" : "";
            PairCodeText.Text = response.TryGetProperty("pair_code", out var code) ? code.GetString() ?? "" : "";
            PairStatusText.Text = "Open Sentinel on the device and enter this code, or scan the QR code. Waiting for approval…";
            RenderPairQr(NormalizeUrl(ServerBox.Text), _pairRequestId, PairCodeText.Text, _dataKey);
            _pairTimer.Start();
        }
        catch (Exception ex)
        {
            StartPairButton.IsEnabled = true;
            PairStatusText.Text = "Pairing failed: " + ex.Message;
        }
    }

    private async Task PollPairAsync()
    {
        if (string.IsNullOrEmpty(_pairRequestId)) return;
        try
        {
            var response = await InstallerCore.PairStatusAsync(
                NormalizeUrl(ServerBox.Text), _pairRequestId, _pairRequestToken);
            var status = response.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            if (status == "approved" && string.IsNullOrEmpty(_agentToken))
            {
                _pairTimer.Stop();
                _deviceId = response.TryGetProperty("device_id", out var did) ? did.GetString() ?? "" : "";
                _agentToken = response.TryGetProperty("agent_token", out var at) ? at.GetString() ?? "" : "";
                _totpSecret = response.TryGetProperty("totp_secret", out var ts) ? ts.GetString() ?? "" : "";
                PairStatusText.Text = "Device approved. Enter the 6-digit code shown on the device.";
                ConfirmButton.IsEnabled = true;
                ConfirmCodeBox.Focus();
            }
            else if (status == "claimed" && string.IsNullOrEmpty(_agentToken))
            {
                _pairTimer.Stop();
                PairStatusText.Text = "This pairing was already claimed. Start pairing again.";
                StartPairButton.IsEnabled = true;
            }
            else if (status == "expired")
            {
                _pairTimer.Stop();
                PairStatusText.Text = "The pairing code expired. Start pairing again.";
                StartPairButton.IsEnabled = true;
            }
        }
        catch
        {
            // keep waiting; transient network errors
        }
    }

    private async void ConfirmDevice_Click(object sender, RoutedEventArgs e)
    {
        var code = ConfirmCodeBox.Text.Trim();
        if (code.Length != 6)
        {
            PairStatusText.Text = "Enter the six digits shown on the device.";
            return;
        }
        ConfirmButton.IsEnabled = false;
        try
        {
            var phones = await InstallerCore.PhonesAsync(NormalizeUrl(ServerBox.Text), _agentToken);
            var pending = phones.FirstOrDefault(p => !p.Confirmed);
            if (pending == null)
            {
                _confirmed = true;
                PairStatusText.Text = "Device confirmed.";
                ConfirmButton.IsEnabled = false;
                return;
            }
            await InstallerCore.ConfirmPhoneAsync(NormalizeUrl(ServerBox.Text), _agentToken, pending.Id, code);
            _confirmed = true;
            PairStatusText.Text = "Device confirmed. You can continue.";
            NextButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ConfirmButton.IsEnabled = true;
            PairStatusText.Text = ex.Message == "bad_code"
                ? "That code is not valid right now. Use the current code on the device."
                : "Confirmation failed: " + ex.Message;
        }
    }

    private void RenderPairQr(string serverUrl, string requestId, string pairCode, string dataKey)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                v = 2,
                u = serverUrl,
                r = requestId,
                c = pairCode,
                n = Environment.MachineName,
                k = dataKey
            });
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(data).GetGraphic(8);
            using var stream = new MemoryStream(png);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            PairQr.Source = image;
            PairQr.Visibility = Visibility.Visible;
        }
        catch
        {
            PairQr.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateMode_Click(object sender, RoutedEventArgs e)
    {
        _index = _steps.IndexOf("Location");
        ShowStep();
    }

    private void UninstallExisting_Click(object sender, RoutedEventArgs e)
    {
        var agent = InstallerCore.AgentExe(_installDir);
        if (!File.Exists(agent))
        {
            FooterStatus.Text = "No installed agent found at that location.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = agent,
                Arguments = "--uninstall",
                WorkingDirectory = _installDir,
                UseShellExecute = true
            });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "Could not start the uninstaller: " + ex.Message;
        }
    }

    private async void SkipPair_Click(object sender, RoutedEventArgs e)
    {
        _skippedPair = true;
        PairStatusText.Text = "Skipped. Link a device later from Settings → Linked devices.";
    }

    private async Task InstallAsync()
    {
        _busy = true;
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        FooterStatus.Text = "Installing…";
        try
        {
            InstallProgress.Visibility = Visibility.Visible;
            InstallProgress.Value = 5;
            ProgressText.Text = "Stopping the running agent…";
            await Task.Run(InstallerCore.StopAgent);
            await Task.Delay(300);
            Directory.CreateDirectory(_installDir);

            ProgressText.Text = "Extracting Sentinel…";
            InstallProgress.Value = 15;
            await Task.Run(() => InstallerCore.InstallPayload(_installDir));
            InstallProgress.Value = 65;

            var startup = StartupCheck.IsChecked == true;
            var updates = UpdatesCheck.IsChecked == true;
            if (!_updateMode)
            {
                ProgressText.Text = "Writing settings and pairing…";
                var serverUrl = NormalizeUrl(ServerBox.Text);
                var deviceId = _deviceId;
                var agentToken = _agentToken;
                var totpSecret = _totpSecret;
                var master = MasterBox.Password;
                await Task.Run(() => InstallerCore.WriteConfig(
                    _installDir, serverUrl, deviceId, agentToken, totpSecret, master, _dataKey, updates, startup, false));
            }
            else
            {
                ProgressText.Text = "Updating settings…";
                await Task.Run(() =>
                {
                    SetConfigBool(_installDir, "CheckForUpdates", updates);
                    SetConfigBool(_installDir, "StartWithWindows", startup);
                    // Update/repair keeps the existing DEK; only a missing one is filled in.
                    InstallerCore.EnsureDataKey(_installDir, _dataKey);
                });
            }
            InstallProgress.Value = 80;

            ProgressText.Text = "Registering startup and the uninstaller…";
            InstallerCore.ConfigureStartup(_installDir, startup);
            InstallerCore.RegisterUninstall(_installDir);
            InstallProgress.Value = 95;

            FooterStatus.Text = "Installed. Starting Sentinel…";
            ProgressText.Text = "Starting Sentinel…";
            if (LaunchCheck.IsChecked == true) InstallerCore.LaunchAgent(_installDir);
            InstallProgress.Value = 100;
            await Task.Delay(600);
            Close();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "Install failed: " + ex.Message;
            NextButton.IsEnabled = true;
            BackButton.IsEnabled = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private static void SetConfigBool(string dir, string key, bool value)
    {
        var path = InstallerCore.ConfigPath(dir);
        if (!File.Exists(path)) return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (node == null) return;
            node[key] = value;
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // leave the existing config untouched
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _pairTimer.Stop();
        base.OnClosed(e);
    }
}
