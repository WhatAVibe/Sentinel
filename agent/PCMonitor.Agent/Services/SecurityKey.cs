using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCMonitor.Agent.Services;

public sealed class SecurityKeyLink
{
    public string CredentialId { get; set; } = "";
    public string RpId { get; set; } = SecurityKey.RelyingPartyId;
    public string UserHandle { get; set; } = "";
    public string Label { get; set; } = "Security key";
    public long LinkedAt { get; set; }
}

public static class SecurityKey
{
    public const string RelyingPartyId = "localhost";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public static bool Available => true;

    public static SecurityKeyLink? Current(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ProtectedSecurityKey)) return null;
        try
        {
            return JsonSerializer.Deserialize<SecurityKeyLink>(Secrets.Unprotect(config.ProtectedSecurityKey));
        }
        catch
        {
            return null;
        }
    }

    public static string Store(SecurityKeyLink link) =>
        Secrets.Protect(JsonSerializer.Serialize(link));

    public static async Task<(bool Ok, string? Error)> LinkAsync(AgentHost host)
    {
        var (ok, error, credentialId) = await RunBrowserFlowAsync("register", "").ConfigureAwait(false);
        if (!ok || string.IsNullOrEmpty(credentialId)) return (false, error ?? "no_credential");
        host.Config.ProtectedSecurityKey = Store(new SecurityKeyLink
        {
            CredentialId = credentialId,
            RpId = RelyingPartyId,
            UserHandle = Base64Url(RandomNumberGenerator.GetBytes(32)),
            Label = "Security key",
            LinkedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        host.SaveConfig();
        return (true, null);
    }

    public static async Task<(bool Ok, string? Error)> VerifyAsync(AgentHost host)
    {
        var link = Current(host.Config);
        if (link == null) return (false, "no_key");
        var (ok, error, _) = await RunBrowserFlowAsync("assert", link.CredentialId).ConfigureAwait(false);
        return (ok, error);
    }

    private static async Task<(bool Ok, string? Error, string? CredentialId)> RunBrowserFlowAsync(
        string mode, string credentialId)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var page = BuildPage(mode, credentialId, port);
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource(Timeout);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                    using var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream, cts.Token).ConfigureAwait(false);
                    if (request == null) continue;
                    if (request.Value.Path.StartsWith("/result", StringComparison.OrdinalIgnoreCase) &&
                        request.Value.Body.Length > 0)
                    {
                        await WriteAsync(stream, "application/json", "{\"ok\":true}", cts.Token).ConfigureAwait(false);
                        if (!result.Task.IsCompleted) result.TrySetResult(request.Value.Body);
                        continue;
                    }
                    await WriteAsync(stream, "text/html; charset=utf-8", page, cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // listener closed or timed out
            }
        });

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"http://localhost:{port}/",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            listener.Stop();
            return (false, "browser_failed", null);
        }

        string? body = null;
        try
        {
            body = await result.Task.WaitAsync(Timeout).ConfigureAwait(false);
        }
        catch
        {
            listener.Stop();
            return (false, "timeout", null);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
            var error = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null;
            var id = root.TryGetProperty("credential_id", out var idElement) ? idElement.GetString() : null;
            return (ok, ok ? null : error ?? "failed", id);
        }
        catch
        {
            return (false, "bad_response", null);
        }
    }

    private static string BuildPage(string mode, string credentialId, int port)
    {
        var challenge = Base64Url(RandomNumberGenerator.GetBytes(32));
        var userId = Base64Url(RandomNumberGenerator.GetBytes(32));
        var isRegister = mode == "register";
        return """
<!doctype html>
<html><head><meta charset="utf-8"><title>Sentinel security key</title>
<style>
body{background:#0b0d11;color:#e6e9ef;font-family:'Segoe UI',sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
.card{max-width:520px;padding:36px;border:1px solid #2b3444;border-radius:18px;background:#12161c;text-align:center}
h1{font-size:20px;margin:0 0 8px}p{color:#9aa3b2;font-size:14px;line-height:1.5}
.status{margin-top:18px;font-size:13px;color:#6ee7a8}
.error{color:#ff6b6b}
</style></head><body><div class="card">
<h1>Sentinel security key</h1>
<p>__PROMPT__</p>
<div id="status" class="status">Waiting for your security key...</div>
</div>
<script>
const b64u = new Uint8Array([__CREDENTIALS__]);
const toB64 = (buf) => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
async function run(){
  const status = document.getElementById('status');
  try{
    const challenge = Uint8Array.from(atob('__CHALLENGE__'), c => c.charCodeAt(0));
    if (__MODE_REGISTER__) {
      const cred = await navigator.credentials.create({publicKey:{
        challenge, rp:{id:'localhost', name:'Sentinel'},
        user:{id:Uint8Array.from(atob('__USERID__'),c=>c.charCodeAt(0)), name:'sentinel', displayName:'Sentinel'},
        pubKeyCredParams:[{type:'public-key',alg:-7},{type:'public-key',alg:-257}],
        authenticatorSelection:{userVerification:'preferred'},
        timeout:90000, attestation:'none'
      }});
      await fetch('/result',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({ok:true,credential_id:toB64(cred.rawId)})});
    } else {
      const idBytes = Uint8Array.from(atob('__CREDID__'), c => c.charCodeAt(0));
      const assertion = await navigator.credentials.get({publicKey:{
        challenge, rpId:'localhost',
        allowCredentials:[{type:'public-key',id:idBytes}],
        userVerification:'preferred', timeout:90000
      }});
      await fetch('/result',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({ok:true,credential_id:toB64(assertion.rawId)})});
    }
    status.textContent = 'Done. You can close this tab.';
  }catch(e){
    status.textContent = 'Failed: ' + (e && e.message ? e.message : e);
    status.className = 'status error';
    fetch('/result',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({ok:false,error:(e&&e.name)||'failed'})});
  }
}
run();
</script></body></html>
""".Replace("__PROMPT__", isRegister
                ? "Touch your security key to link it to this PC."
                : "Touch your linked security key to unlock.")
            .Replace("__CHALLENGE__", challenge)
            .Replace("__USERID__", Base64UrlToAtob(userId))
            .Replace("__CREDID__", Base64UrlToAtob(credentialId))
            .Replace("__MODE_REGISTER__", isRegister ? "true" : "false");
    }

    private static string Base64UrlToAtob(string value) => value.Replace('-', '+').Replace('_', '/');

    private static async Task<(string Path, string Body)?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();
        while (!builder.ToString().Contains("\r\n\r\n") && builder.Length < 16384)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) return null;
            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        var text = builder.ToString();
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0) return null;
        var header = text[..headerEnd];
        var body = text[(headerEnd + 4)..];
        var firstLine = header.Split("\r\n")[0].Split(' ');
        var path = firstLine.Length > 1 ? firstLine[1] : "/";
        var length = 0;
        foreach (var line in header.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line.Split(':', 2)[1].Trim(), out length);
        }
        while (body.Length < length)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;
            body += Encoding.UTF8.GetString(buffer, 0, read);
        }
        return (path, body);
    }

    private static async Task WriteAsync(NetworkStream stream, string contentType, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlToBytes(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        switch (text.Length % 4)
        {
            case 2: text += "=="; break;
            case 3: text += "="; break;
        }
        return Convert.FromBase64String(text);
    }
}
