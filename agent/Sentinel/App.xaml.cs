using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Sentinel.Services;
using Sentinel.Views;

namespace Sentinel;

public partial class App : Application
{
    public static AgentHost Host { get; private set; } = null!;
    public static TrayService Tray { get; private set; } = null!;
    public static new App Current => (App)Application.Current;

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private MainWindow? _main;
    private DispatcherTimer? _deviceWatcher;
    private readonly HashSet<string> _promptedDevices = new();
    private bool _confirmOpen;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => string.Equals(a, "--supervise", StringComparison.OrdinalIgnoreCase)))
        {
            Host = new AgentHost();
            Watchdog.Run(0);
            Host.Dispose();
            Shutdown();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Host = new AgentHost();
            var window = new UninstallWindow();
            window.Show();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--test-key", StringComparison.OrdinalIgnoreCase)))
        {
            Host = new AgentHost();
            var keyWindow = new SecurityKeyWindow(register: true, credentialId: "");
            keyWindow.ShowDialog();
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--security-key", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown();
            return;
        }

        var watchdogIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--watchdog", StringComparison.OrdinalIgnoreCase));
        if (watchdogIndex >= 0 && watchdogIndex + 1 < e.Args.Length && int.TryParse(e.Args[watchdogIndex + 1], out var watchedPid))
        {
            Watchdog.Run(watchedPid);
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, @"Local\Sentinel.Instance", out var isNew);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Sentinel.Show");
        if (!isNew)
        {
            try { _showSignal.Set(); } catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            try { Host?.Log.Write("error", "unhandled", args.Exception.ToString()); } catch { }
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var output = Path.Combine(Path.GetTempPath(), "sentinel-selftest.json");
            var index = Array.FindIndex(e.Args, a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index + 1 < e.Args.Length && !e.Args[index + 1].StartsWith("--")) output = e.Args[index + 1];
            Host = new AgentHost();
            var exitCode = Task.Run(() => SelfTest.Run(Host, output)).GetAwaiter().GetResult();
            Host.Dispose();
            Environment.Exit(exitCode);
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--pair-server", StringComparison.OrdinalIgnoreCase)))
        {
            var index = Array.FindIndex(e.Args, a => string.Equals(a, "--pair-server", StringComparison.OrdinalIgnoreCase));
            var url = index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : "";
            var reportIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--pair-report", StringComparison.OrdinalIgnoreCase));
            var report = reportIndex >= 0 && reportIndex + 1 < e.Args.Length
                ? e.Args[reportIndex + 1]
                : Path.Combine(Path.GetTempPath(), "sentinel-pair.json");
            var insecure = e.Args.Contains("--insecure-tls", StringComparer.OrdinalIgnoreCase);
            Host = new AgentHost();
            var code = Task.Run(() => HeadlessPair.RunAsync(Host, url, report, insecure)).GetAwaiter().GetResult();
            Host.Dispose();
            Environment.Exit(code);
            return;
        }

        Host = new AgentHost();
        Host.Notice += (title, body) => Tray?.ShowBalloon(title, body);
        Host.ToastRequested += text => UiState.Post(() => new ToastWindow("Message from your phone", text).Show());
        Tray = new TrayService(Host, ShowDashboard, ShowSettings, RequestDisarm, RequestExit);
        Host.Start();
        StartWatchdog();
        Host.RestoreSecureLockIfNeeded();

        var startHidden = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (!startHidden)
        {
            if (Host.Config.Paired) ShowDashboard();
            else ShowPairing();
            UiState.Post(MaybeShowTutorial);
        }

        StartShowListener();
        StartDeviceWatcher();
    }

    private void StartDeviceWatcher()
    {
        _deviceWatcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _deviceWatcher.Tick += async (_, _) => await CheckPendingDevicesAsync();
        _deviceWatcher.Start();
    }

    private async Task CheckPendingDevicesAsync()
    {
        if (_shuttingDown || _confirmOpen || !Host.Config.Paired) return;
        if (string.IsNullOrEmpty(Host.Api.Token) || SecureLock.Active) return;
        if (Windows.OfType<Window>().Any(w => w.IsVisible &&
                w is PhoneLinkWindow or PairWindow or TutorialWindow or CodePromptWindow))
            return;
        List<Models.PhoneDto> phones;
        try
        {
            phones = await Host.Api.GetPhonesAsync(CancellationToken.None);
        }
        catch
        {
            return;
        }
        var pending = phones.FirstOrDefault(p => !p.Confirmed && !_promptedDevices.Contains(p.Id));
        foreach (var phone in phones.Where(p => p.Confirmed)) _promptedDevices.Remove(phone.Id);
        if (pending == null) return;
        _promptedDevices.Add(pending.Id);
        _confirmOpen = true;
        try
        {
            var window = new DeviceConfirmWindow(Host, pending);
            if (_main is { IsVisible: true }) window.Owner = _main;
            var confirmed = window.ShowDialog() == true;
            Tray?.ShowBalloon(confirmed ? "Device confirmed" : "Device still pending",
                confirmed
                    ? $"{pending.Name} can now control this PC."
                    : $"{pending.Name} stays locked until you enter its code in Settings.");
        }
        finally
        {
            _confirmOpen = false;
        }
    }

    public void ShowDashboard()
    {
        UiState.Post(() =>
        {
            if (_shuttingDown) return;
            if (_main == null)
            {
                _main = new MainWindow();
                _main.Closed += (_, _) => _main = null;
                _main.Show();
            }
            else
            {
                if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
                _main.Show();
                _main.Activate();
            }
        });
    }

    public void ShowSettings()
    {
        ShowDashboard();
        UiState.Post(() => _main?.NavigateTo("settings"));
    }

    public void ShowPairing()
    {
        UiState.Post(() =>
        {
            if (_shuttingDown) return;
            var window = new PairWindow { Owner = _main };
            var result = window.ShowDialog();
            if (result == true)
            {
                ShowDashboard();
                Tray.Update();
                UiState.Post(MaybeShowTutorial);
                return;
            }
            if (_main == null) ShowDashboard();
        });
    }

    public void MaybeShowTutorial()
    {
        if (Host.Config.TutorialDone || !Host.Config.Paired) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_main == null || Host.Config.TutorialDone || !Host.Config.Paired) return;
            new TutorialWindow { Owner = _main }.ShowDialog();
        };
        timer.Start();
    }

    public void RequestDisarm()    {
        UiState.Post(() => _ = DisarmFlowAsync());
    }

    private async Task DisarmFlowAsync()
    {
        if (!Host.Away)
        {
            ShowDashboard();
            return;
        }

        if (!Host.CanDisarm(out var reason))
        {
            MessageBox.Show(reason, "Sentinel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new CodePromptWindow(
            "Unlock / disable away mode",
            "Enter the 6-digit code from Sentinel on your phone, or type it from your hardware token.",
            Host) { Owner = _main };
        if (dialog.ShowDialog() != true) return;

        var outcome = await Host.DisableAwayAsync("desktop", dialog.Code);
        if (!outcome.Ok)
        {
            var message = outcome.Error switch
            {
                "bad_code" => "That code is not valid right now.",
                "phone_not_present" => "Phone presence is required. Connect your phone over ADB first.",
                _ => outcome.Error ?? "Could not disable away mode."
            };
            MessageBox.Show(message, "Sentinel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void RequestExit()
    {
        UiState.Post(() =>
        {
            if (_shuttingDown) return;

            if (!Host.Config.Paired)
            {
                var confirm = MessageBox.Show(
                    "Exit Sentinel? Remote control will be unavailable until you open it again.",
                    "Exit Sentinel",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm == MessageBoxResult.Yes) ExitApp(true);
                return;
            }

            var dialog = new CodePromptWindow(
                "Authorize exit",
                "Closing Sentinel needs your 2FA code. The watchdog stops only after a valid code.",
                Host) { Owner = _main };
            if (dialog.ShowDialog() != true) return;

            ExitGuard.Authorize();
            ExitApp(true);
        });
    }

    private void StartWatchdog()
    {
        if (!Host.Config.Paired) return;
        try
        {
            using var existing = Mutex.OpenExisting(@"Local\Sentinel.Watchdog");
            return;
        }
        catch
        {
            // not running yet
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            // Detach through the shell so Task Manager's "End task" on the agent does not
            // terminate the supervisor with the agent's process tree.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"\" \"{exe}\" --supervise",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            // watchdog is best-effort
        }
    }

    public void ExitApp(bool authorized = false)
    {
        if (_shuttingDown) return;
        if (!authorized)
        {
            RequestExit();
            return;
        }
        ExitGuard.Authorize();
        SecureLock.Deactivate();
        _shuttingDown = true;
        Sentinel.MainWindow.AllowClose = true;
        try { _main?.Close(); } catch { }
        try { Host?.Dispose(); } catch { }
        try { Tray?.Dispose(); } catch { }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Host?.Dispose(); } catch { }
        _showSignal?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void StartShowListener()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (_showSignal == null) return;
                    _showSignal.WaitOne();
                    Dispatcher.BeginInvoke(new Action(ShowDashboard));
                }
                catch
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "Sentinel.ShowListener"
        };
        thread.Start();
    }
}
