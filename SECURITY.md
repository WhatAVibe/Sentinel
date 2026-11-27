# Security policy

Sentinel is remote-administration software. Treat every deployment as security-sensitive: it can lock a
PC, capture screenshots and (if enabled) record activity.

## Supported versions

Only the latest released versions receive fixes:

| Component | Supported |
|---|---|
| Agent | 2.1.x |
| Relay | 2.0.x |
| Phone app | 2.0.x |
| Watch app | 2.0.x |
| USB key | 0.1.x (agent 2.1.0 bundle) |

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Use GitHub's private vulnerability reporting (**Security → Report a vulnerability**) on this repository.
Please include:

- affected version and component,
- what an attacker gains,
- reproduction steps or a proof of concept,
- any suggested fix.

You can expect an acknowledgement within a few days. This is a personal project, so there is no bug bounty.

## Scope

In scope:

- Authentication or pairing bypass (agent, phone or relay tokens)
- TOTP bypass, replay, or a path where the agent skips local verification
- Secure-lock escape that does not require administrator rights or Ctrl+Alt+Del
- Unauthorized screenshot, event, or message access
- Remote code execution through the relay, API, or agent command handling

Out of scope:

- Killing the agent/supervisor with local administrator rights
- Ctrl+Alt+Del / Windows secure desktop limitations (documented)
- Anything requiring physical access to an unlocked PC
- Self-XSS, social engineering, denial of service by flooding your own relay
- Vulnerabilities in third-party dependencies without a practical exploit path

The USB presence key (`usbkey/`) is deliberately a convenience factor, not a cryptographic authenticator.
A stolen drive, local malware running as the same Windows user, or a spoofed USB identity is outside its
threat model; see [docs/USB-KEY.md](docs/USB-KEY.md). Use the FIDO2 security-key path for real 2FA.

## USB key boundaries

- The master key is 32 random bytes protected with Windows DPAPI `CurrentUser` in
  `%APPDATA%\Sentinel\usbkey.json`. The drive never contains the master key.
- `SENTINEL.KEY` is AES-256-GCM with a random nonce. The AAD binds hardware serial, USB vendor/product/
  revision, volume GUID, volume serial and size. A copied marker fails on another drive; a copied drive
  fails on another Windows account.
- Marker v1 is still accepted on the enrolled PC so an upgrade does not lock you out. Re-enroll to write v2.
- `/local/key/presence` and `/local/key/unlock` require the local bearer token. Unlock is rate-limited and
  requires a key seen within 45 seconds.
- The guard does not mention the USB key in the secure-lock overlay. If the phone already locked the PC, the
  guard reads `/local/status` and leaves the active overlay untouched.
- The guard falls back to `LockWorkStation` if the agent is unreachable or unpaired. It skips locking while
  Windows is shutting down.

## Secure lock notes

- `EnterSecureLock` is idempotent: a second request while active is a no-op, so the phone's message is not
  replaced and no second overlay is created.
- The overlay cannot block Ctrl+Alt+Del; that is a Windows secure-desktop limitation, not a Sentinel bug.
- The 2FA code, recovery password and linked phone remain valid fallbacks when the USB key is lost.

## Release artifacts

The agent, installer, phone APK and USB key binary are unsigned. Windows SmartScreen and Android will warn.
Do not describe a build as signed unless a real signing step was run. Release hashes and the checklist are
in [BUILD.md](BUILD.md); never push a tag or release without the owner's approval.

## Safe harbour

Testing must be limited to your own devices and your own relay. Do not attempt to access other users'
instances. Good-faith research that follows this policy will not be pursued.

## Before you expose a relay

Read [docs/SECURITY.md](docs/SECURITY.md). At minimum: use Tailscale or HTTPS, keep `RequirePhonePresence`
on if you carry the phone, leave keystroke capture off, and set a sensible `retention_days`.
