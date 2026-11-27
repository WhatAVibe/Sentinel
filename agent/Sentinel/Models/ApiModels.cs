using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentinel.Models;

public sealed class ServerInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public sealed class HealthResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("public_url")] public string PublicUrl { get; set; } = "";
}

public sealed class PairRequestResponse
{
    [JsonPropertyName("request_id")] public string RequestId { get; set; } = "";
    [JsonPropertyName("pair_code")] public string PairCode { get; set; } = "";
    [JsonPropertyName("expires_at")] public long ExpiresAt { get; set; }
    [JsonPropertyName("request_token")] public string RequestToken { get; set; } = "";
    [JsonPropertyName("server")] public ServerInfo? Server { get; set; }
}

public sealed class PairStatusResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("agent_token")] public string? AgentToken { get; set; }
    [JsonPropertyName("totp_secret")] public string? TotpSecret { get; set; }
    [JsonPropertyName("device")] public DeviceDto? Device { get; set; }
    [JsonPropertyName("server")] public ServerInfo? Server { get; set; }
}

public sealed class LinkRequestResponse
{
    [JsonPropertyName("request_id")] public string RequestId { get; set; } = "";
    [JsonPropertyName("pair_code")] public string PairCode { get; set; } = "";
    [JsonPropertyName("expires_at")] public long ExpiresAt { get; set; }
    [JsonPropertyName("server")] public ServerInfo? Server { get; set; }
}

public sealed class MetricsDto
{
    [JsonPropertyName("cpu_percent")] public double CpuPercent { get; set; }
    [JsonPropertyName("mem_used_mb")] public long MemUsedMb { get; set; }
    [JsonPropertyName("mem_total_mb")] public long MemTotalMb { get; set; }
    [JsonPropertyName("uptime_seconds")] public long UptimeSeconds { get; set; }
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = "";
    [JsonPropertyName("disk_used_mb")] public long DiskUsedMb { get; set; }
    [JsonPropertyName("disk_total_mb")] public long DiskTotalMb { get; set; }
    [JsonPropertyName("disk_free_mb")] public long DiskFreeMb { get; set; }
    [JsonPropertyName("battery_percent")] public int BatteryPercent { get; set; } = -1;
    [JsonPropertyName("battery_charging")] public bool BatteryCharging { get; set; }
}

public sealed class StateDto
{
    [JsonPropertyName("monitors")] public int Monitors { get; set; }
    [JsonPropertyName("session")] public string Session { get; set; } = "active";
    [JsonPropertyName("screensaver")] public bool Screensaver { get; set; }
    [JsonPropertyName("idle_seconds")] public int IdleSeconds { get; set; }
    [JsonPropertyName("away_reason")] public string? AwayReason { get; set; }
    [JsonPropertyName("agent_uptime_seconds")] public long AgentUptimeSeconds { get; set; }
    [JsonPropertyName("local_api")] public bool LocalApi { get; set; }
    [JsonPropertyName("phone_present")] public bool PhonePresent { get; set; }
    [JsonPropertyName("key_present")] public bool KeyPresent { get; set; }
    [JsonPropertyName("key_label")] public string? KeyLabel { get; set; }
    [JsonPropertyName("secure_lock")] public bool SecureLock { get; set; }
}

public sealed class DeviceSettingsDto
{
    [JsonPropertyName("require_2fa_unlock")] public bool Require2faUnlock { get; set; } = true;
    [JsonPropertyName("protect_power")] public bool ProtectPower { get; set; }
    [JsonPropertyName("power_mode")] public string PowerMode { get; set; } = "confirm";
    [JsonPropertyName("power_message")] public string PowerMessage { get; set; } = "";
}

public sealed class DeviceDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "windows";
    [JsonPropertyName("online")] public bool Online { get; set; }
    [JsonPropertyName("last_seen")] public long? LastSeen { get; set; }
    [JsonPropertyName("away")] public bool Away { get; set; }
    [JsonPropertyName("locked")] public bool Locked { get; set; }
    [JsonPropertyName("settings")] public DeviceSettingsDto Settings { get; set; } = new();
    [JsonPropertyName("state")] public StateDto State { get; set; } = new();
    [JsonPropertyName("metrics")] public MetricsDto Metrics { get; set; } = new();
    [JsonPropertyName("relay")] public bool Relay { get; set; }
    [JsonPropertyName("agent_version")] public string? AgentVersion { get; set; }
    [JsonPropertyName("linked_devices")] public int LinkedDevices { get; set; }
    [JsonPropertyName("totp_secret")] public string? TotpSecret { get; set; }
}

public sealed class CommandEnvelope
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("args")] public Dictionary<string, JsonElement>? Args { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
}

public sealed class CommandList
{
    [JsonPropertyName("commands")] public List<CommandEnvelope> Commands { get; set; } = new();
}

public sealed class ScreenshotDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("monitor")] public int Monitor { get; set; }
    [JsonPropertyName("monitor_name")] public string? MonitorName { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("trigger")] public string? Trigger { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class ScreenshotUploadResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("screenshot")] public ScreenshotDto? Screenshot { get; set; }
}

public sealed class HeartbeatResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("server_time")] public long ServerTime { get; set; }
    [JsonPropertyName("device")] public DeviceDto? Device { get; set; }
}

public sealed class PairApproveResponse
{    [JsonPropertyName("device")] public DeviceDto? Device { get; set; }
    [JsonPropertyName("mobile_token")] public string? MobileToken { get; set; }
    [JsonPropertyName("totp_secret")] public string? TotpSecret { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class PhoneDto
{    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "Phone";
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("last_seen")] public long? LastSeen { get; set; }
    [JsonPropertyName("current")] public bool Current { get; set; }
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("confirmed")] public bool Confirmed { get; set; } = true;
    [JsonPropertyName("app_version")] public string AppVersion { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "phone";

    [JsonIgnore] public string CreatedText => CreatedAt == 0
        ? "—"
        : DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).LocalDateTime.ToString("dd MMM yyyy HH:mm");

    [JsonIgnore] public string LastSeenText => LastSeen == null
        ? "never"
        : DateTimeOffset.FromUnixTimeMilliseconds(LastSeen.Value).LocalDateTime.ToString("dd MMM HH:mm");

    [JsonIgnore] public string Badge => Current ? "THIS PHONE" : "LINKED";
    [JsonIgnore] public System.Windows.Visibility ConfirmVisibility =>
        Confirmed ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    [JsonIgnore] public System.Windows.Visibility PrimaryVisibility =>
        Primary ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    [JsonIgnore] public System.Windows.Visibility NotPrimaryVisibility =>
        Primary ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    [JsonIgnore] public bool NotPrimary => !Primary;
    [JsonIgnore] public bool Outdated { get; set; }
    [JsonIgnore] public string VersionText =>
        $"{(string.Equals(Kind, "watch", StringComparison.OrdinalIgnoreCase) ? "watch" : "app")} " +
        $"{(string.IsNullOrWhiteSpace(AppVersion) ? "version unknown" : "v" + AppVersion)}";
    [JsonIgnore] public System.Windows.Visibility OutdatedVisibility =>
        Outdated ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
}

public sealed class PhoneList
{
    [JsonPropertyName("phones")] public List<PhoneDto> Phones { get; set; } = new();
}

public sealed class ActivityEventData
{
    public long Ts { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string Category { get; init; } = "system";
    public string Severity { get; init; } = "info";
    public string Title { get; init; } = "";
    public Dictionary<string, object?>? Detail { get; init; }
    public string? Enc { get; init; }

    public object ToWire() => string.IsNullOrEmpty(Enc)
        ? new
        {
            ts = Ts,
            category = Category,
            severity = Severity,
            title = Title,
            detail = Detail
        }
        : new
        {
            ts = Ts,
            category = Category,
            severity = Severity,
            enc = Enc
        };
}

public sealed class CaptureResult
{
    public string Path { get; init; } = "";
    public int Monitor { get; init; }
    public string MonitorName { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public string Trigger { get; init; } = "manual";
    public DateTime CapturedAt { get; init; } = DateTime.Now;
}

public sealed class AgentEventDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("category")] public string Category { get; set; } = "system";
    [JsonPropertyName("severity")] public string Severity { get; set; } = "info";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("detail")] public Dictionary<string, JsonElement>? Detail { get; set; }
}

public sealed class AgentEventsResponse
{
    [JsonPropertyName("events")] public List<AgentEventDto> Events { get; set; } = new();
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }
}
