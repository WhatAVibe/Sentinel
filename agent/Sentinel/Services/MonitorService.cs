using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Sentinel.Models;
using static Sentinel.Services.NativeMethods;

namespace Sentinel.Services;

public sealed class MonitorService : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly object _lock = new();
    private readonly HashSet<int> _knownPids = new();
    private readonly List<ManagementEventWatcher> _watchers = new();
    private readonly Dictionary<string, long> _connections = new();
    private readonly Dictionary<int, long> _keyStamps = new();

    private IntPtr _hwnd;
    private WndProc? _wndProc;
    private IntPtr _previousProc;
    private IntPtr _hook;
    private WinEventProc? _winEventProc;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private HookProc? _mouseProc;
    private HookProc? _keyboardProc;
    private System.Threading.Timer? _processTimer;
    private System.Threading.Timer? _idleTimer;
    private System.Threading.Timer? _mouseTimer;
    private System.Threading.Timer? _networkTimer;

    private string _lastClipboardHash = "";
    private bool _idleReported;
    private string _lastWindow = "";
    private long _lastWindowAt;
    private string _previousApp = "";
    private DateTime _lastWindowChange = DateTime.UtcNow;
    private POINT _lastMousePoint;
    private bool _started;

    public event Action<ActivityEventData>? Activity;
    public event Action? UserActivity;
    public event Action<string, string>? PowerEvent;

    public MonitorService(AppConfig cfg) => _cfg = cfg;

    public bool Monitoring { get; set; }

    public void Start()
    {
        if (_started) return;
        _started = true;

        _wndProc = WindowProc;
        _hwnd = CreateWindowEx(0, "STATIC", "SentinelSink", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd != IntPtr.Zero)
        {
            _previousProc = SetWindowLongPtr(_hwnd, GWL_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));
            WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION);
            AddClipboardFormatListener(_hwnd);
        }

        _winEventProc = OnForeground;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        SnapshotProcesses();
        _processTimer = new System.Threading.Timer(_ => PollProcesses(), null, 4000, 4000);
        _idleTimer = new System.Threading.Timer(_ => PollIdle(), null, 5000, 5000);

        if (_cfg.CaptureMouse)
        {
            GetCursorPos(out _lastMousePoint);
            _mouseProc = MouseHook;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
            _mouseTimer = new System.Threading.Timer(_ => PollMouse(), null, 8000, 8000);
        }
        _keyboardProc = KeyboardHook;
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(null), 0);
        if (_cfg.CaptureNetwork)
        {
            _networkTimer = new System.Threading.Timer(_ => PollNetwork(), null, 8000, 20000);
        }

        StartUsbWatchers();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        _processTimer?.Dispose();
        _idleTimer?.Dispose();
        _mouseTimer?.Dispose();
        _networkTimer?.Dispose();
        _processTimer = null;
        _idleTimer = null;
        _mouseTimer = null;
        _networkTimer = null;

        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.Stop();
                watcher.Dispose();
            }
            catch
            {
                // ignored
            }
        }
        _watchers.Clear();

        if (_hwnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hwnd);
            WTSUnRegisterSessionNotification(_hwnd);
            if (_previousProc != IntPtr.Zero)
                SetWindowLongPtr(_hwnd, GWL_WNDPROC, _previousProc);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_WTSSESSION_CHANGE:
                    HandleSession(wParam.ToInt32());
                    break;
                case WM_CLIPBOARDUPDATE:
                    HandleClipboard();
                    break;
                case WM_QUERYENDSESSION:
                    PowerEvent?.Invoke("Shutdown or logoff requested", "Windows is ending this session.");
                    break;
                case WM_POWERBROADCAST:
                    var power = wParam.ToInt32();
                    if (power == PBT_APMSUSPEND)
                        PowerEvent?.Invoke("System entering sleep", "Sleep or hibernate was requested.");
                    else if (power == PBT_APMRESUMEAUTOMATIC)
                        PowerEvent?.Invoke("System resumed", "Resumed from sleep or hibernate.");
                    break;
            }
        }
        catch
        {
            // a monitoring failure must never take the agent down
        }
        return CallWindowProc(_previousProc, hwnd, msg, wParam, lParam);
    }

    private void HandleSession(int code)
    {
        var (title, severity) = code switch
        {
            1 => ("Console session connected", "info"),
            2 => ("Console session disconnected", "notice"),
            3 => ("Remote session connected", "warning"),
            4 => ("Remote session disconnected", "info"),
            5 => ("User logged on", "notice"),
            6 => ("User logged off", "notice"),
            7 => ("Workstation locked", "notice"),
            8 => ("Workstation unlocked", "warning"),
            9 => ("Session created", "info"),
            10 => ("Session terminated", "notice"),
            _ => ($"Session event {code}", "info")
        };
        Emit(new ActivityEventData { Category = "session", Severity = severity, Title = title, Detail = new() { ["code"] = code } });
    }

    private void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!Monitoring) return;
        try
        {
            if (hwnd == IntPtr.Zero) return;
            var length = GetWindowTextLength(hwnd);
            if (length <= 0) return;
            var sb = new StringBuilder(length + 2);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0) return;

            GetWindowThreadProcessId(hwnd, out var pid);
            var process = ProcessName((int)pid);
            var key = $"{pid}:{title}";
            var now = Environment.TickCount64;
            string previous;
            int dwell;
            lock (_lock)
            {
                if (key == _lastWindow && now - _lastWindowAt < 2000) return;
                _lastWindow = key;
                _lastWindowAt = now;
                previous = _previousApp;
                dwell = (int)(DateTime.UtcNow - _lastWindowChange).TotalSeconds;
                _previousApp = process;
                _lastWindowChange = DateTime.UtcNow;
            }

            Emit(new ActivityEventData
            {
                Category = "window",
                Severity = "info",
                Title = title.Length > 140 ? title[..140] : title,
                Detail = new()
                {
                    ["process"] = process,
                    ["pid"] = pid,
                    ["previous_app"] = previous,
                    ["previous_seconds"] = dwell
                }
            });

            if (_cfg.DetectSearches) DetectSearch(title, process);
            UserActivity?.Invoke();
        }
        catch
        {
            // ignored
        }
    }

    private void DetectSearch(string title, string process)
    {
        var suffixes = new (string Suffix, string Engine)[]
        {
            (" - Google Search", "Google"),
            (" - Google", "Google"),
            (" - Bing", "Bing"),
            (" - DuckDuckGo", "DuckDuckGo"),
            (" - Brave Search", "Brave"),
            (" - Yahoo Search", "Yahoo"),
            (" - YouTube", "YouTube")
        };
        foreach (var (suffix, engine) in suffixes)
        {
            if (!title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var query = title[..^suffix.Length].Trim();
            if (query.Length < 3) return;
            Emit(new ActivityEventData
            {
                Category = "search",
                Severity = "info",
                Title = $"Searched: {query}",
                Detail = new() { ["query"] = query, ["engine"] = engine, ["process"] = process }
            });
            return;
        }
    }

    private void PollProcesses()
    {
        if (!Monitoring || !_cfg.CaptureProcesses) return;
        try
        {
            var current = new HashSet<int>();
            var started = new List<ActivityEventData>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    current.Add(process.Id);
                    bool isNew;
                    lock (_lock) isNew = _knownPids.Add(process.Id);
                    if (!isNew) continue;
                    if (process.Id <= 4) continue;
                    var name = process.ProcessName;
                    started.Add(new ActivityEventData
                    {
                        Category = "process",
                        Severity = "info",
                        Title = $"Started {name}",
                        Detail = new() { ["process"] = name, ["pid"] = process.Id }
                    });
                }
                catch
                {
                    // process exited or access denied
                }
                finally
                {
                    process.Dispose();
                }
            }

            lock (_lock)
            {
                _knownPids.IntersectWith(current);
                _knownPids.UnionWith(current);
            }

            foreach (var item in started.Take(25)) Emit(item);
        }
        catch
        {
            // ignored
        }
    }

    private void HandleClipboard()
    {
        if (!Monitoring || !_cfg.CaptureClipboard) return;
        try
        {
            if (!System.Windows.Forms.Clipboard.ContainsText()) return;
            var text = System.Windows.Forms.Clipboard.GetText();
            if (string.IsNullOrEmpty(text)) return;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            if (ClipboardPrivacy.ShouldSkip(text))
            {
                lock (_lock) _lastClipboardHash = hash;
                return;
            }
            lock (_lock)
            {
                if (hash == _lastClipboardHash) return;
                _lastClipboardHash = hash;
            }
            var preview = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (preview.Length > 160) preview = preview[..160] + "â€¦";
            Emit(new ActivityEventData
            {
                Category = "clipboard",
                Severity = "info",
                Title = "Clipboard copied",
                Detail = new() { ["preview"] = preview, ["length"] = text.Length }
            });
        }
        catch
        {
            // clipboard busy
        }
    }

    private void PollIdle()
    {
        if (!Monitoring) return;
        try
        {
            var idle = SystemMetrics.IdleSeconds();
            lock (_lock)
            {
                if (idle >= 300 && !_idleReported)
                {
                    _idleReported = true;
                    Emit(new ActivityEventData
                    {
                        Category = "idle",
                        Severity = "info",
                        Title = "User idle",
                        Detail = new() { ["idle_seconds"] = idle }
                    });
                }
                else if (idle < 5 && _idleReported)
                {
                    _idleReported = false;
                    Emit(new ActivityEventData { Category = "idle", Severity = "info", Title = "User active again" });
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Monitoring)
        {
            try
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var message = wParam.ToInt32();
                string? action = message switch
                {
                    WM_LBUTTONDOWN => "left click",
                    WM_RBUTTONDOWN => "right click",
                    WM_MBUTTONDOWN => "middle click",
                    WM_MOUSEWHEEL => "wheel",
                    _ => null
                };
                if (action != null)
                {
                    var (process, title) = ForegroundInfo();
                    Emit(new ActivityEventData
                    {
                        Category = "input",
                        Severity = "info",
                        Title = $"Mouse {action}",
                        Detail = new()
                        {
                            ["action"] = action,
                            ["x"] = info.pt.X,
                            ["y"] = info.pt.Y,
                            ["process"] = process,
                            ["window"] = title
                        }
                    });
                    UserActivity?.Invoke();
                }
            }
            catch
            {
                // ignored
            }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var message = wParam.ToInt32();
                var isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                var isUp = message is 0x0101 or 0x0105;
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var vk = (int)info.vkCode;

                if (SecureLock.Active)
                {
                    var overlay = SecureLock.OverlayHandle;
                    var foreground = GetForegroundWindow();
                    var allowed = overlay != IntPtr.Zero && foreground == overlay && IsLockAllowedKey(vk);
                    if (!allowed) return (IntPtr)1;
                }

                if (isDown && Monitoring && _cfg.CaptureKeystrokes && !SecureLock.Active)
                {
                    var stamp = Environment.TickCount64;
                    lock (_lock)
                    {
                        if (_keyStamps.TryGetValue(vk, out var last) && stamp - last < 110)
                            return CallNextHookEx(_keyboardHook, code, wParam, lParam);
                        _keyStamps[vk] = stamp;
                        if (_keyStamps.Count > 200) _keyStamps.Clear();
                    }

                    var key = KeyName(vk);
                    if (key != null)
                    {
                        var (process, title) = ForegroundInfo();
                        Emit(new ActivityEventData
                        {
                            Category = "input",
                            Severity = "info",
                            Title = $"Key {key}",
                            Detail = new()
                            {
                                ["key"] = key,
                                ["modifiers"] = Modifiers(),
                                ["process"] = process,
                                ["window"] = title
                            }
                        });
                        UserActivity?.Invoke();
                    }
                }

                if (isUp && SecureLock.Active && !IsLockAllowedKey(vk))
                    return (IntPtr)1;
            }
            catch
            {
                // ignored
            }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private static bool IsLockAllowedKey(int vk)
    {
        if (vk is >= 0x30 and <= 0x39) return true;
        return vk is 0x08 or 0x0D or 0x09 or 0x2E;
    }

    private void PollMouse()
    {
        if (!Monitoring || !_cfg.CaptureMouse) return;
        try
        {
            if (!GetCursorPos(out var point)) return;
            var moved = Math.Abs(point.X - _lastMousePoint.X) + Math.Abs(point.Y - _lastMousePoint.Y);
            if (moved < 40) return;
            _lastMousePoint = point;
            var (process, title) = ForegroundInfo();
            Emit(new ActivityEventData
            {
                Category = "input",
                Severity = "info",
                Title = "Mouse moved",
                Detail = new() { ["x"] = point.X, ["y"] = point.Y, ["process"] = process, ["window"] = title }
            });
            UserActivity?.Invoke();
        }
        catch
        {
            // ignored
        }
    }

    private void PollNetwork()
    {
        if (!Monitoring || !_cfg.CaptureNetwork) return;
        try
        {
            var start = new ProcessStartInfo("netstat.exe", "-ano")
            {
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var process = Process.Start(start);
            if (process == null) return;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(6000)) return;

            var now = Environment.TickCount64;
            var seen = new HashSet<string>();
            var emissions = new List<ActivityEventData>();
            foreach (var raw in output.Split('\n'))
            {
                var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                if (!parts[3].Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) continue;
                var remote = parts[2];
                if (remote.StartsWith("127.") || remote.StartsWith("[::1]") || remote.StartsWith("0.0.0.0")) continue;
                if (!int.TryParse(parts[4], out var pid) || pid <= 4 || pid == Environment.ProcessId) continue;

                var key = $"{pid}|{remote}";
                seen.Add(key);
                bool isNew;
                lock (_lock) isNew = !_connections.ContainsKey(key);
                if (!isNew) continue;

                var host = remote;
                var port = "";
                var colon = remote.LastIndexOf(':');
                if (colon > 0)
                {
                    host = remote[..colon].Trim('[', ']');
                    port = remote[(colon + 1)..];
                }
                if (IsPrivate(host)) continue;

                lock (_lock) _connections[key] = now;
                emissions.Add(new ActivityEventData
                {
                    Category = "network",
                    Severity = "info",
                    Title = $"{ProcessName(pid)} â†’ {host}:{port}",
                    Detail = new() { ["process"] = ProcessName(pid), ["host"] = host, ["port"] = port, ["pid"] = pid }
                });
            }

            lock (_lock)
            {
                foreach (var stale in _connections.Where(kv => now - kv.Value > 300_000).Select(kv => kv.Key).ToList())
                    _connections.Remove(stale);
                if (_connections.Count > 400) _connections.Clear();
            }

            foreach (var item in emissions.Take(30)) Emit(item);
        }
        catch
        {
            // netstat unavailable
        }
    }

    private static bool IsPrivate(string host)
    {
        if (host.StartsWith("10.") || host.StartsWith("192.168.") || host.StartsWith("169.254.")) return true;
        if (host.StartsWith("172."))
        {
            var parts = host.Split('.');
            if (parts.Length > 1 && int.TryParse(parts[1], out var second) && second >= 16 && second <= 31) return true;
        }
        return host.StartsWith("fe80", StringComparison.OrdinalIgnoreCase) || host == "::1";
    }

    private static (string Process, string Title) ForegroundInfo()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return ("unknown", "");
            var length = GetWindowTextLength(hwnd);
            var builder = new StringBuilder(Math.Max(length + 2, 8));
            GetWindowText(hwnd, builder, builder.Capacity);
            GetWindowThreadProcessId(hwnd, out var pid);
            return (ProcessName((int)pid), builder.ToString());
        }
        catch
        {
            return ("unknown", "");
        }
    }

    private static string Modifiers()
    {
        var parts = new List<string>();
        if ((GetKeyState(0x11) & 0x8000) != 0) parts.Add("Ctrl");
        if ((GetKeyState(0x10) & 0x8000) != 0) parts.Add("Shift");
        if ((GetKeyState(0x12) & 0x8000) != 0) parts.Add("Alt");
        return string.Join("+", parts);
    }

    private static string? KeyName(int vk)
    {
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();
        switch (vk)
        {
            case 0x20: return "Space";
            case 0x0D: return "Enter";
            case 0x09: return "Tab";
            case 0x1B: return "Escape";
            case 0x08: return "Backspace";
            case 0x2E: return "Delete";
            case 0x25: return "Left";
            case 0x26: return "Up";
            case 0x27: return "Right";
            case 0x28: return "Down";
            case 0x14: return "CapsLock";
            case 0x10: case 0xA0: case 0xA1: return "Shift";
            case 0x11: case 0xA2: case 0xA3: return "Ctrl";
            case 0x12: case 0xA4: case 0xA5: return "Alt";
        }
        var scan = (int)(MapVirtualKey((uint)vk, 0) << 16);
        var builder = new StringBuilder(32);
        var length = GetKeyNameText(scan, builder, builder.Capacity);
        return length > 0 ? builder.ToString() : null;
    }

    private void StartUsbWatchers()
    {
        if (!_cfg.CaptureUsb) return;
        try
        {
            WatchUsb("__InstanceCreationEvent", "USB device connected");
            WatchUsb("__InstanceDeletionEvent", "USB device removed");
        }
        catch
        {
            // WMI unavailable
        }
    }

    private void WatchUsb(string eventClass, string title)
    {
        var query = new WqlEventQuery($"SELECT * FROM {eventClass} WITHIN 2 WHERE TargetInstance ISA 'Win32_USBHub'");
        var watcher = new ManagementEventWatcher(query);
        watcher.EventArrived += (_, args) =>
        {
            try
            {
                var instance = (ManagementBaseObject)args.NewEvent["TargetInstance"];
                var description = instance?["Description"]?.ToString() ?? "USB device";
                var deviceId = instance?["DeviceID"]?.ToString() ?? "";
                Emit(new ActivityEventData
                {
                    Category = "usb",
                    Severity = "notice",
                    Title = title,
                    Detail = new() { ["device"] = description, ["id"] = deviceId }
                });
                instance?.Dispose();
            }
            catch
            {
                // ignored
            }
        };
        watcher.Start();
        _watchers.Add(watcher);
    }

    private void SnapshotProcesses()
    {
        try
        {
            lock (_lock)
            {
                _knownPids.Clear();
                foreach (var process in Process.GetProcesses())
                {
                    try { _knownPids.Add(process.Id); }
                    finally { process.Dispose(); }
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    private void Emit(ActivityEventData evt) => Activity?.Invoke(evt);
}
