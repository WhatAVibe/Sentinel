using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    public const string RelyingPartyId = "sentinel.local";
    public const string Origin = "https://sentinel.local";
    private const int TimeoutMs = 60_000;
    private const int UserVerificationPreferred = 1;
    private const int AuthenticatorAttachmentAny = 0;
    private const int AttestationNone = 0;
    private const int CoseAlgorithmEs256 = -7;

    public static bool Available
    {
        get
        {
            try { return WebAuthnGetApiVersionNumber() > 0; }
            catch { return false; }
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

    public static async Task<(bool Ok, string? Error)> LinkAsync(AgentHost host)
    {
        var output = Path.Combine(Path.GetTempPath(), $"sentinel-key-{Guid.NewGuid():N}.json");
        var (ok, error, credentialId) = await RunHelperAsync("register", "", output).ConfigureAwait(false);
        try
        {
            if (!ok || string.IsNullOrEmpty(credentialId))
                return (false, error ?? "no_credential");

            var link = new SecurityKeyLink
            {
                CredentialId = credentialId,
                RpId = RelyingPartyId,
                UserHandle = Base64Url(RandomNumberGenerator.GetBytes(32)),
                Label = "Security key",
                LinkedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            host.Config.ProtectedSecurityKey = Store(link);
            host.SaveConfig();
            return (true, null);
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
        }
    }

    public static Task<(bool Ok, string? Error)> VerifyAsync(AgentHost host)
    {
        var link = Current(host.Config);
        if (link == null) return Task.FromResult<(bool Ok, string? Error)>((false, "no_key"));
        return VerifyAsync(link.CredentialId);
    }

    private static async Task<(bool Ok, string? Error)> VerifyAsync(string credentialId)
    {
        var output = Path.Combine(Path.GetTempPath(), $"sentinel-key-{Guid.NewGuid():N}.json");
        try
        {
            var (ok, error, _) = await RunHelperAsync("assert", credentialId, output).ConfigureAwait(false);
            return (ok, error);
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
        }
    }

    private static async Task<(bool Ok, string? Error, string? CredentialId)> RunHelperAsync(
        string mode, string argument, string output)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return (false, "no_process", null);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--security-key {mode} \"{argument.Replace("\"", "")}\" \"{output}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) return (false, "no_process", null);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs + 15_000));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return (false, "timeout", null);
            }

            if (!File.Exists(output)) return (false, "no_response", null);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output).ConfigureAwait(false));
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
            var error = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null;
            var credential = root.TryGetProperty("credential_id", out var credentialElement)
                ? credentialElement.GetString()
                : null;
            return (ok, error, credential);
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name, null);
        }
    }

    // ------------------------------------------------------------------ helper process

    public static int RunHelper(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "";
        var argument = args.Length > 1 ? args[1] : "";
        var output = args.Length > 2 ? args[2] : "";
        try
        {
            if (mode == "register")
            {
                var (ok, error, credentialId) = MakeCredential();
                WriteResult(output, ok, error, credentialId);
                return ok ? 0 : 1;
            }
            if (mode == "assert")
            {
                var (ok, error, credentialId) = GetAssertion(argument);
                WriteResult(output, ok, error, credentialId);
                return ok ? 0 : 1;
            }
            WriteResult(output, false, "bad_mode", null);
            return 2;
        }
        catch (Exception ex)
        {
            WriteResult(output, false, ex.GetType().Name, null);
            return 3;
        }
    }

    private static void WriteResult(string path, bool ok, string? error, string? credentialId)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { ok, error, credential_id = credentialId }));
        }
        catch
        {
            // the parent treats a missing file as a failure
        }
    }

    private static (bool Ok, string? Error, string? CredentialId) MakeCredential()
    {
        if (!Available) return (false, "api_unavailable", null);

        var challenge = RandomNumberGenerator.GetBytes(32);
        var userHandle = RandomNumberGenerator.GetBytes(32);
        var clientDataJson = BuildClientData("webauthn.create", challenge);
        var clientDataBytes = Encoding.UTF8.GetBytes(clientDataJson);

        var rpIdPtr = Marshal.StringToCoTaskMemUni(RelyingPartyId);
        var rpNamePtr = Marshal.StringToCoTaskMemUni("Sentinel");
        var rpIconPtr = IntPtr.Zero;
        var userNamePtr = Marshal.StringToCoTaskMemUni($"{Environment.UserName}@sentinel.local");
        var displayNamePtr = Marshal.StringToCoTaskMemUni(Environment.MachineName);
        var credentialTypePtr = Marshal.StringToCoTaskMemUni("public-key");
        var hashAlgPtr = Marshal.StringToCoTaskMemUni("SHA-256");
        var userHandlePtr = Marshal.AllocHGlobal(userHandle.Length);
        var clientDataPtr = Marshal.AllocHGlobal(clientDataBytes.Length);
        Marshal.Copy(userHandle, 0, userHandlePtr, userHandle.Length);
        Marshal.Copy(clientDataBytes, 0, clientDataPtr, clientDataBytes.Length);

        try
        {
            var rp = new RpEntity
            {
                dwVersion = 1,
                pwszId = rpIdPtr,
                pwszName = rpNamePtr,
                pwszIcon = rpIconPtr
            };
            var user = new UserEntity
            {
                dwVersion = 1,
                cbId = (uint)userHandle.Length,
                pbId = userHandlePtr,
                pwszName = userNamePtr,
                pwszIcon = IntPtr.Zero,
                pwszDisplayName = displayNamePtr
            };
            var parameter = new CoseCredentialParameter
            {
                dwVersion = 1,
                pwszCredentialType = credentialTypePtr,
                lAlg = CoseAlgorithmEs256
            };
            var parameterPtr = Marshal.AllocHGlobal(Marshal.SizeOf<CoseCredentialParameter>());
            Marshal.StructureToPtr(parameter, parameterPtr, false);
            var parameters = new CoseCredentialParameters
            {
                cCredentialParameters = 1,
                pCredentialParameters = parameterPtr
            };
            var clientData = new ClientData
            {
                dwVersion = 1,
                cbClientDataJSON = (uint)clientDataBytes.Length,
                pbClientDataJSON = clientDataPtr,
                pwszHashAlgId = hashAlgPtr
            };
            var options = new MakeCredentialOptions
            {
                dwVersion = 1,
                dwTimeoutMilliseconds = TimeoutMs,
                CredentialList = default,
                Extensions = default,
                dwAuthenticatorAttachment = AuthenticatorAttachmentAny,
                bRequireResidentKey = 0,
                dwUserVerificationRequirement = UserVerificationPreferred,
                dwAttestationConveyancePreference = AttestationNone,
                dwFlags = 0,
                pwszCredentialCreationOptions = IntPtr.Zero
            };

            var hr = WebAuthnMakeCredential(ref rp, ref user, ref parameters, ref clientData, ref options,
                IntPtr.Zero, out var attestationPtr);
            Marshal.FreeHGlobal(parameterPtr);
            if (hr != 0)
            {
                return (false, HResult(hr), null);
            }
            try
            {
                var attestation = Marshal.PtrToStructure<CredentialAttestation>(attestationPtr);
                if (attestation.cbCredentialId == 0 || attestation.pbCredentialId == IntPtr.Zero)
                    return (false, "empty_credential", null);
                var bytes = new byte[attestation.cbCredentialId];
                Marshal.Copy(attestation.pbCredentialId, bytes, 0, bytes.Length);
                return (true, null, Base64Url(bytes));
            }
            finally
            {
                WebAuthnFreeCredentialAttestation(attestationPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(userHandlePtr);
            Marshal.FreeHGlobal(clientDataPtr);
            Marshal.FreeCoTaskMem(rpIdPtr);
            Marshal.FreeCoTaskMem(rpNamePtr);
            Marshal.FreeCoTaskMem(userNamePtr);
            Marshal.FreeCoTaskMem(displayNamePtr);
            Marshal.FreeCoTaskMem(credentialTypePtr);
            Marshal.FreeCoTaskMem(hashAlgPtr);
        }
    }

    private static (bool Ok, string? Error, string? CredentialId) GetAssertion(string credentialId)
    {
        if (!Available) return (false, "api_unavailable", null);
        byte[] expected;
        try { expected = Base64UrlToBytes(credentialId); }
        catch { return (false, "bad_credential", null); }
        if (expected.Length == 0) return (false, "bad_credential", null);

        var challenge = RandomNumberGenerator.GetBytes(32);
        var clientDataJson = BuildClientData("webauthn.get", challenge);
        var clientDataBytes = Encoding.UTF8.GetBytes(clientDataJson);
        var clientDataPtr = Marshal.AllocHGlobal(clientDataBytes.Length);
        Marshal.Copy(clientDataBytes, 0, clientDataPtr, clientDataBytes.Length);
        var hashAlgPtr = Marshal.StringToCoTaskMemUni("SHA-256");
        var typePtr = Marshal.StringToCoTaskMemUni("public-key");
        var idPtr = Marshal.AllocHGlobal(expected.Length);
        Marshal.Copy(expected, 0, idPtr, expected.Length);
        var credentialPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Credential>());
        var credentialArrayPtr = Marshal.AllocHGlobal(IntPtr.Size);

        try
        {
            var credential = new Credential
            {
                dwVersion = 1,
                cbId = (uint)expected.Length,
                pbId = idPtr,
                pwszCredentialType = typePtr
            };
            Marshal.StructureToPtr(credential, credentialPtr, false);
            Marshal.WriteIntPtr(credentialArrayPtr, 0, credentialPtr);

            var options = new GetAssertionOptions
            {
                dwVersion = 1,
                dwTimeoutMilliseconds = TimeoutMs,
                CredentialList = new CredentialList { cCredentials = 1, ppCredentials = credentialArrayPtr },
                Extensions = default,
                dwAuthenticatorAttachment = AuthenticatorAttachmentAny,
                dwUserVerificationRequirement = UserVerificationPreferred,
                dwFlags = 0,
                pwszU2fAppId = IntPtr.Zero,
                pbU2fAppId = IntPtr.Zero,
                pwszCredentialCreationOptions = IntPtr.Zero,
                pCancellationId = IntPtr.Zero
            };
            var clientData = new ClientData
            {
                dwVersion = 1,
                cbClientDataJSON = (uint)clientDataBytes.Length,
                pbClientDataJSON = clientDataPtr,
                pwszHashAlgId = hashAlgPtr
            };

            var hr = WebAuthnAuthenticate(ref clientData, ref options, IntPtr.Zero, out var assertionPtr);
            if (hr != 0) return (false, HResult(hr), null);
            try
            {
                var assertion = Marshal.PtrToStructure<Assertion>(assertionPtr);
                if (assertion.Credential.cbId == 0 || assertion.Credential.pbId == IntPtr.Zero)
                    return (false, "empty_assertion", null);
                var received = new byte[assertion.Credential.cbId];
                Marshal.Copy(assertion.Credential.pbId, received, 0, received.Length);
                if (!CryptographicOperations.FixedTimeEquals(received, expected))
                    return (false, "credential_mismatch", null);
                return (true, null, Base64Url(received));
            }
            finally
            {
                WebAuthnFreeAssertion(assertionPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(clientDataPtr);
            Marshal.FreeHGlobal(idPtr);
            Marshal.FreeHGlobal(credentialPtr);
            Marshal.FreeHGlobal(credentialArrayPtr);
            Marshal.FreeCoTaskMem(hashAlgPtr);
            Marshal.FreeCoTaskMem(typePtr);
        }
    }

    private static string BuildClientData(string type, byte[] challenge) =>
        $"{{\"type\":\"{type}\",\"challenge\":\"{Base64Url(challenge)}\",\"origin\":\"{Origin}\",\"crossOrigin\":false}}";

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

    private static string HResult(int hr) => hr switch
    {
        unchecked((int)0x800704C7) => "cancelled",
        unchecked((int)0x80070005) => "access_denied",
        unchecked((int)0x80070490) => "not_found",
        _ => $"webauthn_0x{hr:X8}"
    };

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct RpEntity
    {
        public uint dwVersion;
        public IntPtr pwszId;
        public IntPtr pwszName;
        public IntPtr pwszIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserEntity
    {
        public uint dwVersion;
        public uint cbId;
        public IntPtr pbId;
        public IntPtr pwszName;
        public IntPtr pwszIcon;
        public IntPtr pwszDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CoseCredentialParameter
    {
        public uint dwVersion;
        public IntPtr pwszCredentialType;
        public int lAlg;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CoseCredentialParameters
    {
        public uint cCredentialParameters;
        public IntPtr pCredentialParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientData
    {
        public uint dwVersion;
        public uint cbClientDataJSON;
        public IntPtr pbClientDataJSON;
        public IntPtr pwszHashAlgId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint dwVersion;
        public uint cbId;
        public IntPtr pbId;
        public IntPtr pwszCredentialType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CredentialList
    {
        public uint cCredentials;
        public IntPtr ppCredentials;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Extensions
    {
        public uint cExtensions;
        public IntPtr pExtensions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MakeCredentialOptions
    {
        public uint dwVersion;
        public uint dwTimeoutMilliseconds;
        public CredentialList CredentialList;
        public Extensions Extensions;
        public uint dwAuthenticatorAttachment;
        public int bRequireResidentKey;
        public uint dwUserVerificationRequirement;
        public uint dwAttestationConveyancePreference;
        public uint dwFlags;
        public IntPtr pwszCredentialCreationOptions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GetAssertionOptions
    {
        public uint dwVersion;
        public uint dwTimeoutMilliseconds;
        public CredentialList CredentialList;
        public Extensions Extensions;
        public uint dwAuthenticatorAttachment;
        public uint dwUserVerificationRequirement;
        public uint dwFlags;
        public IntPtr pwszU2fAppId;
        public IntPtr pbU2fAppId;
        public IntPtr pwszCredentialCreationOptions;
        public IntPtr pCancellationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CredentialAttestation
    {
        public uint dwVersion;
        public IntPtr pwszFormatType;
        public uint cbAuthenticatorData;
        public IntPtr pbAuthenticatorData;
        public uint cbAttestationObject;
        public IntPtr pbAttestationObject;
        public uint cbCredentialId;
        public IntPtr pbCredentialId;
        public Extensions Extensions;
        public int lStatusCode;
        public IntPtr pwszStatusText;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Assertion
    {
        public uint dwVersion;
        public uint cbAuthenticatorData;
        public IntPtr pbAuthenticatorData;
        public uint cbSignature;
        public IntPtr pbSignature;
        public Credential Credential;
        public uint cbUserId;
        public IntPtr pbUserId;
    }

    [DllImport("webauthn.dll")]
    private static extern int WebAuthnGetApiVersionNumber();

    [DllImport("webauthn.dll", CharSet = CharSet.Unicode)]
    private static extern int WebAuthnMakeCredential(
        ref RpEntity pRpInformation,
        ref UserEntity pUserInformation,
        ref CoseCredentialParameters pPubKeyCredParams,
        ref ClientData pWebAuthnClientData,
        ref MakeCredentialOptions pWebAuthnMakeCredentialOptions,
        IntPtr pClsId,
        out IntPtr ppWebAuthnCredentialAttestation);

    [DllImport("webauthn.dll", CharSet = CharSet.Unicode)]
    private static extern int WebAuthnAuthenticate(
        ref ClientData pWebAuthnClientData,
        ref GetAssertionOptions pWebAuthnGetAssertionOptions,
        IntPtr pClsId,
        out IntPtr ppWebAuthnAssertion);

    [DllImport("webauthn.dll")]
    private static extern void WebAuthnFreeCredentialAttestation(IntPtr pWebAuthnCredentialAttestation);

    [DllImport("webauthn.dll")]
    private static extern void WebAuthnFreeAssertion(IntPtr pWebAuthnAssertion);
}
