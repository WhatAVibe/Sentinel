using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCMonitor.Agent.Models;

namespace PCMonitor.Agent.Services;

public sealed class ActivityItem
{
    public long Id { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    public string Category { get; init; } = "system";
    public string Severity { get; init; } = "info";
    public string Title { get; init; } = "";
    public string DetailText { get; init; } = "";
    public Brush Accent { get; init; } = UiState.CategoryBrush("system");
    public string CategoryLabel => Category.ToUpperInvariant();
    public string TimeText => Time.ToString("HH:mm:ss");
    public string DayText => Time.ToString("ddd, dd MMM");
    public Visibility DetailVisibility => string.IsNullOrWhiteSpace(DetailText) ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class ShotItem : INotifyPropertyChanged
{
    private BitmapImage? _thumb;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; init; } = "";
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public int Monitor { get; init; }
    public string MonitorName { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public string Trigger { get; init; } = "manual";

    public BitmapImage? Thumb
    {
        get => _thumb;
        set
        {
            _thumb = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
        }
    }

    public string Caption => $"{(string.IsNullOrEmpty(MonitorName) ? "Display " + Monitor : MonitorName)} · {Width}×{Height}";
    public string TimeText => CapturedAt.ToString("dd MMM · HH:mm:ss");
    public string TriggerText => Trigger.ToUpperInvariant();
}

public sealed class UiState : INotifyPropertyChanged
{
    public static UiState I { get; } = new();

    private readonly ObservableCollection<ActivityItem> _activity = new();
    private readonly ObservableCollection<ShotItem> _shots = new();
    private readonly object _shotLock = new();
    private readonly List<ShotItem> _shotMirror = new();

    private string _connection = "Offline";
    private bool _away;
    private string _deviceName = Environment.MachineName;
    private string _userName = Environment.UserName;
    private string _hostname = Environment.MachineName;
    private string _serverLabel = "Not paired";
    private string _serverUrl = "";
    private bool _paired;
    private double _cpu;
    private double _memPercent;
    private string _memText = "";
    private string _uptimeText = "—";
    private int _monitors;
    private string _session = "active";
    private string _lastSeen = "never";
    private int _shotCount;
    private int _eventCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ActivityItem> Activity => _activity;
    public ObservableCollection<ShotItem> Shots => _shots;

    public string Connection { get => _connection; set => Set(ref _connection, value, nameof(Connection), nameof(ConnectionBrush)); }
    public bool Away { get => _away; set => Set(ref _away, value, nameof(Away), nameof(AwayText), nameof(AwayVisibility)); }
    public string DeviceName { get => _deviceName; set => Set(ref _deviceName, value); }
    public string UserName { get => _userName; set => Set(ref _userName, value); }
    public string Hostname { get => _hostname; set => Set(ref _hostname, value); }
    public string ServerLabel { get => _serverLabel; set => Set(ref _serverLabel, value); }
    public string ServerUrl { get => _serverUrl; set => Set(ref _serverUrl, value); }
    public bool Paired { get => _paired; set => Set(ref _paired, value, nameof(Paired), nameof(NotPairedVisibility), nameof(PairedVisibility)); }
    public double Cpu { get => _cpu; set => Set(ref _cpu, value, nameof(Cpu), nameof(CpuText)); }
    public double MemPercent { get => _memPercent; set => Set(ref _memPercent, value, nameof(MemPercent)); }
    public string MemText { get => _memText; set => Set(ref _memText, value); }
    public string UptimeText { get => _uptimeText; set => Set(ref _uptimeText, value); }
    public int Monitors { get => _monitors; set => Set(ref _monitors, value); }
    public string Session { get => _session; set => Set(ref _session, value); }
    public string LastSeen { get => _lastSeen; set => Set(ref _lastSeen, value); }
    public int ShotCount { get => _shotCount; set => Set(ref _shotCount, value, nameof(ShotCount), nameof(EmptyShotsVisibility), nameof(ShotsVisibility)); }
    public int EventCount { get => _eventCount; set => Set(ref _eventCount, value, nameof(EventCount), nameof(EmptyActivityVisibility)); }

    public string AwayText => Away ? "Away mode active" : "Monitoring idle";
    public Visibility AwayVisibility => Away ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PairedVisibility => Paired ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NotPairedVisibility => Paired ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyShotsVisibility => ShotCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShotsVisibility => ShotCount == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyActivityVisibility => EventCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string CpuText => $"{Cpu:0}%";

    public Brush ConnectionBrush => Connection switch
    {
        "Live" => CategoryBrush("clipboard"),
        "Direct" => CategoryBrush("screenshot"),
        "Connecting" => CategoryBrush("usb"),
        _ => CategoryBrush("system")
    };

    public static Brush CategoryBrush(string category) => category switch
    {
        "window" => Frozen("#6C8CFF"),
        "process" => Frozen("#A78BFA"),
        "usb" => Frozen("#F2C46D"),
        "clipboard" => Frozen("#6EE7A8"),
        "session" => Frozen("#FF9F6B"),
        "security" => Frozen("#FF6B6B"),
        "screenshot" => Frozen("#5BC0EB"),
        "power" => Frozen("#F2A65A"),
        "idle" => Frozen("#7C8798"),
        _ => Frozen("#8B93A1")
    };

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static void Post(Action action)
    {
        var app = Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.BeginInvoke(action);
    }

    public void AddActivity(ActivityEventData data)
    {
        var detail = "";
        if (data.Detail is { Count: > 0 })
            detail = string.Join("  ·  ", data.Detail.Where(kv => kv.Value != null).Select(kv => $"{kv.Key}: {kv.Value}"));
        AddActivity(new ActivityItem
        {
            Time = DateTimeOffset.FromUnixTimeMilliseconds(data.Ts).LocalDateTime,
            Category = data.Category,
            Severity = data.Severity,
            Title = data.Title,
            DetailText = detail,
            Accent = CategoryBrush(data.Category)
        });
    }

    public void AddActivity(ActivityItem item)
    {
        Post(() =>
        {
            _activity.Insert(0, item);
            while (_activity.Count > 800) _activity.RemoveAt(_activity.Count - 1);
            EventCount = _activity.Count;
        });
    }

    public void AddShot(CaptureResult capture)    {
        var item = new ShotItem
        {
            Path = capture.Path,
            CapturedAt = capture.CapturedAt,
            Monitor = capture.Monitor,
            MonitorName = capture.MonitorName,
            Width = capture.Width,
            Height = capture.Height,
            Trigger = capture.Trigger
        };
        Post(() =>
        {
            _shots.Insert(0, item);
            while (_shots.Count > 400) _shots.RemoveAt(_shots.Count - 1);
            ShotCount = _shots.Count;
        });
        TrackShot(item);

        Task.Run(() =>
        {
            var thumb = ScreenCapture.LoadThumbnail(capture.Path, 560);
            if (thumb != null) Post(() => item.Thumb = thumb);
        });
    }

    public void TrackShot(ShotItem item)
    {
        lock (_shotLock)
        {
            _shotMirror.Insert(0, item);
            while (_shotMirror.Count > 400) _shotMirror.RemoveAt(_shotMirror.Count - 1);
        }
    }

    public ShotItem[] SnapshotShots()
    {
        lock (_shotLock)
        {
            return _shotMirror.ToArray();
        }
    }

    public ActivityItem[] SnapshotActivity()
    {
        var app = Application.Current;
        if (app == null) return _activity.ToArray();
        return app.Dispatcher.CheckAccess() ? _activity.ToArray() : app.Dispatcher.Invoke(() => _activity.ToArray());
    }

    public void UpdateFromDevice(DeviceDto device)
    {
        Post(() =>
        {
            DeviceName = device.Name;
            Away = device.Away;
            if (device.Metrics != null)
            {
                Cpu = device.Metrics.CpuPercent;
                MemPercent = device.Metrics.MemTotalMb > 0
                    ? Math.Round(device.Metrics.MemUsedMb * 100.0 / device.Metrics.MemTotalMb, 1)
                    : 0;
                MemText = $"{device.Metrics.MemUsedMb / 1024.0:0.0} / {device.Metrics.MemTotalMb / 1024.0:0.0} GB";
                UserName = device.Metrics.User;
                Hostname = device.Metrics.Hostname;
                UptimeText = FormatUptime(device.Metrics.UptimeSeconds);
            }
            if (device.State != null)
            {
                Monitors = device.State.Monitors;
                Session = device.State.Session;
            }
            if ((device.LastSeen ?? 0) > 0)
            {
                var seen = DateTimeOffset.FromUnixTimeMilliseconds(device.LastSeen!.Value).LocalDateTime;
                LastSeen = seen.ToString("HH:mm:ss");
            }
            Paired = true;
        });
    }

    private static string FormatUptime(long seconds)
    {
        if (seconds <= 0) return "—";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : $"{span.Hours}h {span.Minutes}m";
    }

    private void Set<T>(ref T field, T value, params string[] properties)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        if (properties.Length == 0)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            return;
        }
        foreach (var property in properties)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
