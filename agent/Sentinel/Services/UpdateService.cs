using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sentinel.Services;

public sealed class UpdateInfo
{
    public string Product { get; set; } = "";
    public string Agent { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Watch { get; set; } = "";
    public string Tag { get; set; } = "";
    public string ReleaseUrl { get; set; } = "https://github.com/WhatAVibe/Sentinel/releases/latest";
    public long CheckedAt { get; set; }

    public bool ProductOutdated => Newer(Tag, AgentInfo.ProductVersion);
    public bool AgentOutdated => Newer(Agent, AgentInfo.Version);
    public bool AnyOutdated => ProductOutdated || AgentOutdated;

    public static bool Newer(string candidate, string current)
    {
        var a = Parse(candidate);
        var b = Parse(current);
        if (a == null || b == null) return false;
        for (var i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i] > b[i];
        }
        return false;
    }

    private static int[]? Parse(string value)
    {
        var match = Regex.Match(value ?? "", @"(\d+)\.(\d+)\.(\d+)");
        if (!match.Success) return null;
        return new[]
        {
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value)
        };
    }
}

public static class Updates
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly string[] TrackedFiles =
    {
        "Sentinel-Agent-", "Sentinel-Phone-", "Sentinel-Watch-"
    };

    public static UpdateInfo Current { get; private set; } = new();
    public static event Action? Changed;
    public static DateTime LastCheck { get; private set; } = DateTime.MinValue;

    public static async Task<UpdateInfo> CheckAsync(bool manual = false)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://api.github.com/repos/WhatAVibe/Sentinel/releases/latest");
            request.Headers.UserAgent.ParseAdd($"SentinelAgent/{AgentInfo.Version}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Current;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = document.RootElement;
            var info = new UpdateInfo
            {
                Tag = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "",
                ReleaseUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "",
                CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.StartsWith("Sentinel-Agent-")) info.Agent = VersionFrom(name, "Sentinel-Agent-");
                    else if (name.StartsWith("Sentinel-Phone-")) info.Phone = VersionFrom(name, "Sentinel-Phone-");
                    else if (name.StartsWith("Sentinel-Watch-")) info.Watch = VersionFrom(name, "Sentinel-Watch-");
                }
            }
            Current = info;
            LastCheck = DateTime.Now;
            Changed?.Invoke();
            return info;
        }
        catch
        {
            return Current;
        }
    }

    private static string VersionFrom(string name, string prefix)
    {
        var rest = name[prefix.Length..];
        var match = Regex.Match(rest, @"^\d+\.\d+\.\d+");
        return match.Success ? match.Value : "";
    }

    public static void OpenReleasePage()
    {
        try
        {
            var url = string.IsNullOrWhiteSpace(Current.ReleaseUrl)
                ? "https://github.com/WhatAVibe/Sentinel/releases/latest"
                : Current.ReleaseUrl;
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // ignored
        }
    }
}
