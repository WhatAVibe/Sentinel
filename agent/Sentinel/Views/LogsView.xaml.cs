using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sentinel.Services;

namespace Sentinel.Views;

public partial class LogsView : UserControl
{
    private readonly ObservableCollection<LogEntry> _visible = new();
    private readonly List<LogEntry> _all = new();
    private const int MaxEntries = 1500;

    public LogsView()
    {
        InitializeComponent();
        LogList.ItemsSource = _visible;
        Loaded += (_, _) =>
        {
            if (_all.Count == 0)
            {
                foreach (var entry in Diagnostics.Snapshot()) AddEntry(entry, broadcast: false);
                Refresh();
            }
            Diagnostics.Added += OnAdded;
        };
        Unloaded += (_, _) => Diagnostics.Added -= OnAdded;
    }

    private void OnAdded(LogEntry entry)
    {
        UiState.Post(() => AddEntry(entry, broadcast: true));
    }

    private void AddEntry(LogEntry entry, bool broadcast)
    {
        _all.Add(entry);
        if (_all.Count > MaxEntries) _all.RemoveRange(0, _all.Count - MaxEntries);

        var query = SearchBox.Text.Trim();
        if (query.Length == 0 || Matches(entry, query))
        {
            _visible.Add(entry);
            while (_visible.Count > MaxEntries) _visible.RemoveAt(0);
            CountText.Text = $"{_all.Count} entries";
            if (broadcast && AutoScroll.IsChecked == true && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        }
        else if (_all.Count % 10 == 0)
        {
            CountText.Text = $"{_all.Count} entries";
        }
    }

    private static bool Matches(LogEntry entry, string query) =>
        entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.Level.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (entry.Detail?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    private void Refresh()
    {
        var query = SearchBox.Text.Trim();
        _visible.Clear();
        foreach (var entry in _all)
        {
            if (query.Length == 0 || Matches(entry, query)) _visible.Add(entry);
        }
        CountText.Text = $"{_all.Count} entries";
        if (_visible.Count > 0) LogList.ScrollIntoView(_visible[^1]);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, _visible.Select(entry =>
            $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.LevelText}] {entry.Message}" +
            (string.IsNullOrWhiteSpace(entry.Detail) ? "" : $" | {entry.Detail}")));
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // clipboard busy
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Diagnostics.Clear();
        _all.Clear();
        _visible.Clear();
        CountText.Text = "0 entries";
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = App.Host.Store.LogsDir, UseShellExecute = true });
        }
        catch
        {
            // ignored
        }
    }
}
