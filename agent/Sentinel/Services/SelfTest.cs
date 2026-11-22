using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Sentinel.Services;

public static class SelfTest
{
    private sealed record Check(string Name, bool Ok, string Detail);

    public static int Run(AgentHost host, string outputPath)
    {
        var checks = new List<Check>();

        void Add(string name, Func<bool> test)
        {
            try
            {
                var ok = test();
                checks.Add(new Check(name, ok, ok ? "ok" : "failed"));
            }
            catch (Exception ex)
            {
                checks.Add(new Check(name, false, ex.Message));
            }
        }

        Add("dpapi", () =>
        {
            var value = "secret-" + Guid.NewGuid().ToString("N");
            var protectedValue = Secrets.Protect(value);
            return protectedValue != value && Secrets.Unprotect(protectedValue) == value;
        });

        Add("totp-rfc6238", () =>
        {
            const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
            return Totp.Compute(secret, 1, 8) == "94287082" &&
                   Totp.Compute(secret, 41152263, 8) == "89005924" &&
                   Totp.Compute(secret, 666666666, 8) == "65353130";
        });

        Add("totp-window", () =>
        {
            var secret = Security.CreateSecret();
            var code = Totp.Now(secret);
            return Totp.Verify(secret, code) && !Totp.Verify(secret, code == "000000" ? "111111" : "000000");
        });

        Add("metrics", () =>
        {
            var metrics = SystemMetrics.Collect();
            return metrics.MemTotalMb > 0 && metrics.CpuPercent >= 0 && !string.IsNullOrWhiteSpace(metrics.User);
        });

        Add("monitors", () =>
        {
            var monitors = ScreenCapture.Monitors();
            return monitors.Count > 0 && monitors.All(m => m.Rect.Width > 0);
        });

        Check capture;
        try
        {
            var outcome = host.CaptureAndUploadAsync("selftest").GetAwaiter().GetResult();
            var batch = outcome.Data as CaptureBatch;
            var first = batch?.Items.FirstOrDefault();
            var fileOk = first != null && File.Exists(first.Path) && new FileInfo(first.Path).Length > 4096;
            capture = new Check("screenshot", outcome.Ok && batch is { Count: > 0 } && fileOk,
                outcome.Ok && first != null
                    ? $"captured={batch!.Count} file={new FileInfo(first.Path).Length / 1024}KB {first.Width}x{first.Height}"
                    : outcome.Error ?? "failed");
        }
        catch (Exception ex)
        {
            capture = new Check("screenshot", false, ex.Message);
        }
        checks.Add(capture);

        Add("local-api", () =>
        {
            host.Local.Start();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var baseUrl = $"http://127.0.0.1:{host.Local.Port}";
            var health = http.GetStringAsync(baseUrl + "/local/health").GetAwaiter().GetResult();
            using var healthDoc = JsonDocument.Parse(health);
            var ok = healthDoc.RootElement.GetProperty("ok").GetBoolean();

            var unauthorized = http.GetAsync(baseUrl + "/local/status").GetAwaiter().GetResult();
            ok &= unauthorized.StatusCode == System.Net.HttpStatusCode.Unauthorized;

            var code = host.EnsureLocalPairCode();
            var pairBody = new StringContent(JsonSerializer.Serialize(new { code, mobile_name = "selftest" }),
                Encoding.UTF8, "application/json");
            var pair = http.PostAsync(baseUrl + "/local/pair", pairBody).GetAwaiter().GetResult();
            var pairText = pair.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            ok &= pair.IsSuccessStatusCode;
            using var pairDoc = JsonDocument.Parse(pairText);
            var token = pairDoc.RootElement.GetProperty("token").GetString() ?? "";

            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/local/status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var status = http.Send(request);
            ok &= status.IsSuccessStatusCode;

            using var commandRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/local/command")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { type = "info" }), Encoding.UTF8, "application/json")
            };
            commandRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var command = http.Send(commandRequest);
            ok &= command.IsSuccessStatusCode;

            using var eventsRequest = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/local/events?since=0");
            eventsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            ok &= http.Send(eventsRequest).IsSuccessStatusCode;

            using var shotsRequest = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/local/screenshots");
            shotsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            ok &= http.Send(shotsRequest).IsSuccessStatusCode;

            return ok;
        });

        Add("config-roundtrip", () =>
        {
            var path = host.Store.ConfigPath;
            var before = File.ReadAllText(path);
            host.SaveConfig();
            var after = File.ReadAllText(path);
            return after.Contains(host.Config.DeviceId) && before.Length > 0;
        });

        Add("adb-resolve", () =>
        {
            var path = host.Adb.ResolveAdb();
            return path != null && File.Exists(path);
        });

        var payload = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            machine = Environment.MachineName,
            config = new { host.Config.DeviceId, host.Config.DeviceName, host.Config.Paired, host.Local.Port },
            checks
        }, new JsonSerializerOptions { WriteIndented = true });

        try
        {
            File.WriteAllText(outputPath, payload);
        }
        catch
        {
            // reported through exit code only
        }

        return checks.All(c => c.Ok) ? 0 : 1;
    }
}

internal static class Security
{
    public static string CreateSecret()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var builder = new StringBuilder(32);
        foreach (var b in bytes) builder.Append(alphabet[b % 32]);
        return builder.ToString();
    }
}
