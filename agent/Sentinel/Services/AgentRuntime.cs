using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Sentinel.Models;

namespace Sentinel.Services;

public sealed class AgentRuntime : IDisposable
{
    private readonly AgentHost _host;
    private readonly ApiClient _api;
    private readonly AppConfig _cfg;
    private readonly object _pendingLock = new();
    private readonly List<ActivityEventData> _pending = new();
    private readonly SemaphoreSlim _flushSignal = new(0, 1);
    private readonly List<Task> _loops = new();

    private CancellationTokenSource? _cts;
    private int _started;

    // A command queued before this process started (or while the PC was
    // asleep) must never run after a reboot. The relay expires pending
    // commands after command_ttl_seconds; this is the agent-side guard.
    private const long StaleCommandMs = 180_000;
    private const long StartupGraceMs = 60_000;
    private readonly long _startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public AgentRuntime(AgentHost host, ApiClient api, AppConfig cfg)
    {
        _host = host;
        _api = api;
        _cfg = cfg;
    }

    public string Status { get; private set; } = "Offline";
    public long LastContact { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRunning => _started == 1;

    public event Action<string>? StatusChanged;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetStatus("Connecting");
        _loops.Add(Task.Run(() => HeartbeatLoopAsync(ct), ct));
        _loops.Add(Task.Run(() => CommandLoopAsync(ct), ct));
        _loops.Add(Task.Run(() => EventLoopAsync(ct), ct));
        _loops.Add(Task.Run(() => AutomationLoopAsync(ct), ct));
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0) return;
        try
        {
            _cts?.Cancel();
            try
            {
                Task.WaitAll(_loops.ToArray(), TimeSpan.FromSeconds(3));
            }
            catch
            {
                // ignored
            }
            _loops.Clear();
            _cts?.Dispose();
            _cts = null;
        }
        finally
        {
            SetStatus("Offline");
        }
    }

    public void Enqueue(ActivityEventData evt)
    {
        lock (_pendingLock)
        {
            _pending.Add(evt);
            if (_pending.Count > 4000) _pending.RemoveRange(0, _pending.Count - 4000);
        }
        if (_flushSignal.CurrentCount == 0)
        {
            try { _flushSignal.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    public void Dispose()
    {
        Stop();
        _flushSignal.Dispose();
    }

    // Events are encrypted only for the wire; the local dashboard keeps the
    // plaintext copy in ActivityEventData/Title/Detail.
    public ActivityEventData ProtectEvent(ActivityEventData evt)
    {
        if (!string.IsNullOrEmpty(evt.Enc)) return evt;
        var hadKey = !string.IsNullOrEmpty(_cfg.ProtectedDataKey);
        var payload = JsonSerializer.Serialize(new { title = evt.Title, detail = evt.Detail }, ApiClient.Json);
        var enc = DataKey.Encrypt(_cfg, "event", Encoding.UTF8.GetBytes(payload));
        if (!hadKey && !string.IsNullOrEmpty(_cfg.ProtectedDataKey)) _host.SaveConfig();
        return new ActivityEventData
        {
            Ts = evt.Ts,
            Category = evt.Category,
            Severity = evt.Severity,
            Title = evt.Title,
            Detail = evt.Detail,
            Enc = enc
        };
    }

    private void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var locked = SystemMetrics.IsWorkstationLocked();
                var hb = await _api.HeartbeatAsync(_host.Away, locked, SystemMetrics.Collect(), _host.CurrentState(), _host.TakeNameForPublish(), ct)
                    .ConfigureAwait(false);
                LastContact = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                LastError = null;
                if (hb?.Device != null)
                {
                    UiState.I.UpdateFromDevice(hb.Device);
                    _host.SetLinkedDevices(hb.Device.LinkedDevices);
                    var remoteName = hb.Device.Name;
                    if (!string.IsNullOrWhiteSpace(remoteName) &&
                        remoteName != _cfg.DeviceName &&
                        !_host.NameRecentlyChanged)
                    {
                        _cfg.DeviceName = remoteName;
                        _host.SaveConfig();
                        _host.Log.Write("info", "device name synced from relay", new { name = remoteName });
                    }
                    if (hb.Device.Settings != null &&
                        hb.Device.Settings.ProtectPower != _cfg.ProtectPowerCommands)
                    {
                        _cfg.ProtectPowerCommands = hb.Device.Settings.ProtectPower;
                        _host.SaveConfig();
                    }
                }
                if (Status != "Live") _host.Log.Write("info", "connection live", new { server = _api.BaseUrl });
                SetStatus("Live");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ApiException ex) when (ex.Status == 401)
            {
                LastError = "Device token rejected — re-pair required";
                SetStatus("Re-pair");
                _host.Notify("Re-pair required", "The server rejected this device token.");
                await SafeDelay(TimeSpan.FromSeconds(30), ct);
                continue;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SetStatus("Offline");
                _host.Log.Write("error", "heartbeat failed", ex.ToString());
            }
            await SafeDelay(TimeSpan.FromSeconds(Math.Max(5, _cfg.HeartbeatSeconds)), ct);
        }
    }

    private async Task CommandLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var commands = await _api.GetCommandsAsync(25, ct).ConfigureAwait(false);
                foreach (var command in commands)
                {
                    if (ct.IsCancellationRequested) return;

                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (command.CreatedAt > 0)
                    {
                        var ageMs = nowMs - command.CreatedAt;
                        var preStartMs = _startedAtMs - command.CreatedAt;
                        if (ageMs > StaleCommandMs || preStartMs > StartupGraceMs)
                        {
                            _host.Log.Write("warning", "discarded stale command",
                                new { command.Id, command.Type, age_seconds = ageMs / 1000 });
                            await _api.PostCommandResultAsync(command.Id, false, "expired", null, ct)
                                .ConfigureAwait(false);
                            continue;
                        }
                    }

                    var outcome = await _host.ExecuteCommandAsync(command, command.Source ?? "remote").ConfigureAwait(false);
                    await _api.PostCommandResultAsync(command.Id, outcome.Ok, outcome.Error, outcome.Data, ct)
                        .ConfigureAwait(false);
                    _host.Log.Write("info", $"command {command.Type}", new { command.Id, outcome.Ok, outcome.Error });
                }
                if (commands.Count == 0) await SafeDelay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ApiException ex) when (ex.Status == 401)
            {
                SetStatus("Re-pair");
                await SafeDelay(TimeSpan.FromSeconds(20), ct);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SetStatus("Offline");
                _host.Log.Write("error", "command loop failed", ex.ToString());
                await SafeDelay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task EventLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(2, _cfg.EventFlushSeconds));
        while (!ct.IsCancellationRequested)
        {
            List<ActivityEventData> batch;
            lock (_pendingLock)
            {
                batch = _pending.Take(100).ToList();
            }

            if (batch.Count == 0)
            {
                try
                {
                    await _flushSignal.WaitAsync(interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            try
            {
                var encrypted = batch.Select(ProtectEvent).ToList();
                await _api.PostEventsAsync(encrypted, ct).ConfigureAwait(false);
                lock (_pendingLock)
                {
                    _pending.RemoveRange(0, Math.Min(batch.Count, _pending.Count));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await SafeDelay(interval, ct);
            }
        }
    }

    private async Task AutomationLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_host.Away)
                {
                    if (_cfg.LockAfterAwaySeconds > 0 &&
                        _host.AwaySince.HasValue &&
                        DateTime.UtcNow - _host.AwaySince.Value > TimeSpan.FromSeconds(_cfg.LockAfterAwaySeconds) &&
                        !SystemMetrics.IsWorkstationLocked())
                    {
                        PowerActions.Lock();
                    }
                }
                else
                {
                    if (_cfg.AutoAwayOnIdle &&
                        SystemMetrics.IdleSeconds() >= _cfg.AutoAwayIdleMinutes * 60)
                    {
                        await _host.EnableAwayAsync("auto:idle").ConfigureAwait(false);
                    }
                    else if (_cfg.AutoAwayOnPhoneDisconnect &&
                             _cfg.AdbEnabled &&
                             _host.Adb.LastKnownTrustedPresent)
                    {
                        // transition handled in AdbService callback
                    }
                }
            }
            catch
            {
                // ignored
            }
            await SafeDelay(TimeSpan.FromSeconds(15), ct);
        }
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }
}
