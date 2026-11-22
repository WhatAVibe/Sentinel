using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PCMonitor.Agent.Models;

namespace PCMonitor.Agent.Services;

public sealed class ApiException : Exception
{
    public int Status { get; }
    public string Code { get; }

    public ApiException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }
}

public sealed class ApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _http;
    public string BaseUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public bool AllowInsecureTls { get; set; }

    public event Action? Unauthorized;
    public event Action<string>? Trace;

    private static string Sanitize(string path) =>
        Regex.Replace(path, @"(token=)[^&]+", "$1***", RegexOptions.IgnoreCase);

    public ApiClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, errors) =>
                AllowInsecureTls || errors == System.Net.Security.SslPolicyErrors.None
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PCMonitorAgent/{AgentInfo.Version}");
    }

    private string Url(string path) => BaseUrl.TrimEnd('/') + path;

    private HttpRequestMessage Request(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, Url(path));
        if (!string.IsNullOrEmpty(Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        }
        return request;
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var request = Request(method, path, body);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Trace?.Invoke($"{(int)response.StatusCode} {method.Method} {Sanitize(path)} ({watch.ElapsedMilliseconds} ms)");
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Unauthorized?.Invoke();
                throw new ApiException(401, "unauthorized", "device token rejected");
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = ExtractError(payload) ?? $"http_{(int)response.StatusCode}";
                throw new ApiException((int)response.StatusCode, code, code);
            }
            if (typeof(T) == typeof(string)) return (T)(object)payload;
            if (string.IsNullOrWhiteSpace(payload)) return default;
            return JsonSerializer.Deserialize<T>(payload, Json);
        }
        catch (Exception ex) when (ex is not ApiException && ex is not OperationCanceledException)
        {
            Trace?.Invoke($"FAILED {method.Method} {Sanitize(path)} ({watch.ElapsedMilliseconds} ms): {ex}");
            throw;
        }
    }

    private static string? ExtractError(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("error", out var error)) return error.GetString();
        }
        catch
        {
            // not json
        }
        return null;
    }

    public static async Task<HealthResponse?> PingAsync(string baseUrl, bool insecure, CancellationToken ct)
    {
        using var client = new ApiClient { BaseUrl = baseUrl, AllowInsecureTls = insecure };
        return await client.SendAsync<HealthResponse>(HttpMethod.Get, "/api/v1/health", null, ct).ConfigureAwait(false);
    }

    public Task<PairRequestResponse> CreatePairAsync(string deviceName, string fingerprint, CancellationToken ct) =>
        SendAsync<PairRequestResponse>(HttpMethod.Post, "/api/v1/pair/request", new
        {
            device_name = deviceName,
            platform = "windows",
            fingerprint,
            agent_version = AgentInfo.Version
        }, ct)!;

    public Task<PairStatusResponse> PollPairAsync(string requestId, string requestToken, CancellationToken ct) =>
        SendAsync<PairStatusResponse>(HttpMethod.Get,
            $"/api/v1/pair/status?request_id={Uri.EscapeDataString(requestId)}&token={Uri.EscapeDataString(requestToken)}",
            null, ct)!;

    public Task<LinkRequestResponse> CreateLinkRequestAsync(CancellationToken ct) =>
        SendAsync<LinkRequestResponse>(HttpMethod.Post, "/api/v1/agent/link-request", new { }, ct)!;

    public Task<HeartbeatResponse> HeartbeatAsync(bool away, bool locked, MetricsDto metrics, StateDto state, string? name, CancellationToken ct) =>
        SendAsync<HeartbeatResponse>(HttpMethod.Post, "/api/v1/agent/heartbeat", new
        {
            away,
            locked,
            name,
            agent_version = AgentInfo.Version,
            metrics,
            state
        }, ct)!;

    public async Task<List<CommandEnvelope>> GetCommandsAsync(int waitSeconds, CancellationToken ct)
    {
        var result = await SendAsync<CommandList>(HttpMethod.Get, $"/api/v1/agent/commands?wait={waitSeconds}", null, ct)
            .ConfigureAwait(false);
        return result?.Commands ?? new List<CommandEnvelope>();
    }

    public Task PostCommandResultAsync(string id, bool ok, string? error, object? data, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"/api/v1/agent/commands/{id}/result",
            new { ok, error, data }, ct);

    public Task PostEventsAsync(IEnumerable<ActivityEventData> events, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/agent/events",
            new { events = events.Select(e => e.ToWire()).ToArray() }, ct);

    public async Task<AgentEventsResponse> GetEventsAsync(long before, int limit, CancellationToken ct)
    {
        var path = $"/api/v1/agent/events?limit={limit}" + (before > 0 ? $"&before={before}" : "");
        return await SendAsync<AgentEventsResponse>(HttpMethod.Get, path, null, ct).ConfigureAwait(false)
               ?? new AgentEventsResponse();
    }

    public Task PostStateAsync(bool? away, bool? locked, StateDto? state, CancellationToken ct, object? settings = null) =>
        SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/agent/state",
            new { away, locked, state, settings }, ct);

    public Task PostNameAsync(string name, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/agent/state", new { name }, ct);

    public Task PostMessageReplyAsync(string messageId, string? reply, string status, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"/api/v1/agent/messages/{messageId}/reply",
            new { reply, status }, ct);

    public async Task<List<PhoneDto>> GetPhonesAsync(CancellationToken ct)
    {
        var result = await SendAsync<PhoneList>(HttpMethod.Get, "/api/v1/agent/phones", null, ct);
        return result?.Phones ?? new List<PhoneDto>();
    }

    public Task RemovePhoneAsync(string phoneId, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Delete, $"/api/v1/agent/phones/{phoneId}", null, ct);

    public Task RenamePhoneAsync(string phoneId, string name, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Patch, $"/api/v1/agent/phones/{phoneId}", new { name }, ct);

    public Task SetPrimaryPhoneAsync(string phoneId, bool primary, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Patch, $"/api/v1/agent/phones/{phoneId}", new { primary }, ct);

    public Task ConfirmPhoneAsync(string phoneId, string code, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"/api/v1/agent/phones/{phoneId}/confirm", new { code }, ct);

    public Task PurgeScreenshotsAsync(CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/agent/screenshots/purge", new { }, ct);

    public Task PurgeDeviceAsync(CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Delete, "/api/v1/agent/device", null, ct);

    public async Task<ScreenshotUploadResponse> UploadScreenshotAsync(CaptureResult capture, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var size = new FileInfo(capture.Path).Length;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/v1/agent/screenshots"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            using var form = new MultipartFormDataContent();
            var info = new FileInfo(capture.Path);
            var stream = File.OpenRead(capture.Path);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", info.Name);
            form.Add(new StringContent(DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString()), "ts");
            form.Add(new StringContent(capture.Monitor.ToString()), "monitor");
            form.Add(new StringContent(capture.MonitorName), "monitor_name");
            form.Add(new StringContent(capture.Width.ToString()), "width");
            form.Add(new StringContent(capture.Height.ToString()), "height");
            form.Add(new StringContent(capture.Trigger), "trigger");
            request.Content = form;

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Trace?.Invoke($"{(int)response.StatusCode} UPLOAD {capture.MonitorName} ({size / 1024} KB, {watch.ElapsedMilliseconds} ms)");
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) Unauthorized?.Invoke();
            if (!response.IsSuccessStatusCode)
                throw new ApiException((int)response.StatusCode, ExtractError(payload) ?? "upload_failed", payload);
            return JsonSerializer.Deserialize<ScreenshotUploadResponse>(payload, Json) ?? new ScreenshotUploadResponse();
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"FAILED UPLOAD {capture.MonitorName} ({size / 1024} KB, {watch.ElapsedMilliseconds} ms): {ex}");
            throw;
        }
    }

    public void Dispose() => _http.Dispose();
}
