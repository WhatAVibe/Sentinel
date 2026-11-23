using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

        Add("enc-roundtrip", () =>
        {
            var config = new AppConfig();
            foreach (var kind in new[] { "event", "message", "shot" })
            {
                var plain = RandomNumberGenerator.GetBytes(257);
                var enc = DataKey.Encrypt(config, kind, plain);
                if (!DataKey.Decrypt(config, kind, enc).SequenceEqual(plain)) return false;
            }

            var sample = DataKey.Encrypt(config, "event", Encoding.UTF8.GetBytes("tamper"));
            var tampered = Convert.FromBase64String(sample);
            tampered[^1] ^= 0x01;
            var tamperDetected = false;
            try
            {
                DataKey.Decrypt(config, "event", Convert.ToBase64String(tampered));
            }
            catch (CryptographicException)
            {
                tamperDetected = true;
            }

            var wrongAadDetected = false;
            try
            {
                DataKey.Decrypt(config, "shot", sample);
            }
            catch (CryptographicException)
            {
                wrongAadDetected = true;
            }

            return tamperDetected && wrongAadDetected;
        });

        Add("data-key", () =>
        {
            var config = new AppConfig();
            var first = DataKey.GetOrCreate(config);
            var second = DataKey.GetOrCreate(config);
            var ok = first.Length == 32 && first.SequenceEqual(second);
            ok &= !string.IsNullOrEmpty(config.ProtectedDataKey);
            ok &= config.ProtectedDataKey != Convert.ToBase64String(first);
            ok &= Secrets.Unprotect(config.ProtectedDataKey) == Convert.ToBase64String(first);

            var roundtrip = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config));
            ok &= roundtrip != null && DataKey.GetOrCreate(roundtrip).SequenceEqual(first);

            DataKey.Clear(config);
            ok &= string.IsNullOrEmpty(config.ProtectedDataKey);
            ok &= !DataKey.GetOrCreate(config).SequenceEqual(first);
            return ok;
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
            return metrics.MemTotalMb > 0 && metrics.CpuPercent >= 0 &&
                   !string.IsNullOrWhiteSpace(metrics.User) &&
                   metrics.DiskTotalMb > 0 && metrics.DiskFreeMb >= 0 &&
                   metrics.BatteryPercent >= -1;
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

            var healthResponse = http.GetAsync(baseUrl + "/local/health").GetAwaiter().GetResult();
            ok &= !healthResponse.Headers.Contains("Access-Control-Allow-Origin");

            var lastPairStatus = 0;
            for (var attempt = 0; attempt < 9; attempt++)
            {
                var wrong = new StringContent(JsonSerializer.Serialize(new { code = "ZZZZZZ" }),
                    Encoding.UTF8, "application/json");
                var response = http.PostAsync(baseUrl + "/local/pair", wrong).GetAwaiter().GetResult();
                lastPairStatus = (int)response.StatusCode;
            }
            ok &= lastPairStatus == 429;

            var locked = false;
            for (var attempt = 0; attempt < 10 && !locked; attempt++)
            {
                using var checkRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/local/totp/check")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { code = "000000" }),
                        Encoding.UTF8, "application/json")
                };
                checkRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var response = http.Send(checkRequest);
                locked = (int)response.StatusCode == 429;
            }
            ok &= locked;

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

        Add("master-password", () =>
        {
            var config = new AppConfig();
            MasterPassword.Set(config, "correct horse battery");
            var ok = MasterPassword.Verify(config, "correct horse battery");
            ok &= !MasterPassword.Verify(config, "wrong password");
            ok &= !MasterPassword.NeedsUpgrade(config);

            var salt = RandomNumberGenerator.GetBytes(16);
            var legacy = Rfc2898DeriveBytes.Pbkdf2("legacy password", salt, 120_000,
                HashAlgorithmName.SHA256, 32);
            config.ProtectedMasterPassword = Secrets.Protect(
                $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(legacy)}");
            ok &= MasterPassword.Verify(config, "legacy password");
            ok &= MasterPassword.NeedsUpgrade(config);
            return ok;
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
