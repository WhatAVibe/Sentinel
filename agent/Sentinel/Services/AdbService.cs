using System.Diagnostics;
using System.IO;

namespace Sentinel.Services;

public sealed record AdbDevice(string Serial, string State, string Model, string Product, string TransportId)
{
    public string Label => string.IsNullOrWhiteSpace(Model) ? Serial : $"{Model.Replace('_', ' ')} · {Serial}";
    public bool Usable => State == "device";
}

public sealed class AdbService : IDisposable
{
    private readonly AgentHost _host;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _cts;

    public AdbService(AgentHost host) => _host = host;

    public List<AdbDevice> Devices { get; } = new();
    public bool TrustedPresent { get; private set; }
    public bool LastKnownTrustedPresent { get; private set; }
    public string? AdbPath { get; private set; }
    public bool Running { get; private set; }
    public string LastError { get; private set; } = "";
    public bool ReverseReady { get; private set; }

    public event Action? Changed;

    public void Start()
    {
        if (Running) return;
        Running = true;
        _cts = new CancellationTokenSource();
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, 500, 15000);
    }

    public void Stop()
    {
        Running = false;
        _timer?.Dispose();
        _timer = null;
        _cts?.Cancel();
        _cts = null;
        Devices.Clear();
        TrustedPresent = false;
        ReverseReady = false;
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
    }

    public string? ResolveAdb()
    {
        var configured = _host.Config.AdbPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim(), "adb.exe")); }
            catch { }
        }

        var sdk = Environment.GetEnvironmentVariable("ANDROID_HOME") ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (!string.IsNullOrWhiteSpace(sdk)) candidates.Add(Path.Combine(sdk, "platform-tools", "adb.exe"));

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe"));
        candidates.Add(@"C:\Android\platform-tools\adb.exe");
        candidates.Add(@"C:\Program Files\platform-tools\adb.exe");
        candidates.Add(@"C:\flutter\bin\adb.exe");

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // invalid path
            }
        }
        return null;
    }

    public async Task<string> RefreshAsync()
    {
        if (!Running) return "ADB disabled";
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false)) return "ADB busy";
        try
        {
            AdbPath = ResolveAdb();
            if (AdbPath == null)
            {
                LastError = "adb.exe not found. Set the path in Settings.";
                Devices.Clear();
                TrustedPresent = false;
                Changed?.Invoke();
                return LastError;
            }

            var result = await RunAsync("devices -l", 15000).ConfigureAwait(false);
            var parsed = ParseDevices(result.stdout);
            var previous = LastKnownTrustedPresent;

            Devices.Clear();
            Devices.AddRange(parsed);
            var trusted = _host.Config.TrustedAdbSerials;
            TrustedPresent = Devices.Any(d => d.Usable && (trusted.Count == 0 || trusted.Contains(d.Serial)));
            LastError = result.exitCode == 0 ? "" : (result.stderr.Trim().Length > 0 ? result.stderr.Trim() : "adb failed");

            if (TrustedPresent)
            {
                if (!_host.Away && previous == false && _host.Config.AutoAwayOnPhoneDisconnect && _host.Config.AdbEnabled)
                {
                    // presence regained: no automatic action needed
                }
                await SetupReverseAsync().ConfigureAwait(false);
            }
            else
            {
                ReverseReady = false;
                if (previous && !_host.Away && _host.Config.AutoAwayOnPhoneDisconnect)
                    _ = _host.EnableAwayAsync("auto:phone-left");
            }

            LastKnownTrustedPresent = TrustedPresent;
            Changed?.Invoke();
            return LastError;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SetupReverseAsync()
    {
        if (!TrustedPresent) return "no trusted device";
        if (AdbPath == null) return "adb missing";
        var port = _host.Config.LocalPort;
        var errors = new List<string>();
        var ok = false;
        foreach (var device in Devices.Where(d => d.Usable))
        {
            var result = await RunAsync($"-s {device.Serial} reverse tcp:{port} tcp:{port}", 12000).ConfigureAwait(false);
            if (result.exitCode == 0 && !result.stdout.Contains("error", StringComparison.OrdinalIgnoreCase))
                ok = true;
            else if (!string.IsNullOrWhiteSpace(result.stderr))
                errors.Add(result.stderr.Trim());
        }
        ReverseReady = ok;
        LastError = ok ? "" : string.Join("; ", errors);
        return LastError;
    }

    public async Task<string> PairAsync(string endpoint, string code)
    {
        if (AdbPath == null) AdbPath = ResolveAdb();
        if (AdbPath == null) return "adb.exe not found";
        var result = await RunAsync($"pair {endpoint.Trim()} {code.Trim()}", 40000).ConfigureAwait(false);
        if (result.exitCode != 0 || result.stdout.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(result.stdout + result.stderr) ? "pairing failed" : (result.stdout + result.stderr).Trim();
        await RefreshAsync().ConfigureAwait(false);
        return "paired";
    }

    public async Task<string> ConnectAsync(string endpoint)
    {
        if (AdbPath == null) AdbPath = ResolveAdb();
        if (AdbPath == null) return "adb.exe not found";
        var target = endpoint.Trim();
        if (!target.Contains(':')) target += ":5555";
        var result = await RunAsync($"connect {target}", 20000).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
        return (result.stdout + result.stderr).Trim();
    }

    public async Task DisconnectAsync(string serial)
    {
        if (AdbPath == null) return;
        await RunAsync($"disconnect {serial}", 10000).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task<bool> IsDeviceLockedAsync(string serial)
    {
        if (AdbPath == null) return false;
        var result = await RunAsync($"-s {serial} shell dumpsys window | grep -E \"mDreamingLockscreen|mShowingLockscreen\"", 12000)
            .ConfigureAwait(false);
        return result.stdout.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunAsync(string arguments, int timeoutMs)
    {
        if (AdbPath == null) return (-1, "", "adb missing");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AdbPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = new Process { StartInfo = psi };
            var stdout = new System.Text.StringBuilder();
            var stderr = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return (-1, stdout.ToString(), "timeout");
            }
            return (process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    private static List<AdbDevice> ParseDevices(string output)
    {
        var list = new List<AdbDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices") || line.StartsWith("*")) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var serial = parts[0];
            var state = parts[1];
            var model = "";
            var product = "";
            var transport = "";
            foreach (var part in parts.Skip(2))
            {
                if (part.StartsWith("model:")) model = part[6..];
                else if (part.StartsWith("product:")) product = part[8..];
                else if (part.StartsWith("transport_id:")) transport = part[13..];
            }
            list.Add(new AdbDevice(serial, state, model, product, transport));
        }
        return list;
    }
}
