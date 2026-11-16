using System.IO;
using System.Text;
using System.Text.Json;

namespace PCMonitor.Agent.Services;

public sealed class ActivityLog
{
    private readonly object _lock = new();
    private readonly string _directory;

    public ActivityLog(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public void Write(string level, string message, object? data = null)
    {
        string? detail = null;
        try
        {
            detail = data == null ? null : JsonSerializer.Serialize(data);
        }
        catch
        {
            detail = data?.ToString();
        }
        Diagnostics.Add(level, message, detail);

        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            level,
            message,
            data
        });
        lock (_lock)
        {
            try
            {
                var path = Path.Combine(_directory, $"agent-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                TrimOlderThan(10);
            }
            catch
            {
                // logging must never throw
            }
        }
    }

    public string[] Tail(int lines = 300)
    {
        try
        {
            var path = Path.Combine(_directory, $"agent-{DateTime.Now:yyyyMMdd}.log");
            if (!File.Exists(path)) return Array.Empty<string>();
            var all = File.ReadAllLines(path);
            return all.Length <= lines ? all : all[^lines..];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void TrimOlderThan(int days)
    {
        foreach (var file in Directory.GetFiles(_directory, "agent-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-days)) File.Delete(file);
            }
            catch
            {
                // ignored
            }
        }
    }
}
