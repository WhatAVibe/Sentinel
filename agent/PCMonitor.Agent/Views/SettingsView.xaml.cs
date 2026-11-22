using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using PCMonitor.Agent.Models;
using PCMonitor.Agent.Services;

namespace PCMonitor.Agent.Views;

public partial class SettingsView : UserControl
{
    private bool _loading;
    private readonly DispatcherTimer _statusTimer;
    private readonly ObservableCollection<PhoneDto> _phones = new();

    public SettingsView()
    {
        InitializeComponent();
        DataContext = UiState.I;
        PhonesList.ItemsSource = _phones;
        Loaded += OnLoaded;
        Unloaded += (_, _) => _statusTimer.Stop();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshLiveStatus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadFromConfig();
        RefreshLiveStatus();
        _statusTimer.Start();
        _ = LoadPhonesAsync();
        _ = RefreshRelayVersionAsync();
    }

    private void LoadFromConfig()
    {
        _loading = true;
        var cfg = App.Host.Config;
        ServerBox.Text = cfg.ServerUrl;
        DeviceNameBox.Text = cfg.DeviceName;
        DeviceIdText.Text = $"Device id · {cfg.DeviceId}";

        ScreensaverCheck.IsChecked = cfg.AwayScreensaver;
        LockAfterBox.Text = cfg.LockAfterAwaySeconds.ToString();
        AutoShotsCheck.IsChecked = cfg.AutoScreenshotInAway;
        AwayShotMinBox.Text = cfg.AwayShotMinSeconds.ToString();
        AwayShotMaxBox.Text = cfg.AwayShotMaxSeconds.ToString();
        ActivityShotsCheck.IsChecked = cfg.AwayShotOnActivity;
        MonitorAwayCheck.IsChecked = cfg.MonitorOnlyInAway;
        AutoIdleCheck.IsChecked = cfg.AutoAwayOnIdle;
        AutoIdleBox.Text = cfg.AutoAwayIdleMinutes.ToString();
        AutoPhoneCheck.IsChecked = cfg.AutoAwayOnPhoneDisconnect;

        ClipboardCheck.IsChecked = cfg.CaptureClipboard;
        UsbCheck.IsChecked = cfg.CaptureUsb;
        ProcessCheck.IsChecked = cfg.CaptureProcesses;
        MouseCheck.IsChecked = cfg.CaptureMouse;
        KeyboardCheck.IsChecked = cfg.CaptureKeystrokes;
        NetworkCheck.IsChecked = cfg.CaptureNetwork;
        SearchCheck.IsChecked = cfg.DetectSearches;

        PowerSilent.IsChecked = cfg.PowerActionMode == "silent";
        PowerNotify.IsChecked = cfg.PowerActionMode == "notify";
        PowerConfirm.IsChecked = cfg.PowerActionMode == "confirm";
        PowerMessageBox.Text = cfg.PowerActionMessage;
        PopupSecondsBox.Text = cfg.MessagePopupSeconds.ToString();

        AdbCheck.IsChecked = cfg.AdbEnabled;
        AdbPathBox.Text = cfg.AdbPath;
        LocalPortBox.Text = cfg.LocalPort.ToString();

        PresenceCheck.IsChecked = cfg.RequirePhonePresence;
        ProtectPowerCheck.IsChecked = cfg.ProtectPowerCommands;
        InsecureCheck.IsChecked = cfg.AllowInsecureTls;

        var link = SecurityKey.Current(cfg);
        SecurityKeyText.Text = link == null
            ? (SecurityKey.Available ? "No key linked" : "This Windows build has no WebAuthn API")
            : $"Linked · {link.CredentialId[..Math.Min(18, link.CredentialId.Length)]}…";
        AgentVersionText.Text = $"Sentinel agent {AgentInfo.Version} · protocol {AgentInfo.Protocol}";

        StartupCheck.IsChecked = StartupManager.IsEnabled();
        TrayCheck.IsChecked = cfg.MinimizeToTray;
        BalloonCheck.IsChecked = cfg.BalloonNotifications;

        DirectCodeText.Text = App.Host.EnsureLocalPairCode();
        _loading = false;
    }

    private void Apply(Action<AppConfig> mutate, bool reconnect = false)
    {
        if (_loading) return;
        mutate(App.Host.Config);
        App.Host.SaveConfig();
        if (reconnect) App.Host.RestartConnectors();
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox box) return;
        var value = box.IsChecked == true;
        switch (box.Tag as string)
        {
            case "screensaver":
                Apply(c => c.AwayScreensaver = value);
                break;
            case "autoshots":
                Apply(c => c.AutoScreenshotInAway = value);
                break;
            case "monitoraway":
                Apply(c => c.MonitorOnlyInAway = value);
                break;
            case "autoidle":
                Apply(c => c.AutoAwayOnIdle = value);
                break;
            case "autophone":
                Apply(c => c.AutoAwayOnPhoneDisconnect = value);
                break;
            case "clipboard":
                Apply(c => c.CaptureClipboard = value);
                break;
            case "usb":
                Apply(c => c.CaptureUsb = value);
                break;
            case "process":
                Apply(c => c.CaptureProcesses = value);
                break;
            case "mouse":
                Apply(c => c.CaptureMouse = value);
                App.Host.Monitor.Stop();
                App.Host.Monitor.Start();
                break;
            case "keys":
                Apply(c => c.CaptureKeystrokes = value);
                App.Host.Monitor.Stop();
                App.Host.Monitor.Start();
                break;
            case "network":
                Apply(c => c.CaptureNetwork = value);
                App.Host.Monitor.Stop();
                App.Host.Monitor.Start();
                break;
            case "search":
                Apply(c => c.DetectSearches = value);
                break;
            case "activityshots":
                Apply(c => c.AwayShotOnActivity = value);
                break;
            case "presence":
                Apply(c => c.RequirePhonePresence = value);
                break;
            case "protectpower":
                Apply(c => c.ProtectPowerCommands = value);
                _ = App.Host.PushSettingsAsync();
                break;
            case "insecure":
                Apply(c => c.AllowInsecureTls = value, reconnect: true);
                break;
            case "adb":
                Apply(c => c.AdbEnabled = value);
                if (value) App.Host.Adb.Start();
                else App.Host.Adb.Stop();
                break;
            case "startup":
                StartupManager.Set(value);
                Apply(c => c.StartWithWindows = value);
                break;
            case "tray":
                Apply(c => c.MinimizeToTray = value);
                break;
            case "balloon":
                Apply(c => c.BalloonNotifications = value);
                break;
        }
    }

    private void Number_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not TextBox box) return;
        if (!int.TryParse(box.Text.Trim(), out var value)) return;
        switch (box.Tag as string)
        {
            case "lockafter":
                value = Math.Clamp(value, 0, 3600);
                Apply(c => c.LockAfterAwaySeconds = value);
                break;
            case "awayshotmin":
                value = Math.Clamp(value, 10, 3600);
                Apply(c =>
                {
                    c.AwayShotMinSeconds = value;
                    if (c.AwayShotMaxSeconds < value) c.AwayShotMaxSeconds = value + 30;
                });
                AwayShotMaxBox.Text = App.Host.Config.AwayShotMaxSeconds.ToString();
                break;
            case "awayshotmax":
                value = Math.Clamp(value, 15, 3600);
                Apply(c =>
                {
                    c.AwayShotMaxSeconds = value;
                    if (c.AwayShotMinSeconds > value) c.AwayShotMinSeconds = Math.Max(10, value - 30);
                });
                AwayShotMinBox.Text = App.Host.Config.AwayShotMinSeconds.ToString();
                break;
            case "popupseconds":
                value = Math.Clamp(value, 3, 600);
                Apply(c => c.MessagePopupSeconds = value);
                break;
            case "autoidle":
                value = Math.Clamp(value, 1, 240);
                Apply(c => c.AutoAwayIdleMinutes = value);
                break;
            case "localport":
                value = Math.Clamp(value, 1024, 65000);
                Apply(c => c.LocalPort = value, reconnect: true);
                break;
        }
        box.Text = value.ToString();
    }

    private async Task LoadPhonesAsync()
    {
        if (!App.Host.Config.Paired)
        {
            _phones.Clear();
            PhonesEmpty.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            var phones = await App.Host.Api.GetPhonesAsync(CancellationToken.None);
            _phones.Clear();
            foreach (var phone in phones) _phones.Add(phone);
            PhonesEmpty.Visibility = _phones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ServerStatus.Text = "Linked phones: " + ex.Message;
        }
    }

    private async void RefreshPhones_Click(object sender, RoutedEventArgs e) => await LoadPhonesAsync();

    private async void RemovePhone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        var dialog = new CodePromptWindow(
            "Authorize phone removal",
            "Removing a phone revokes its access immediately. Enter your 2FA code.",
            App.Host) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (MessageBox.Show("Remove this phone from the PC?", "Linked phones", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await App.Host.Api.RemovePhoneAsync(id, CancellationToken.None);
            await LoadPhonesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Remove phone", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AdbPath_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Apply(c => c.AdbPath = AdbPathBox.Text.Trim(), reconnect: true);
    }

    private void PowerMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton radio) return;
        var mode = radio.Tag as string ?? "confirm";
        Apply(c => c.PowerActionMode = mode);
        _ = App.Host.PushSettingsAsync();
    }

    private void PowerMessage_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Apply(c => c.PowerActionMessage = PowerMessageBox.Text.Trim());
        _ = App.Host.PushSettingsAsync();
    }

    private async void SaveServer_Click(object sender, RoutedEventArgs e)
    {
        var url = Normalize(ServerBox.Text);
        if (url == null)
        {
            ServerStatus.Text = "Enter a valid http(s) address.";
            return;
        }
        Apply(c => c.ServerUrl = url, reconnect: true);
        ServerBox.Text = url;
        ServerStatus.Text = "Saved. Reconnecting…";
        await Task.Delay(600);
        RefreshLiveStatus();
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var url = Normalize(ServerBox.Text);
        if (url == null)
        {
            ServerStatus.Text = "Enter a valid http(s) address.";
            return;
        }
        ServerStatus.Text = "Contacting server…";
        try
        {
            var health = await ApiClient.PingAsync(url, InsecureCheck.IsChecked == true, CancellationToken.None);
            ServerStatus.Text = health == null
                ? "No response."
                : $"Connected to {health.Name} v{health.Version} · {health.PublicUrl}";
        }
        catch (Exception ex)
        {
            ServerStatus.Text = "Failed: " + ex.Message;
        }
    }

    private void AddPhone_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Host.Config.Paired)
        {
            MessageBox.Show("Pair this PC first.", "Sentinel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new PhoneLinkWindow { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void Unpair_Click(object sender, RoutedEventArgs e)
    {
        if (App.Host.Config.Paired)
        {
            var dialog = new CodePromptWindow(
                "Authorize unpair",
                "Unpairing removes remote access from this PC. Enter your 2FA code to continue.",
                App.Host) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
        }

        if (MessageBox.Show("Remove the pairing from this PC? You will need to pair again.", "Unpair",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        App.Host.Unpair();
        LoadFromConfig();
        RefreshLiveStatus();
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var name = DeviceNameBox.Text.Trim();
        if (name.Length < 2) return;
        Apply(c => c.DeviceName = name);
        UiState.I.DeviceName = name;
        App.Tray.Update();
        App.Host.PublishName();
        ServerStatus.Text = $"Renamed to {name}. Phones see it on the next refresh.";
    }

    private void BrowseAdb_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select adb.exe",
            Filter = "adb.exe|adb.exe|Executables|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            AdbPathBox.Text = dialog.FileName;
            Apply(c => c.AdbPath = dialog.FileName, reconnect: true);
        }
    }

    private async void RefreshAdb_Click(object sender, RoutedEventArgs e)
    {
        AdbDevicesText.Text = "Refreshing…";
        await App.Host.Adb.RefreshAsync();
        RefreshLiveStatus();
    }

    private async void ConnectAdb_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = AdbEndpointBox.Text.Trim();
        if (endpoint.Length == 0)
        {
            AdbDevicesText.Text = "Enter ip:port from wireless debugging.";
            return;
        }
        AdbDevicesText.Text = await App.Host.Adb.ConnectAsync(endpoint);
        RefreshLiveStatus();
    }

    private async void PairAdb_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = AdbPairEndpointBox.Text.Trim();
        var code = AdbPairCodeBox.Text.Trim();
        if (endpoint.Length == 0 || code.Length == 0)
        {
            AdbDevicesText.Text = "Enter the pairing ip:port and the 6-digit wireless debugging code.";
            return;
        }
        AdbDevicesText.Text = await App.Host.Adb.PairAsync(endpoint, code);
        AdbPairCodeBox.Text = "";
        RefreshLiveStatus();
    }

    private void NewCode_Click(object sender, RoutedEventArgs e)
    {
        App.Host.InvalidateLocalPairCode();
        DirectCodeText.Text = App.Host.EnsureLocalPairCode();
    }

    private async Task RefreshRelayVersionAsync()
    {
        var cfg = App.Host.Config;
        if (string.IsNullOrWhiteSpace(cfg.ServerUrl))
        {
            RelayVersionText.Text = "Relay: not configured";
            return;
        }
        RelayVersionText.Text = "Relay: checking…";
        try
        {
            var health = await ApiClient.PingAsync(cfg.ServerUrl, cfg.AllowInsecureTls, CancellationToken.None);
            RelayVersionText.Text = health == null
                ? "Relay: no response"
                : $"Relay {health.Version} · {health.PublicUrl}";
        }
        catch (Exception ex)
        {
            RelayVersionText.Text = "Relay: " + ex.Message;
        }
    }

    private async void LinkKey_Click(object sender, RoutedEventArgs e)
    {
        if (!SecurityKey.Available)
        {
            MessageBox.Show("This Windows build does not expose the WebAuthn API. Windows 10 1903 or newer is required.",
                "Security key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SecurityKeyText.Text = "Waiting for the key — touch it now…";
        var (ok, error) = await SecurityKey.LinkAsync(App.Host);
        var link = SecurityKey.Current(App.Host.Config);
        if (ok && link != null)
        {
            SecurityKeyText.Text = $"Linked · {link.CredentialId[..Math.Min(18, link.CredentialId.Length)]}…";
            MessageBox.Show("Security key linked. Touch it whenever Sentinel asks to unlock, or use the 2FA code.",
                "Security key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SecurityKeyText.Text = $"Link failed · {error}";
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (SecurityKey.Current(App.Host.Config) == null)
        {
            SecurityKeyText.Text = "No key linked";
            return;
        }
        if (MessageBox.Show("Unlink the security key from this PC?", "Security key",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        App.Host.Config.ProtectedSecurityKey = "";
        App.Host.SaveConfig();
        SecurityKeyText.Text = "No key linked";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Open(App.Host.Store.Root);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Open(App.Host.Store.LogsDir);

    private async void TestAlarm_Click(object sender, RoutedEventArgs e)
    {
        await App.Host.RunLocalAsync("alarm");
    }

    private void RefreshLiveStatus()
    {
        var host = App.Host;
        var cfg = host.Config;
        DirectCodeText.Text = host.EnsureLocalPairCode();

        var status = host.Runtime.Status;
        ServerStatus.Text = cfg.Paired
            ? $"{(string.IsNullOrEmpty(cfg.ServerUrl) ? "no server" : cfg.ServerUrl)} · {status}" +
              (string.IsNullOrEmpty(host.Runtime.LastError) ? "" : $" · {host.Runtime.LastError}")
            : "Not paired. Enter the server address and pair from your phone.";

        var adb = host.Adb;
        var devices = adb.Devices.Count == 0
            ? "no devices attached"
            : string.Join(", ", adb.Devices.Select(d => $"{d.Label} [{d.State}]"));
        DirectStatusText.Text = $"Local API {(host.Local.Running ? "listening" : "stopped")} on http://127.0.0.1:{host.Local.Port}" +
                                (host.Local.Running ? " · adb reverse " + (adb.ReverseReady ? "ready" : "pending") : "");
        AdbDevicesText.Text = cfg.AdbEnabled
            ? $"ADB: {devices} · path: {adb.AdbPath ?? "not found"}"
            : "Direct link disabled.";
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

    private static void Open(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignored
        }
    }
}
