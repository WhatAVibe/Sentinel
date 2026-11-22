using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Sentinel.Views;

namespace Sentinel.Services;

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

    public static bool Available
    {
        get
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch
            {
                return false;
            }
        }
    }

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

    public static Task<(bool Ok, string? Error)> LinkAsync(AgentHost host)
    {
        return PromptAsync(register: true, credentialId: "")
            .ContinueWith(task =>
            {
                var (ok, error, credentialId) = task.Result;
                if (!ok || string.IsNullOrEmpty(credentialId))
                    return (false, error ?? "no_credential");
                host.Config.ProtectedSecurityKey = Store(new SecurityKeyLink
                {
                    CredentialId = credentialId,
                    RpId = RelyingPartyId,
                    UserHandle = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                    Label = "Security key",
                    LinkedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                host.SaveConfig();
                return (true, (string?)null);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public static Task<(bool Ok, string? Error)> VerifyAsync(AgentHost host)
    {
        var link = Current(host.Config);
        if (link == null) return Task.FromResult<(bool Ok, string? Error)>((false, "no_key"));
        return PromptAsync(register: false, credentialId: link.CredentialId)
            .ContinueWith(task =>
            {
                var (ok, error, _) = task.Result;
                return (ok, error);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static Task<(bool Ok, string? Error, string? CredentialId)> PromptAsync(bool register, string credentialId)
    {
        var app = Application.Current;
        if (app == null) return Task.FromResult<(bool Ok, string? Error, string? CredentialId)>((false, "no_ui", null));
        return app.Dispatcher.InvokeAsync(() =>
        {
            var window = new SecurityKeyWindow(register, credentialId);
            var owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
            if (owner != null && owner is not SecurityKeyWindow) window.Owner = owner;
            window.Topmost = SecureLock.Active;
            window.ShowDialog();
            return window.Outcome;
        }).Task;
    }
}
