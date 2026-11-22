using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Sentinel.Services;

namespace Sentinel.Views;

public partial class SecurityKeyWindow : Window
{
    private readonly bool _register;
    private readonly string _credentialId;
    private (bool Ok, string? Error, string? CredentialId) _outcome = (false, "cancelled", null);

    public (bool Ok, string? Error, string? CredentialId) Outcome => _outcome;

    public SecurityKeyWindow(bool register, string credentialId)
    {
        InitializeComponent();
        _register = register;
        _credentialId = credentialId;
        Loaded += OnLoaded;
    }

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "sentinel-key.log"),
                $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // diagnostics only
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Trace("window loaded");
        try
        {
            var data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Sentinel",
                "WebView2");
            Directory.CreateDirectory(data);
            Trace("creating environment");
            var environment = await CoreWebView2Environment.CreateAsync(null, data);
            Trace("environment ready, ensuring core");
            await Web.EnsureCoreWebView2Async(environment);
            Trace("core ready");
            Web.CoreWebView2.WebMessageReceived += OnMessage;

            var page = BuildPage();
            var port = StartServer(page);
            Trace("navigating to localhost:" + port);
            Web.Source = new Uri($"http://localhost:{port}/");
        }
        catch (Exception ex)
        {
            Trace("failed: " + ex);
            _outcome = (false, "webview2_unavailable: " + ex.Message, null);
            Close();
        }
    }

    private System.Net.Sockets.TcpListener? _listener;

    private int StartServer(string page)
    {
        _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(page);
            while (_listener != null)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    using var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (read <= 0) continue;
                    var request = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                    var head = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                    var headBytes = System.Text.Encoding.ASCII.GetBytes(head);
                    await stream.WriteAsync(headBytes).ConfigureAwait(false);
                    await stream.WriteAsync(bytes).ConfigureAwait(false);
                }
                catch
                {
                    // listener stopped or client vanished
                }
            }
        });
        return port;
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
        base.OnClosed(e);
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        Trace("message: " + e.WebMessageAsJson);
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
            var error = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null;
            var credential = root.TryGetProperty("credential_id", out var idElement) ? idElement.GetString() : null;
            _outcome = (ok, ok ? null : error ?? "failed", credential);
        }
        catch
        {
            _outcome = (false, "bad_response", null);
        }
        Close();
    }

    private string BuildPage()
    {
        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var userId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var credential = _register ? "" : Convert.ToBase64String(Base64UrlToBytes(_credentialId));
        return """
<!doctype html>
<html><head><meta charset="utf-8"><title>Sentinel security key</title>
<style>
body{margin:0;background:#0b0d11;color:#e6e9ef;font-family:'Segoe UI',sans-serif;display:flex;align-items:center;justify-content:center;height:100vh}
.card{max-width:460px;padding:30px;border:1px solid #2b3444;border-radius:18px;background:#12161c;text-align:center}
h1{font-size:19px;margin:0 0 8px}p{color:#9aa3b2;font-size:13px;line-height:1.5}
.status{margin-top:16px;font-size:12px;color:#6ee7a8}
.error{color:#ff6b6b}
button{margin-top:18px;background:#171c24;color:#e6e9ef;border:1px solid #2a3340;border-radius:8px;padding:10px 18px;cursor:pointer;font-size:14px}
button:disabled{opacity:.5}
</style></head><body><div class="card">
<h1>Sentinel security key</h1>
<p>__PROMPT__</p>
<p style="font-size:11px">Windows owns the key. Click the button, then touch the key when the system prompt appears.</p>
<button id="go" autofocus onclick="run()">Use security key</button>
<div id="status" class="status">Ready.</div>
</div>
<script>
const post = (data) => window.chrome.webview.postMessage(data);
const toB64u = (buf) => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
async function run(){
  document.getElementById('go').disabled = true;
  const status = document.getElementById('status');
  status.className = 'status';
  status.textContent = 'Waiting for the Windows security prompt...';
  try{
    const challenge = Uint8Array.from(atob('__CHALLENGE__'), c => c.charCodeAt(0));
    if (__REGISTER__) {
      const created = await navigator.credentials.create({publicKey:{
        challenge,
        rp:{id:'localhost', name:'Sentinel'},
        user:{id:Uint8Array.from(atob('__USERID__'),c=>c.charCodeAt(0)), name:'Sentinel', displayName:'Sentinel'},
        pubKeyCredParams:[{type:'public-key',alg:-7},{type:'public-key',alg:-257}],
        authenticatorSelection:{userVerification:'preferred'},
        timeout:120000,
        attestation:'none'
      }});
      post({ok:true,credential_id:toB64u(created.rawId)});
      status.textContent = 'Key linked. You can close this window.';
    } else {
      const id = Uint8Array.from(atob('__CREDENTIAL__'), c => c.charCodeAt(0));
      const assertion = await navigator.credentials.get({publicKey:{
        challenge, rpId:'localhost',
        allowCredentials:[{type:'public-key',id:id}],
        userVerification:'preferred',
        timeout:120000
      }});
      post({ok:true,credential_id:toB64u(assertion.rawId)});
      status.textContent = 'Key accepted.';
    }
  }catch(e){
    status.textContent = 'Failed: ' + (e && e.message ? e.message : e);
    status.className = 'status error';
    post({ok:false,error:(e&&e.name)||'failed'});
  }
}
</script></body></html>
""".Replace("__PROMPT__", _register
                ? "Touch your security key to link it to Sentinel."
                : "Touch your linked security key to unlock Sentinel.")
            .Replace("__CHALLENGE__", challenge)
            .Replace("__USERID__", userId)
            .Replace("__CREDENTIAL__", credential)
            .Replace("__REGISTER__", _register ? "true" : "false");
    }

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
