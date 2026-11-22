using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sentinel.Models;

namespace Sentinel.Services;

public sealed class LocalApiServer : IDisposable
{
    private const int MaxBody = 2 * 1024 * 1024;

    private readonly AgentHost _host;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, object> _commands = new();
    private readonly ConcurrentDictionary<string, (int count, DateTime window)> _pairAttempts = new();

    public LocalApiServer(AgentHost host) => _host = host;

    public bool Running { get; private set; }
    public string LastError { get; private set; } = "";
    public int Port => _host.Config.LocalPort;

    public void Start()
    {
        if (Running) return;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _cts = new CancellationTokenSource();
            Running = true;
            LastError = "";
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _host.Log.Write("info", "local api listening", new { port = Port });
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Running = false;
            _host.Log.Write("error", "local api failed", ex.Message);
        }
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                request = request with { Remote = remote };
                var watch = Stopwatch.StartNew();
                var (status, contentType, payload) = await RouteAsync(request, timeout.Token).ConfigureAwait(false);
                _host.Log.Write(
                    status >= 400 ? "warning" : "http",
                    $"local {request.Method} {request.Path} -> {status} ({watch.ElapsedMilliseconds} ms)",
                    new { remote });
                await WriteResponseAsync(stream, status, contentType, payload, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    using var stream = client.GetStream();
                    await WriteResponseAsync(stream, 500, "application/json",
                        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = "internal", detail = ex.Message }, ApiClient.Json)),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // client gone
                }
            }
        }
    }

    private async Task<(int status, string contentType, byte[] payload)> RouteAsync(HttpRequest request, CancellationToken ct)
    {
        var path = request.Path;
        var method = request.Method;

        if (method == "OPTIONS") return (204, "text/plain", Array.Empty<byte>());

        if (method == "GET" && path == "/local/health")
        {
            return Json(new
            {
                ok = true,
                name = "Sentinel Agent",
                version = AgentInfo.Version,
                device_name = _host.Config.DeviceName,
                adb = _host.Adb.LastKnownTrustedPresent,
                port = Port
            });
        }

        if (method == "POST" && path == "/local/pair")
        {
            var body = ParseJson(request.Body);
            var code = GetString(body, "code");
            var key = request.Remote;
            var attempt = _pairAttempts.GetOrAdd(key, _ => (0, DateTime.UtcNow));
            if (DateTime.UtcNow - attempt.window > TimeSpan.FromMinutes(1)) attempt = (0, DateTime.UtcNow);
            attempt.count++;
            _pairAttempts[key] = attempt;
            if (attempt.count > 8) return Json(new { error = "rate_limited" }, 429);

            if (!_host.ValidateLocalPairCode(code)) return Json(new { error = "bad_code" }, 403);
            _host.InvalidateLocalPairCode();
            _pairAttempts.TryRemove(key, out _);
            _host.Log.Write("info", "direct mode paired", new { remote = request.Remote });
            return Json(new
            {
                token = _host.Config.LocalToken,
                device_id = _host.Config.DeviceId,
                device_name = _host.Config.DeviceName,
                totp_secret = _host.Config.TotpSecret,
                name = "Sentinel Agent",
                version = AgentInfo.Version
            });
        }

        var token = ExtractToken(request);
        if (!TokenMatches(token)) return Json(new { error = "invalid_token" }, 401);

        switch (method, path)
        {
            case ("GET", "/local/status"):
                return Json(new { device = _host.BuildDeviceSnapshot() });

            case ("GET", "/local/events"):
            {
                long since = long.TryParse(QueryValue(request, "since"), out var s) ? s : 0;
                var limit = int.TryParse(QueryValue(request, "limit"), out var l) ? Math.Clamp(l, 1, 500) : 200;
                var events = _host.RecentEvents(since, limit).Select(e => new
                {
                    id = e.Id,
                    ts = e.Data.Ts,
                    category = e.Data.Category,
                    severity = e.Data.Severity,
                    title = e.Data.Title,
                    detail = e.Data.Detail
                }).ToArray();
                return Json(new { events });
            }

            case ("GET", "/local/screenshots"):
            {
                var shots = UiState.I.SnapshotShots().Take(200).Select(shot => new
                {
                    id = System.IO.Path.GetFileNameWithoutExtension(shot.Path),
                    ts = new DateTimeOffset(shot.CapturedAt).ToUnixTimeMilliseconds(),
                    monitor = shot.Monitor,
                    monitor_name = shot.MonitorName,
                    width = shot.Width,
                    height = shot.Height,
                    trigger = shot.Trigger,
                    url = $"/local/screenshots/{System.IO.Path.GetFileNameWithoutExtension(shot.Path)}/file"
                }).ToArray();
                return Json(new { screenshots = shots });
            }

            case ("POST", "/local/command"):
            {
                var body = ParseJson(request.Body);
                var type = GetString(body, "type");
                if (string.IsNullOrWhiteSpace(type)) return Json(new { error = "type_required" }, 400);
                var code = GetString(body, "code");
                var args = ExtractArgs(body);
                if (!string.IsNullOrEmpty(code)) args["code"] = JsonSerializer.SerializeToElement(code);
                var envelope = new CommandEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = type,
                    Args = args
                };
                var outcome = await _host.ExecuteCommandAsync(envelope, "direct").ConfigureAwait(false);
                var commandPayload = new
                {
                    id = envelope.Id,
                    type,
                    args,
                    status = outcome.Ok ? "done" : "failed",
                    created_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    completed_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    result = new { ok = outcome.Ok, error = outcome.Error, data = outcome.Data }
                };
                _commands[envelope.Id] = commandPayload;
                return Json(new { ok = outcome.Ok, error = outcome.Error, command = commandPayload },
                    outcome.Ok ? 200 : 400);
            }

            case ("POST", "/local/unlock"):
            {
                var body = ParseJson(request.Body);
                var code = GetString(body, "code");
                var action = GetString(body, "action");
                if (action is not ("unlock" or "away_disable" or "")) return Json(new { error = "bad_action" }, 400);
                if (!_host.VerifyCode(code)) return Json(new { error = "bad_code" }, 403);
                if (!_host.CanDisarm(out var why)) return Json(new { error = "not_allowed", detail = why }, 403);
                var envelope = new CommandEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = string.IsNullOrEmpty(action) ? "unlock" : action,
                    Args = new() { ["code"] = JsonSerializer.SerializeToElement(code) }
                };
                var outcome = await _host.ExecuteCommandAsync(envelope, "direct").ConfigureAwait(false);
                return Json(new
                {
                    ok = outcome.Ok,
                    error = outcome.Error,
                    command = new
                    {
                        id = envelope.Id,
                        type = envelope.Type,
                        status = outcome.Ok ? "done" : "failed",
                        result = new { ok = outcome.Ok, error = outcome.Error, data = outcome.Data }
                    }
                }, outcome.Ok ? 200 : 403);
            }

            case ("POST", "/local/totp/check"):
            {
                var body = ParseJson(request.Body);
                var ok = _host.VerifyCode(GetString(body, "code"));
                return Json(new { ok, seconds_remaining = Totp.SecondsRemaining() });
            }
        }

        if (method == "GET" && path.StartsWith("/local/screenshots/") && path.EndsWith("/file"))
        {
            var id = path["/local/screenshots/".Length..^"/file".Length];
            var match = UiState.I.SnapshotShots()
                .FirstOrDefault(s => string.Equals(System.IO.Path.GetFileNameWithoutExtension(s.Path), id, StringComparison.OrdinalIgnoreCase));
            if (match == null || !System.IO.File.Exists(match.Path)) return Json(new { error = "not_found" }, 404);
            try
            {
                var bytes = await System.IO.File.ReadAllBytesAsync(match.Path, ct).ConfigureAwait(false);
                return (200, "image/png", bytes);
            }
            catch
            {
                return Json(new { error = "unreadable" }, 500);
            }
        }

        if (method == "GET" && path.StartsWith("/local/commands/"))
        {
            var id = path["/local/commands/".Length..];
            return _commands.TryGetValue(id, out var command)
                ? Json(new { command })
                : Json(new { error = "not_found" }, 404);
        }

        return Json(new { error = "not_found" }, 404);
    }

    private static (int, string, byte[]) Json(object payload, int status = 200) =>
        (status, "application/json", JsonSerializer.SerializeToUtf8Bytes(payload, ApiClient.Json));

    private bool TokenMatches(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var expected = _host.Config.LocalToken;
        if (string.IsNullOrEmpty(expected)) return false;
        var a = Encoding.UTF8.GetBytes(token);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string ExtractToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue("authorization", out var header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header[7..].Trim();
        var query = QueryValue(request, "token");
        return query ?? "";
    }

    private static string? QueryValue(HttpRequest request, string key)
    {
        if (request.Query.TryGetValue(key, out var value)) return value;
        return null;
    }

    private static JsonElement ParseJson(byte[] body)
    {
        if (body.Length == 0) return default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch
        {
            return default;
        }
    }

    private static string GetString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
        return "";
    }

    private static Dictionary<string, JsonElement> ExtractArgs(JsonElement body)
    {
        var args = new Dictionary<string, JsonElement>();
        if (body.ValueKind == JsonValueKind.Object &&
            body.TryGetProperty("args", out var element) &&
            element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject()) args[property.Name] = property.Value.Clone();
        }
        return args;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string contentType, byte[] payload, CancellationToken ct)
    {
        var reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            429 => "Too Many Requests",
            _ => "Internal Server Error"
        };
        var header = $"HTTP/1.1 {status} {reason}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {payload.Length}\r\n" +
                     "Connection: close\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
                     "Access-Control-Allow-Headers: Authorization, Content-Type\r\n" +
                     "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                     "Cache-Control: no-store\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
        if (payload.Length > 0) await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private sealed record HttpRequest(
        string Method,
        string Path,
        Dictionary<string, string> Query,
        Dictionary<string, string> Headers,
        byte[] Body,
        string Remote);

    private static async Task<HttpRequest> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read <= 0) throw new IOException("connection closed");
            buffer.Write(chunk, 0, read);
            headerEnd = FindHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
            if (buffer.Length > 128 * 1024) throw new IOException("headers too large");
        }

        var raw = buffer.ToArray();
        var headerText = Encoding.UTF8.GetString(raw, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) throw new IOException("bad request line");
        var method = requestLine[0].ToUpperInvariant();
        var target = requestLine[1];
        var queryIndex = target.IndexOf('?');
        var path = queryIndex >= 0 ? target[..queryIndex] : target;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (queryIndex >= 0)
        {
            foreach (var pair in target[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) query[Uri.UnescapeDataString(pair)] = "";
                else query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        headers.TryGetValue("content-length", out var lengthValue);
        var contentLength = int.TryParse(lengthValue, out var parsed) ? Math.Clamp(parsed, 0, MaxBody) : 0;
        var body = new byte[contentLength];
        var bodyStart = headerEnd + 4;
        var available = raw.Length - bodyStart;
        var copied = Math.Min(Math.Max(available, 0), contentLength);
        if (copied > 0) Array.Copy(raw, bodyStart, body, 0, copied);
        var offset = copied;
        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), ct).ConfigureAwait(false);
            if (read <= 0) break;
            offset += read;
        }

        var remote = "unknown";
        return new HttpRequest(method, path, query, headers, body, remote);
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') return i;
        }
        return -1;
    }
}
