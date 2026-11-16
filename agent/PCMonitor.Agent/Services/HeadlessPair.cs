using System.IO;
using System.Text.Json;

namespace PCMonitor.Agent.Services;

public static class HeadlessPair
{
    public static async Task<int> RunAsync(AgentHost host, string serverUrl, string reportPath, bool insecure)
    {
        try
        {
            var api = new ApiClient { BaseUrl = serverUrl, AllowInsecureTls = insecure };
            var health = await ApiClient.PingAsync(serverUrl, insecure, CancellationToken.None);
            if (health == null) return 2;

            var request = await api.CreatePairAsync(host.Config.DeviceName, host.Config.DeviceId, CancellationToken.None);
            var report = new
            {
                server = serverUrl,
                request_id = request.RequestId,
                pair_code = request.PairCode,
                request_token = request.RequestToken,
                expires_at = request.ExpiresAt
            };
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            var deadline = DateTimeOffset.UtcNow.AddSeconds(300);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(1500);
                var status = await api.PollPairAsync(request.RequestId, request.RequestToken, CancellationToken.None);
                if (status.Status == "approved" && !string.IsNullOrEmpty(status.AgentToken))
                {
                    host.Config.ServerUrl = serverUrl;
                    host.Config.AllowInsecureTls = insecure;
                    host.SaveConfig();
                    host.ApplyPairing(status.DeviceId ?? "", status.AgentToken, status.TotpSecret ?? "");
                    File.WriteAllText(reportPath + ".done", "paired");
                    return 0;
                }
                if (status.Status is "expired" or "denied") return 3;
            }
            return 4;
        }
        catch (Exception ex)
        {
            File.WriteAllText(reportPath + ".error", ex.ToString());
            return 1;
        }
    }
}
