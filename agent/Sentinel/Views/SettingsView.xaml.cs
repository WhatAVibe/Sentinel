using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Sentinel.Models;
using Sentinel.Services;

namespace Sentinel.Views;

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
        Unloaded += (_, _) =>
        {
            _statusTimer.Stop();
            ServerBox.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 };
        };
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
        DeviceIdText.Text = $"Device id Â· {cfg.DeviceId}";

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
            : $"Linked Â· {link.CredentialId[..Math.Min(18, link.CredentialId.Length)]}â€¦";
        AgentVersionText.Text = $"Sentinel agent {AgentInfo.Version}";
        UpdateMasterStatus();

        StartupCheck.IsChecked = StartupManager.IsEnabled();
        TrayCheck.IsChecked = cfg.MinimizeToTray;
        BalloonCheck.IsChecked = cfg.BalloonNotifications;
        UpdateCheckBox.IsChecked = cfg.CheckForUpdates;

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
            case "updatecheck":
                Apply(c => c.CheckForUpdates = value);
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
            foreach (var phone in phones)
            {
                var latest = string.Equals(phone.Kind, "watch", StringComparison.OrdinalIgnoreCase)
                    ? Updates.Current.Watch
                    : Updates.Current.Phone;
                phone.Outdated = !string.IsNullOrEmpty(latest) &&
                                 !string.IsNullOrEmpty(phone.AppVersion) &&
                                 UpdateInfo.Newer(latest, phone.AppVersion);
                _phones.Add(phone);
            }
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
            "Authorize device removal",
            "Removing a device revokes its access immediately. Enter your 2FA code.",
            App.Host) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (MessageBox.Show("Remove this device from the PC?", "Linked devices", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await App.Host.Api.RemovePhoneAsync(id, CancellationToken.None);
            await LoadPhonesAsync();
        }
        catch (ApiException ex) when (ex.Code == "primary_device")
        {
            MessageBox.Show("This is the primary device. Unset primary on this PC before removing it.",
                "Linked devices", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Remove device", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ConfirmPhone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        var dialog = new CodePromptWindow(
            "Confirm linked device",
            "Enter the 6-digit code shown on the new device. Nothing can be triggered until it is confirmed.",
            App.Host) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await App.Host.Api.ConfirmPhoneAsync(id, dialog.Code, CancellationToken.None);
            await LoadPhonesAsync();
            ServerStatus.Text = "Device confirmed.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Confirm device", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RenamePhone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        var phone = _phones.FirstOrDefault(p => p.Id == id);
        var name = PromptText(Window.GetWindow(this), "Rename device", "Device name", phone?.Name ?? "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await App.Host.Api.RenamePhoneAsync(id, name.Trim(), CancellationToken.None);
            await LoadPhonesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Rename device", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void PrimaryPhone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        var phone = _phones.FirstOrDefault(p => p.Id == id);
        if (phone == null) return;
        var makePrimary = !phone.Primary;
        if (makePrimary && MessageBox.Show(
                "A primary device cannot be unlinked by anything until you unset primary on this PC. Continue?",
                "Primary device", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await App.Host.Api.SetPrimaryPhoneAsync(id, makePrimary, CancellationToken.None);
            await LoadPhonesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Primary device", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? PromptText(Window? owner, string title, string label, string initial)
    {
        var window = new Window
        {
            Title = title,
            Width = 430,
            Height = 190,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["Bg"]
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });
        var box = new TextBox { Text = initial, Padding = new Thickness(10, 8, 10, 8) };
        panel.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.Resources["GhostButton"] };
        var save = new Button { Content = "Save", Padding = new Thickness(16, 7, 16, 7), Style = (Style)Application.Current.Resources["PrimaryButton"] };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        window.Content = panel;
        string? result = null;
        save.Click += (_, _) => { result = box.Text; window.DialogResult = true; };
        cancel.Click += (_, _) => window.DialogResult = false;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? result : null;
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
        ServerStatus.Text = "Saved. Reconnectingâ€¦";
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
        ServerStatus.Text = "Contacting serverâ€¦";
        try
        {
            var health = await ApiClient.PingAsync(url, InsecureCheck.IsChecked == true, CancellationToken.None);
            ServerStatus.Text = health == null
                ? "No response."
                : $"Connected to {health.Name} Â· relay v{health.Version}";
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

    private async void Unpair_Click(object sender, RoutedEventArgs e)    {
        if (App.Host.Config.Paired)
        {
            var dialog = new CodePromptWindow(
                "Authorize unpair",
                "Unpairing removes remote access from this PC. Enter your 2FA code to continue.",
                App.Host) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
        }

        if (MessageBox.Show("Remove the pairing from this PC and erase all relay data for it? Every linked device will be signed out.",
                "Unpair", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await App.Host.Api.PurgeDeviceAsync(CancellationToken.None);
        }
        catch
        {
            // offline: still clear locally
        }
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
        AdbDevicesText.Text = "Refreshingâ€¦";
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

    private void ServerBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ServerBox.Effect = null;
    }

    private async Task RefreshRelayVersionAsync()
    {
        var cfg = App.Host.Config;
        if (string.IsNullOrWhiteSpace(cfg.ServerUrl))
        {
            RelayVersionText.Text = "Relay: not configured";
            return;
        }
        RelayVersionText.Text = "Relay: checkingâ€¦";
        try
        {
            var health = await ApiClient.PingAsync(cfg.ServerUrl, cfg.AllowInsecureTls, CancellationToken.None);
            RelayVersionText.Text = health == null
                ? "Relay: no response"
                : $"Relay {health.Version} Â· {health.PublicUrl}";
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
        SecurityKeyText.Text = "Waiting for the key â€” touch it nowâ€¦";
        var (ok, error) = await SecurityKey.LinkAsync(App.Host);
        var link = SecurityKey.Current(App.Host.Config);
        if (ok && link != null)
        {
            SecurityKeyText.Text = $"Linked Â· {link.CredentialId[..Math.Min(18, link.CredentialId.Length)]}â€¦";
            MessageBox.Show("Security key linked. Touch it whenever Sentinel asks to unlock, or use the 2FA code.",
                "Security key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SecurityKeyText.Text = $"Link failed Â· {error}";
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

    private void SaveMaster_Click(object sender, RoutedEventArgs e)
    {
        var password = MasterNewBox.Password;
        if (password.Length < 8)
        {
            MessageBox.Show("Use at least 8 characters.", "Recovery password",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (password != MasterConfirmBox.Password)
        {
            MessageBox.Show("The two passwords do not match.", "Recovery password",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new CodePromptWindow(
            "Authorize recovery password",
            "Confirm with your 2FA code or current recovery password.",
            App.Host) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        App.Host.SetMasterPassword(password);
        MasterNewBox.Clear();
        MasterConfirmBox.Clear();
        UpdateMasterStatus();
        ServerStatus.Text = "Recovery password saved.";
    }

    private void ClearMaster_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Host.HasMasterPassword) return;
        if (MessageBox.Show("Remove the recovery password? A lost device would then leave this PC locked.",
                "Recovery password", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var dialog = new CodePromptWindow(
            "Authorize clearing the recovery password",
            "Confirm with your 2FA code or the current recovery password.",
            App.Host) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        App.Host.ClearMasterPassword();
        UpdateMasterStatus();
        ServerStatus.Text = "Recovery password cleared.";
    }

    private void UpdateMasterStatus()
    {
        if (MasterStatusText == null) return;
        MasterStatusText.Text = App.Host.HasMasterPassword
            ? "Set â€” unlocks secure lock, exit and unpair locally."
            : "Not set â€” only linked devices can unlock this PC.";
        MasterStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            App.Host.HasMasterPassword ? "Success" : "Muted");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Open(App.Host.Store.Root);    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Open(App.Host.Store.LogsDir);

    private void Tutorial_Click(object sender, RoutedEventArgs e) =>
        new TutorialWindow { Owner = Window.GetWindow(this) }.ShowDialog();

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
            ? status + (string.IsNullOrEmpty(host.Runtime.LastError) ? "" : $" Â· {host.Runtime.LastError}")
            : "Not paired. Enter the server address and pair from your device.";

        var adb = host.Adb;
        var devices = adb.Devices.Count == 0
            ? "no devices attached"
            : string.Join(", ", adb.Devices.Select(d => $"{d.Label} [{d.State}]"));
        DirectStatusText.Text = $"Local API {(host.Local.Running ? "listening" : "stopped")} on http://127.0.0.1:{host.Local.Port}" +
                                (host.Local.Running ? " Â· adb reverse " + (adb.ReverseReady ? "ready" : "pending") : "");
        AdbDevicesText.Text = cfg.AdbEnabled
            ? $"ADB: {devices} Â· path: {adb.AdbPath ?? "not found"}"
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

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "Checking GitHub releasesâ€¦";
        var info = await Updates.CheckAsync(true);
        if (info.CheckedAt == 0)
        {
            UpdateStatusText.Text = "Could not reach GitHub. Try again later.";
            return;
        }
        UpdateStatusText.Text =
            $"Current: agent {AgentInfo.Version}. Latest release {info.Tag}: agent {info.Agent}, " +
            $"phone {info.Phone}, watch {info.Watch}. " +
            (info.AnyOutdated ? "Update available â€” use Open releases." : "Everything is up to date.");
    }

    private void OpenReleases_Click(object sender, RoutedEventArgs e) => Updates.OpenReleasePage();

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
