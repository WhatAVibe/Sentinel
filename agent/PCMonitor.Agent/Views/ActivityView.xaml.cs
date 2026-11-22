using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCMonitor.Agent.Models;
using PCMonitor.Agent.Services;

namespace PCMonitor.Agent.Views;

public partial class ActivityView : UserControl
{
    private static readonly (string Key, string Label)[] Filters =
    {
        ("all", "All"),
        ("window", "Windows"),
        ("process", "Apps"),
        ("usb", "USB"),
        ("clipboard", "Clipboard"),
        ("session", "Session"),
        ("security", "Security"),
        ("power", "Power")
    };

    private readonly ObservableCollection<ActivityItem> _filtered = new();
    private readonly List<ActivityItem> _history = new();
    private readonly List<Button> _chips = new();
    private readonly DispatcherTimer _refreshTimer;
    private string _filter = "all";

    public ActivityView()
    {
        InitializeComponent();
        DataContext = UiState.I;
        EventsList.ItemsSource = _filtered;

        foreach (var (key, label) in Filters)
        {
            var chip = new Button
            {
                Content = label,
                Tag = key,
                Style = (Style)Application.Current.Resources["GhostButton"],
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 8),
                FontSize = 12
            };
            chip.Click += Chip_Click;
            _chips.Add(chip);
            Chips.Children.Add(chip);
        }

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Rebuild();
        };

        UiState.I.Activity.CollectionChanged += Activity_CollectionChanged;
        Loaded += (_, _) =>
        {
            UpdateChips();
            Rebuild();
            _ = LoadHistoryAsync();
        };
        Unloaded += (_, _) => UiState.I.Activity.CollectionChanged -= Activity_CollectionChanged;
    }

    private async Task LoadHistoryAsync()
    {
        if (!App.Host.Config.Paired) return;
        try
        {
            var page = await App.Host.Api.GetEventsAsync(0, 400, CancellationToken.None);
            _history.Clear();
            foreach (var evt in page.Events)
                _history.Add(ToItem(evt));
            Rebuild();
        }
        catch
        {
            // offline; live events still show
        }
    }

    private static ActivityItem ToItem(AgentEventDto evt)
    {
        var detail = "";
        if (evt.Detail is { Count: > 0 })
        {
            detail = string.Join("  ·  ", evt.Detail
                .Where(kv => kv.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                .Select(kv => $"{kv.Key}: {kv.Value}"));
        }
        return new ActivityItem
        {
            Id = evt.Id,
            Time = DateTimeOffset.FromUnixTimeMilliseconds(evt.Ts).LocalDateTime,
            Category = evt.Category,
            Severity = evt.Severity,
            Title = evt.Title,
            DetailText = detail,
            Accent = UiState.CategoryBrush(evt.Category)
        };
    }

    private void Activity_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button chip) return;
        _filter = chip.Tag as string ?? "all";
        UpdateChips();
        Rebuild();
    }

    private void UpdateChips()
    {
        foreach (var chip in _chips)
        {
            var active = string.Equals(chip.Tag as string, _filter, StringComparison.Ordinal);
            chip.Background = active
                ? (System.Windows.Media.Brush)Application.Current.Resources["AccentDim"]
                : (System.Windows.Media.Brush)Application.Current.Resources["Elevated"];
            chip.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[active ? "Text" : "Muted"];
        }
    }

    private void Rebuild()
    {
        var live = UiState.I.Activity.ToList();
        var known = _history.Select(h => $"{h.Time.Ticks}|{h.Title}").ToHashSet();
        var merged = _history
            .Concat(live.Where(item => !known.Contains($"{item.Time.Ticks}|{item.Title}")))
            .OrderByDescending(item => item.Time)
            .Where(Matches)
            .Take(600)
            .ToList();
        _filtered.Clear();
        foreach (var item in merged) _filtered.Add(item);
        EmptyState.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool Matches(ActivityItem item)
    {
        if (_filter == "all") return true;
        if (_filter == "session") return item.Category is "session" or "idle";
        if (_filter == "security") return item.Category is "security" or "screenshot";
        return string.Equals(item.Category, _filter, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = App.Host.Store.LogsDir,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignored
        }
    }
}
