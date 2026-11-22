using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using PCMonitor.Agent.Models;
using PCMonitor.Agent.Views;

namespace PCMonitor.Agent.Services;

public sealed record CommandOutcome(bool Ok, string? Error = null, object? Data = null);

public sealed record CaptureBatch(int Count, int Uploaded, List<string> Ids, List<CaptureResult> Items);

public sealed class AgentHost : IDisposable
{
    public ConfigStore Store { get; }
    public AppConfig Config { get; private set; }
    public ActivityLog Log { get; }
    public ApiClient Api { get; }
    public MonitorService Monitor { get; }
    public AgentRuntime Runtime { get; }
    public AdbService Adb { get; }
    public LocalApiServer Local { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    public bool Away { get; private set; }
    public string AwayReason { get; private set; } = "";
    public DateTime? AwaySince { get; private set; }

    private bool _nameDirty;
    private long _nameChangedAtMs;

    public event Action<string, string>? Notice;       // title, body
    public event Action? PairingChanged;
    public event Action? AwayChanged;
    public event Action<string>? ToastRequested;

    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private System.Threading.Timer? _awayShotTimer;
    private MessageWindow? _messageWindow;
    private FullscreenMessageWindow? _fullscreenMessage;
    private SecureLockWindow? _secureLockWindow;
    private DateTime _lastAwayShotAt = DateTime.MinValue;
    private readonly List<LocalEventRecord> _localEvents = new();    private readonly object _eventLock = new();
    private long _eventSeq;
    private string _localPairCode = "";
    private DateTime _localPairCodeExpiry = DateTime.MinValue;

    public AgentHost()
    {
        Store = new ConfigStore();
        Config = Store.Load();
        Log = new ActivityLog(Store.LogsDir);
        Api = new ApiClient
        {
            BaseUrl = Config.ServerUrl,
            Token = Config.AgentToken,
            AllowInsecureTls = Config.AllowInsecureTls
        };
        Monitor = new MonitorService(Config);
        Runtime = new AgentRuntime(this, Api, Config);
        Adb = new AdbService(this);
        Local = new LocalApiServer(this);

        Runtime.StatusChanged += status => UiState.I.Connection = status;
        Api.Trace += line => Log.Write(line.StartsWith("FAILED", StringComparison.OrdinalIgnoreCase) ? "error" : "http", line);
        Api.Unauthorized += () =>
        {
            if (Runtime.Status != "Re-pair") UiState.I.Connection = "Re-pair";
        };
        UiState.I.Connection = "Offline";
        UiState.I.ServerUrl = Config.ServerUrl;
        UiState.I.ServerLabel = string.IsNullOrWhiteSpace(Config.ServerUrl) ? "Not paired" : Config.ServerUrl;
        UiState.I.Paired = Config.Paired;
        UiState.I.DeviceName = Config.DeviceName;
        LoadExistingShots();
    }

    public void Start()
    {
        Monitor.Monitoring = !Config.MonitorOnlyInAway;
        Monitor.Activity += OnActivity;
        Monitor.UserActivity += OnUserActivity;
        Monitor.PowerEvent += OnPowerEvent;
        Monitor.Start();
        StartConnectors();
        var uptime = Environment.TickCount64 / 1000;
        if (uptime < 600)
        {
            OnActivity(new ActivityEventData
            {
                Category = "power",
                Severity = "info",
                Title = "PC booted",
                Detail = new() { ["uptime_seconds"] = uptime, ["version"] = AgentInfo.Version }
            });
        }
        Log.Write("info", "agent started", new { Config.DeviceId, paired = Config.Paired, version = AgentInfo.Version });
    }

    private void StartConnectors()
    {
        Api.BaseUrl = Config.ServerUrl;
        Api.Token = Config.AgentToken;
        Local.Start();
        if (Config.AdbEnabled) Adb.Start();
        if (Config.Paired)
        {
            Runtime.Start();
            _ = PushSettingsAsync();
        }
    }

    public async Task PushSettingsAsync()
    {
        if (!Config.Paired || string.IsNullOrEmpty(Api.Token)) return;
        try
        {
            await Api.PostStateAsync(null, null, null, CancellationToken.None,
                new
                {
                    protect_power = Config.ProtectPowerCommands,
                    power_mode = Config.PowerActionMode,
                    power_message = Config.PowerActionMessage
                });
        }
        catch
        {
            // next heartbeat/state post will retry
        }
    }

    public void Stop()
    {
        StopAwayScreenshotTimer();
        Runtime.Stop();
        Local.Stop();
        Adb.Stop();
        Monitor.Activity -= OnActivity;
        Monitor.UserActivity -= OnUserActivity;
        Monitor.PowerEvent -= OnPowerEvent;
        Monitor.Stop();
    }

    public void SaveConfig()
    {
        Store.Save(Config);
        UiState.I.ServerUrl = Config.ServerUrl;
        UiState.I.ServerLabel = string.IsNullOrWhiteSpace(Config.ServerUrl) ? "Not paired" : Config.ServerUrl;
        UiState.I.DeviceName = Config.DeviceName;
    }

    public void ApplyPairing(string deviceId, string agentToken, string totpSecret)
    {
        if (!string.IsNullOrWhiteSpace(deviceId)) Config.DeviceId = deviceId;
        if (!string.IsNullOrWhiteSpace(agentToken)) Config.ProtectedAgentToken = Secrets.Protect(agentToken);
        if (!string.IsNullOrWhiteSpace(totpSecret)) Config.ProtectedTotpSecret = Secrets.Protect(totpSecret);
        if (string.IsNullOrWhiteSpace(Config.DeviceId)) Config.DeviceId = Guid.NewGuid().ToString("N");
        SaveConfig();
        UiState.I.Paired = true;
        Log.Write("info", "pairing stored", new { Config.DeviceId });
        PairingChanged?.Invoke();
        if (!Runtime.IsRunning) StartConnectors();
    }

    public void Unpair()
    {
        Config.ProtectedAgentToken = "";
        Config.ProtectedTotpSecret = "";
        SaveConfig();
        Runtime.Stop();
        UiState.I.Paired = false;
        UiState.I.Connection = "Offline";
        PairingChanged?.Invoke();
    }

    public void RestartConnectors()
    {
        Runtime.Stop();
        Local.Stop();
        Adb.Stop();
        StartConnectors();
    }

    // ------------------------------------------------------------------ state

    public StateDto CurrentState() => new()
    {
        Monitors = ScreenCapture.Monitors().Count,
        Session = SystemMetrics.IsWorkstationLocked() ? "locked" : "active",
        Screensaver = Away,
        IdleSeconds = SystemMetrics.IdleSeconds(),
        AwayReason = Away ? AwayReason : null,
        AgentUptimeSeconds = (long)(DateTime.UtcNow - StartedAt).TotalSeconds,
        LocalApi = Local.Running,
        PhonePresent = Adb.TrustedPresent,
        SecureLock = PCMonitor.Agent.Services.SecureLock.Active
    };

    public DeviceDto BuildDeviceSnapshot()
    {
        var state = CurrentState();
        return new DeviceDto
        {
            Id = Config.DeviceId,
            Name = Config.DeviceName,
            Platform = "windows",
            Online = true,
            LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Away = Away,
            Locked = state.Session == "locked",
            Settings = new DeviceSettingsDto
            {
                Require2faUnlock = true,
                ProtectPower = Config.ProtectPowerCommands,
                PowerMode = Config.PowerActionMode,
                PowerMessage = Config.PowerActionMessage
            },
            State = state,
            Metrics = SystemMetrics.Collect(),
            Relay = Runtime.Status == "Live",
            AgentVersion = AgentInfo.Version,
            TotpSecret = Config.TotpSecret
        };
    }

    public (long Id, ActivityEventData Data)[] RecentEvents(long since, int limit)
    {
        lock (_eventLock)
        {
            return _localEvents
                .Where(e => e.Id > since)
                .TakeLast(limit)
                .Select(e => (e.Id, e.Data))
                .ToArray();
        }
    }

    private void OnActivity(ActivityEventData evt)
    {
        Runtime.Enqueue(evt);
        UiState.I.AddActivity(evt);
        lock (_eventLock)
        {
            _eventSeq++;
            _localEvents.Add(new LocalEventRecord(_eventSeq, evt));
            if (_localEvents.Count > 3000) _localEvents.RemoveRange(0, _localEvents.Count - 3000);
        }
        if (evt.Severity is "warning" or "notice")
            Log.Write("event", evt.Title, evt.Detail);
    }

    private void OnPowerEvent(string title, string detail)
    {
        var evt = new ActivityEventData
        {
            Category = "power",
            Severity = "warning",
            Title = title,
            Detail = new() { ["detail"] = detail }
        };
        UiState.I.AddActivity(evt);
        lock (_eventLock)
        {
            _eventSeq++;
            _localEvents.Add(new LocalEventRecord(_eventSeq, evt));
            if (_localEvents.Count > 3000) _localEvents.RemoveRange(0, _localEvents.Count - 3000);
        }
        Log.Write("event", title, detail);
        if (!Config.Paired) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await Api.PostEventsAsync(new[] { evt }, timeout.Token);
            }
            catch (Exception ex)
            {
                Log.Write("error", "power event post failed", ex.Message);
            }
        });
    }

    private sealed record LocalEventRecord(long Id, ActivityEventData Data);

    // ------------------------------------------------------------------ away mode

    public async Task<CommandOutcome> EnableAwayAsync(string reason)
    {
        if (Away) return new CommandOutcome(true, null, new { away = true, already = true });
        Log.Write("info", "away.enable.begin", new { reason });
        Away = true;
        AwayReason = reason;
        AwaySince = DateTime.UtcNow;
        UiState.I.Away = true;
        Monitor.Monitoring = true;

        string screenDetail = "disabled";
        if (Config.AwayScreensaver)
        {
            screenDetail = PowerActions.TriggerScreensaver(out var detail) ? detail : $"screensaver unavailable: {detail}";
        }
        Log.Write("info", "away.enable.screensaver", new { screenDetail });
        StartAwayScreenshotTimer();
        OnActivity(new ActivityEventData
        {
            Category = "security",
            Severity = "warning",
            Title = "Away mode enabled",
            Detail = new() { ["reason"] = reason, ["screensaver"] = screenDetail }
        });
        Log.Write("info", "away.enable.event", new { });
        try
        {
            AwayChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write("error", "away.change handler", ex.Message);
        }
        await PostStateSafe();
        Log.Write("info", "away.enable.state-posted", new { });
        try
        {
            Notify("Away mode active", "Activity logging started, screensaver engaged.");
        }
        catch (Exception ex)
        {
            Log.Write("error", "away.notify", ex.Message);
        }
        Log.Write("info", "away.enable.done", new { });
        return new CommandOutcome(true, null, new { away = true, screensaver = screenDetail });
    }

    public async Task<CommandOutcome> DisableAwayAsync(string reason, string? code)
    {
        if (!Away) return new CommandOutcome(true, null, new { away = false, already = true });
        if (!Totp.Verify(Config.TotpSecret, code)) return new CommandOutcome(false, "bad_code");
        if (Config.RequirePhonePresence && !Adb.TrustedPresent)
            return new CommandOutcome(false, "phone_not_present");

        Away = false;
        AwayReason = "";
        AwaySince = null;
        UiState.I.Away = false;
        StopAwayScreenshotTimer();
        Monitor.Monitoring = !Config.MonitorOnlyInAway;
        PowerActions.ScreenOn();
        OnActivity(new ActivityEventData
        {
            Category = "security",
            Severity = "warning",
            Title = "Away mode disabled",
            Detail = new() { ["reason"] = reason }
        });
        AwayChanged?.Invoke();
        await PostStateSafe();
        Notify("Away mode disabled", "Monitoring stopped, display restored.");
        return new CommandOutcome(true, null, new { away = false });
    }

    private async Task PostStateSafe()
    {
        if (!Config.Paired) return;
        try
        {
            await Api.PostStateAsync(Away, SystemMetrics.IsWorkstationLocked(), CurrentState(), CancellationToken.None);
        }
        catch
        {
            // offline is fine, heartbeat will sync
        }
    }

    private void StartAwayScreenshotTimer()
    {
        StopAwayScreenshotTimer();
        if (!Config.AutoScreenshotInAway) return;
        ScheduleAwayShot();
    }

    private void ScheduleAwayShot(int? minSeconds = null)
    {
        if (!Away || !Config.AutoScreenshotInAway) return;
        _awayShotTimer?.Dispose();
        var min = Math.Max(10, minSeconds ?? Math.Min(Config.AwayShotMinSeconds, Config.AwayShotMaxSeconds));
        var max = Math.Max(min + 5, Math.Max(Config.AwayShotMinSeconds, Config.AwayShotMaxSeconds));
        var delay = minSeconds.HasValue ? min : Random.Shared.Next(min, max + 1);
        _awayShotTimer = new System.Threading.Timer(_ =>
        {
            if (!Away) return;
            _ = CaptureAndUploadAsync("away", 1600);
            ScheduleAwayShot();
        }, null, TimeSpan.FromSeconds(delay), Timeout.InfiniteTimeSpan);
    }

    private void OnUserActivity()
    {
        if (!Away || !Config.AwayShotOnActivity) return;
        if (DateTime.UtcNow - _lastAwayShotAt < TimeSpan.FromSeconds(15)) return;
        ScheduleAwayShot(3);
    }

    private void StopAwayScreenshotTimer()
    {
        _awayShotTimer?.Dispose();
        _awayShotTimer = null;
    }

    // ------------------------------------------------------------------ commands

    public async Task<CommandOutcome> ExecuteCommandAsync(CommandEnvelope command, string source)
    {
        var args = command.Args ?? new Dictionary<string, JsonElement>();
        string Arg(string key) => args.TryGetValue(key, out var el)
            ? (el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString())
            : "";
        string? code = args.ContainsKey("code") ? Arg("code") : null;

        Log.Write("info", $"execute {command.Type}", new { source, code = code != null });
        try
        {
            switch (command.Type)
            {
                case "lock":
                    PowerActions.Lock();
                    OnActivity(new ActivityEventData { Category = "security", Severity = "warning", Title = "PC locked remotely", Detail = new() { ["source"] = source } });
                    return new CommandOutcome(true);

                case "unlock":
                case "away_disable":
                {
                    if (!Totp.Verify(Config.TotpSecret, code)) return new CommandOutcome(false, "bad_code");
                    if (Config.RequirePhonePresence && !Adb.TrustedPresent)
                        return new CommandOutcome(false, "phone_not_present");
                    var result = await DisableAwayAsync(source, code);
                    if (result.Ok)
                    {
                        PowerActions.ScreenOn();
                        OnActivity(new ActivityEventData { Category = "security", Severity = "warning", Title = "PC unlocked remotely", Detail = new() { ["source"] = source } });
                    }
                    return result;
                }

                case "away_enable":
                    return await EnableAwayAsync(source);

                case "screenshot":
                {
                    var outcome = await CaptureAndUploadAsync(string.IsNullOrWhiteSpace(Arg("trigger")) ? "remote" : Arg("trigger"), 2560);
                    return outcome;
                }

                case "shutdown":
                    return await HandlePowerAsync("shutdown", "shut down", source);

                case "restart":
                    return await HandlePowerAsync("restart", "restart", source);

                case "cancel_shutdown":
                    PowerActions.CancelShutdown();
                    return new CommandOutcome(true);

                case "logoff":
                    PowerActions.Logoff();
                    return new CommandOutcome(true);

                case "sleep":
                    PowerActions.Sleep();
                    return new CommandOutcome(true);

                case "hibernate":
                    PowerActions.Hibernate();
                    return new CommandOutcome(true);

                case "screen_off":
                    PowerActions.ScreenOff();
                    return new CommandOutcome(true);

                case "screen_on":
                    PowerActions.ScreenOn();
                    return new CommandOutcome(true);

                case "clipboard":
                {
                    var text = Arg("text");
                    if (string.IsNullOrEmpty(text)) return new CommandOutcome(false, "missing_text");
                    if (text.Length > 20_000) text = text[..20_000];
                    if (!SetClipboard(text)) return new CommandOutcome(false, "clipboard_failed");
                    OnActivity(new ActivityEventData
                    {
                        Category = "clipboard",
                        Severity = "info",
                        Title = "Clipboard set from your phone",
                        Detail = new() { ["chars"] = text.Length, ["source"] = source }
                    });
                    return new CommandOutcome(true, null, new { chars = text.Length });
                }

                case "media":
                    PowerActions.MediaPlayPause();
                    return new CommandOutcome(true);

                case "message":
                {
                    var text = Arg("text");
                    if (string.IsNullOrWhiteSpace(text)) text = "Message from your phone";
                    var title = Arg("title");
                    if (string.IsNullOrWhiteSpace(title)) title = "Message from your phone";
                    var kind = Arg("kind");
                    if (string.IsNullOrWhiteSpace(kind)) kind = "toast";
                    var messageId = Arg("message_id");
                    var allowReply = args.TryGetValue("allow_reply", out var replyFlag) &&
                                     (replyFlag.ValueKind == JsonValueKind.True ||
                                      (replyFlag.ValueKind == JsonValueKind.String &&
                                       bool.TryParse(replyFlag.GetString(), out var parsed) && parsed));
                    ShowMessage(title, text, kind, allowReply, string.IsNullOrWhiteSpace(messageId) ? null : messageId);
                    OnActivity(new ActivityEventData
                    {
                        Category = "message",
                        Severity = "notice",
                        Title = $"Message shown: {text[..Math.Min(90, text.Length)]}",
                        Detail = new() { ["kind"] = kind, ["reply"] = allowReply }
                    });
                    return new CommandOutcome(true);
                }

                case "alarm":
                {
                    _ = Task.Run(async () =>
                    {
                        for (var i = 0; i < 12; i++)
                        {
                            System.Media.SystemSounds.Hand.Play();
                            await Task.Delay(600);
                        }
                    });
                    return new CommandOutcome(true);
                }

                case "lock_screen":
                {
                    var message = Arg("message");
                    if (string.IsNullOrWhiteSpace(message)) message = "This PC was locked from your phone.";
                    EnterSecureLock(message);
                    return new CommandOutcome(true, null, new { locked = true });
                }

                case "unlock_screen":
                {
                    if (!Totp.Verify(Config.TotpSecret, code)) return new CommandOutcome(false, "bad_code");
                    ExitSecureLock(source);
                    return new CommandOutcome(true, null, new { locked = false });
                }

                case "agent_exit":
                {
                    if (!Totp.Verify(Config.TotpSecret, code))
                        return new CommandOutcome(false, "bad_code");
                    var app = Application.Current;
                    if (app == null) return new CommandOutcome(false, "no_ui");
                    OnActivity(new ActivityEventData
                    {
                        Category = "security",
                        Severity = "warning",
                        Title = "Agent exit authorized remotely",
                        Detail = new() { ["source"] = source }
                    });
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(900);
                        ExitGuard.Authorize();
                        app.Dispatcher.Invoke(() => App.Current.ExitApp(true));
                    });
                    return new CommandOutcome(true, null, new { exiting = true });
                }

                case "info":
                    return new CommandOutcome(true, null, new
                    {
                        away = Away,
                        state = CurrentState(),
                        metrics = SystemMetrics.Collect(),
                        device_id = Config.DeviceId,
                        device_name = Config.DeviceName,
                        local_api = Local.Running,
                        adb = new { enabled = Config.AdbEnabled, devices = Adb.Devices.Count, trusted = Adb.TrustedPresent }
                    });

                default:
                    return new CommandOutcome(false, "unsupported_command");
            }
        }
        catch (Exception ex)
        {
            Log.Write("error", $"command {command.Type} failed", ex.Message);
            return new CommandOutcome(false, ex.Message);
        }
    }

    public Task<CommandOutcome> RunLocalAsync(string type, Dictionary<string, JsonElement>? args = null) =>
        ExecuteCommandAsync(new CommandEnvelope { Id = Guid.NewGuid().ToString("N"), Type = type, Args = args }, "desktop");

    private static bool SetClipboard(string text)
    {
        ClipboardPrivacy.Suppress(text);
        try
        {
            var app = Application.Current;
            if (app != null)
            {
                app.Dispatcher.Invoke(() => Clipboard.SetText(text));
                return true;
            }
        }
        catch
        {
            // fall through to a private STA thread
        }

        var done = false;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                Clipboard.SetText(text);
                done = true;
            }
            catch
            {
                // ignored
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(2000);
        return done;
    }

    public void PublishName()
    {
        _nameDirty = true;
        _nameChangedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public string? TakeNameForPublish()
    {
        if (!_nameDirty) return null;
        _nameDirty = false;
        return Config.DeviceName;
    }

    public bool NameRecentlyChanged =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _nameChangedAtMs < 30_000;

    // ------------------------------------------------------------------ power + messages

    private async Task<CommandOutcome> HandlePowerAsync(string type, string verb, string source)
    {
        var mode = (Config.PowerActionMode ?? "confirm").ToLowerInvariant();
        var template = string.IsNullOrWhiteSpace(Config.PowerActionMessage)
            ? "Your phone asked to {action} this PC."
            : Config.PowerActionMessage;
        var message = template.Replace("{action}", verb);

        if (mode == "silent")
        {
            ExecutePower(type);
            OnActivity(new ActivityEventData
            {
                Category = "security",
                Severity = "warning",
                Title = $"{(type == "shutdown" ? "Shutdown" : "Restart")} scheduled",
                Detail = new() { ["source"] = source, ["mode"] = mode }
            });
            return new CommandOutcome(true, null, new { mode, executed = true });
        }

        if (mode == "notify")
        {
            ShowMessage(
                $"PC will {verb}",
                $"{message}\n\nThis PC will {verb} in 10 seconds.",
                "alert",
                false,
                null);
            ExecutePower(type);
            OnActivity(new ActivityEventData
            {
                Category = "security",
                Severity = "warning",
                Title = $"{(type == "shutdown" ? "Shutdown" : "Restart")} scheduled (notified)",
                Detail = new() { ["source"] = source, ["mode"] = mode }
            });
            return new CommandOutcome(true, null, new { mode, executed = true });
        }

        var accepted = await ConfirmPowerAsync(
            $"{(type == "shutdown" ? "Shut down" : "Restart")} this PC?",
            message);
        if (!accepted)
        {
            OnActivity(new ActivityEventData
            {
                Category = "security",
                Severity = "warning",
                Title = $"{(type == "shutdown" ? "Shutdown" : "Restart")} declined at the PC",
                Detail = new() { ["source"] = source, ["mode"] = mode }
            });
            return new CommandOutcome(false, "declined_by_user");
        }

        ExecutePower(type);
        OnActivity(new ActivityEventData
        {
            Category = "security",
            Severity = "warning",
            Title = $"{(type == "shutdown" ? "Shutdown" : "Restart")} allowed at the PC",
            Detail = new() { ["source"] = source, ["mode"] = mode }
        });
        return new CommandOutcome(true, null, new { mode, executed = true });
    }

    private static void ExecutePower(string type)
    {
        if (type == "shutdown") PowerActions.Shutdown(10);
        else PowerActions.Restart(10);
    }

    private Task<bool> ConfirmPowerAsync(string title, string message)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = Application.Current;
        if (app == null)
        {
            completion.TrySetResult(true);
            return completion.Task;
        }
        app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var window = new PowerConfirmWindow(title, message);
                window.ShowDialog();
                completion.TrySetResult(window.Accepted);
            }
            catch (Exception ex)
            {
                Log.Write("error", "power confirm failed", ex.Message);
                completion.TrySetResult(false);
            }
        });
        return completion.Task;
    }

    public void ShowMessage(string title, string body, string kind, bool allowReply, string? messageId)
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _messageWindow?.Close();
                _fullscreenMessage?.Close();
            }
            catch
            {
                // ignored
            }

            if (kind == "fullscreen")
            {
                var fullscreen = new FullscreenMessageWindow(title, body);
                fullscreen.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_fullscreenMessage, fullscreen)) _fullscreenMessage = null;
                };
                _fullscreenMessage = fullscreen;
                fullscreen.Show();
                fullscreen.Activate();
                return;
            }

            var seconds = kind is "dialog" or "alert" ? 0 : Math.Max(4, Config.MessagePopupSeconds);
            var window = new MessageWindow(title, body, kind, allowReply, messageId, seconds);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_messageWindow, window)) _messageWindow = null;
            };
            _messageWindow = window;
            window.Show();
            window.Activate();
        });
    }

    public void EnterSecureLock(string message)
    {
        SecureLock.Activate(message);
        OnActivity(new ActivityEventData
        {
            Category = "security",
            Severity = "warning",
            Title = "Secure lock engaged",
            Detail = new() { ["message"] = message }
        });
        ShowSecureLockWindow(message);
        _ = PostStateSafe();
    }

    public void ExitSecureLock(string reason)
    {
        if (!SecureLock.Active) return;
        SecureLock.Deactivate();
        var app = Application.Current;
        app?.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _secureLockWindow?.CloseAuthorized();
            }
            catch
            {
                // ignored
            }
            _secureLockWindow = null;
        });
        OnActivity(new ActivityEventData
        {
            Category = "security",
            Severity = "warning",
            Title = "Secure lock released",
            Detail = new() { ["reason"] = reason }
        });
        _ = PostStateSafe();
    }

    public void RestoreSecureLockIfNeeded()
    {
        if (SecureLock.Load()) ShowSecureLockWindow(SecureLock.Message);
    }

    private void ShowSecureLockWindow(string message)
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            if (_secureLockWindow != null) return;
            var window = new SecureLockWindow(message);
            window.Closed += (_, _) => _secureLockWindow = null;
            _secureLockWindow = window;
            window.Show();
            window.Activate();
        });
    }

    public async Task SendMessageReplyAsync(string messageId, string? reply, string status)
    {
        if (string.IsNullOrWhiteSpace(messageId) || !Config.Paired) return;
        try
        {
            await Api.PostMessageReplyAsync(messageId, reply, status, CancellationToken.None);
            Log.Write("info", "message reply sent", new { messageId, status });
        }
        catch (Exception ex)
        {
            Log.Write("error", "message reply failed", ex.ToString());
        }
    }

    // ------------------------------------------------------------------ screenshots

    public async Task<CommandOutcome> CaptureAndUploadAsync(string trigger, int maxWidth = 2560)
    {
        if (!await _captureGate.WaitAsync(TimeSpan.Zero))
            return new CommandOutcome(false, "capture_busy");
        try
        {
            List<CaptureResult> captures;
            try
            {
                captures = await Task.Run(() => ScreenCapture.CaptureAll(Store.ShotsDir, trigger, maxWidth));
            }
            catch (Exception ex)
            {
                return new CommandOutcome(false, ex.Message);
            }

            if (captures.Count == 0) return new CommandOutcome(false, "capture_failed");

            foreach (var capture in captures) UiState.I.AddShot(capture);
            if (trigger == "away") _lastAwayShotAt = DateTime.UtcNow;

            var uploaded = new List<string>();
            if (Config.Paired && !string.IsNullOrEmpty(Api.Token))
            {
                foreach (var capture in captures)
                {
                    try
                    {
                        var response = await Api.UploadScreenshotAsync(capture, CancellationToken.None);
                        if (response.Screenshot != null) uploaded.Add(response.Screenshot.Id);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("error", "screenshot upload failed", ex.Message);
                    }
                }
            }

            OnActivity(new ActivityEventData
            {
                Category = "screenshot",
                Severity = "info",
                Title = $"Screenshot captured · {captures.Count} display(s)",
                Detail = new() { ["trigger"] = trigger, ["uploaded"] = uploaded.Count }
            });

            return new CommandOutcome(true, null, new CaptureBatch(captures.Count, uploaded.Count, uploaded, captures));
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public IReadOnlyList<string> FindScreenshotPath(string id)
    {
        var match = UiState.I.Shots.FirstOrDefault(s =>
            string.Equals(Path.GetFileNameWithoutExtension(s.Path), id, StringComparison.OrdinalIgnoreCase));
        return match == null ? Array.Empty<string>() : new[] { match.Path };
    }

    // ------------------------------------------------------------------ local pairing

    public string EnsureLocalPairCode()
    {
        if (DateTime.UtcNow < _localPairCodeExpiry && _localPairCode.Length == 6) return _localPairCode;
        _localPairCode = TokenGenerator.PairCode();
        _localPairCodeExpiry = DateTime.UtcNow.AddMinutes(10);
        return _localPairCode;
    }

    public bool ValidateLocalPairCode(string code)
    {
        if (DateTime.UtcNow > _localPairCodeExpiry) return false;
        return !string.IsNullOrEmpty(code) &&
               string.Equals(code.Trim().ToUpperInvariant(), _localPairCode, StringComparison.Ordinal);
    }

    public void InvalidateLocalPairCode()
    {
        _localPairCode = "";
        _localPairCodeExpiry = DateTime.MinValue;
    }

    public bool VerifyCode(string? code) => Totp.Verify(Config.TotpSecret, code);

    public bool CanDisarm(out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(Config.TotpSecret))
        {
            reason = "No TOTP secret stored. Re-pair the device.";
            return false;
        }
        if (Config.RequirePhonePresence && !Adb.TrustedPresent)
        {
            reason = "Phone presence required but no trusted ADB device is connected.";
            return false;
        }
        return true;
    }

    public void Notify(string title, string body)
    {
        Notice?.Invoke(title, body);
        Log.Write("notice", title, body);
    }

    private void LoadExistingShots()
    {
        try
        {
            var files = new DirectoryInfo(Store.ShotsDir).GetFiles("*.png")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(120);
            foreach (var file in files)
            {
                var item = new ShotItem
                {
                    Path = file.FullName,
                    CapturedAt = file.LastWriteTime,
                    Monitor = 0,
                    MonitorName = "Local capture",
                    Width = 0,
                    Height = 0,
                    Trigger = "local"
                };
                UiState.I.Shots.Add(item);
                UiState.I.TrackShot(item);
                var thumb = ScreenCapture.LoadThumbnail(file.FullName, 420);
                if (thumb != null) item.Thumb = thumb;
            }
            UiState.I.ShotCount = UiState.I.Shots.Count;
        }
        catch
        {
            // ignored
        }
    }

    public void Dispose()
    {
        Stop();
        Runtime.Dispose();
        Local.Dispose();
        Adb.Dispose();
        Monitor.Dispose();
        Api.Dispose();
        _captureGate.Dispose();
    }
}
