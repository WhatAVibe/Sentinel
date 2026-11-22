using System.Collections.Generic;

namespace Sentinel.Services;

public sealed record LogEntry(DateTime Time, string Level, string Message, string? Detail)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => Level.ToUpperInvariant();
}

public static class Diagnostics
{
    private static readonly object Gate = new();
    private static readonly List<LogEntry> Buffer = new();
    private const int Capacity = 2500;

    public static event Action<LogEntry>? Added;

    public static void Add(string level, string message, string? detail = null)
    {
        var entry = new LogEntry(DateTime.Now, level, message, detail);
        lock (Gate)
        {
            Buffer.Add(entry);
            if (Buffer.Count > Capacity) Buffer.RemoveRange(0, Buffer.Count - Capacity);
        }
        try
        {
            Added?.Invoke(entry);
        }
        catch
        {
            // a broken listener must never break logging
        }
    }

    public static LogEntry[] Snapshot(int max = 600)
    {
        lock (Gate)
        {
            return Buffer.Count <= max ? Buffer.ToArray() : Buffer.GetRange(Buffer.Count - max, max).ToArray();
        }
    }

    public static void Clear()
    {
        lock (Gate) Buffer.Clear();
    }
}
