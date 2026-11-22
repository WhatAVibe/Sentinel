# Security model

Sentinel is a personal remote-administration tool for **your own** computer. Deploy it only on devices you
own and control. It has no stealth features: the tray icon, dashboard, logs and settings are visible to
anyone using the PC.

## What it protects

- Someone at your unlocked PC while you are away: they cannot quietly disable monitoring, because disarming
  needs the TOTP code (and optionally the paired phone on ADB).
- Casual network attackers: tokens are required, pairing is explicit, and screenshot access is token-gated.
- Lost-phone scenarios: *unpair* deletes the server token and the device's access immediately.

## What it does not protect

- A compromised or malicious PC (the agent runs as your user and can be killed by a local admin).
- A compromised relay host. It sees plaintext metadata, events and screenshots. If that matters, host the
  relay on hardware you trust and use TLS/Tailscale.
- Physical access with the PC powered off or booted from another OS.
- Anything you type into a PC that is already compromised before the agent starts.

## Secrets and storage

| Secret | Where | Protection |
|---|---|---|
| Agent token | PC `config.json` | DPAPI (current user), base64 |
| TOTP secret | PC `config.json` | DPAPI |
| Phone token | phone | `flutter_secure_storage` (KeyStore / DPAPI) |
| Phone TOTP secret | phone | `flutter_secure_storage` |
| Tokens (server) | SQLite | SHA-256 hashes only |
| Pairing code | server + PC | 6 chars, 10 min TTL, single use |

The quick-unlock PIN is a convenience lock for the app, stored as salted SHA-256. It is not a substitute for
the 2FA code and does not protect the relay.

## Authentication

- Agent and phone authenticate with 32-byte random bearer tokens (`Authorization: Bearer …`).
- WS clients pass the same token as a query parameter (WebSocket clients cannot set headers reliably).
- Pairing approvals are rate limited per IP; codes are compared case-insensitively and single-use.
- Every pairing, command, unlock attempt and bad code is appended to the `audit` table.

## Second factor

- TOTP, RFC 6238, SHA-1, 6 digits, 30-second step, ±1 step window.
- Verified **twice**: the relay rejects the request, and the agent checks the same code locally before
  disarming. A malicious/compromised relay therefore cannot unlock the PC by itself.
- Rate limits: 6 code attempts/minute per device, then HTTP 429. The mobile app also rate-limits itself.
- Works with any authenticator: the app's Security tab, Yubico Authenticator, a Flipper Zero TOTP app,
  etc. The tray dialog accepts the six digits typed manually.
- Optional ADB presence requirement adds a "something you have nearby" factor for disarming.
- **Physical security key (FIDO2).** Settings → Physical security key links a YubiKey, a Flipper Zero with
  the FIDO app, or any CTAP2 authenticator through the Windows WebAuthn API. Once linked, local unlock
  prompts (secure lock, exit, unpair, phone removal) accept a key touch instead of the code. The assertion
  is matched against the stored credential id by the platform; the credential id is DPAPI-protected in
  `config.json`. Unlinking is done from Settings and needs an unlocked desktop.

## Screenshots, clipboard, privacy

Away mode records the screen and (optionally) clipboard text, mouse/keyboard input and network connections.
Treat `storage/shots` and `storage/logs` as sensitive:

- Restrict server storage permissions to the relay user only.
- Set `retention_days` (default 30) or delete captures from the phone.
- Disable `CaptureClipboard` if password managers may copy secrets while away.
- **Keystroke capture is opt-in** (`CaptureKeystrokes`, default off). When enabled it records printable keys
  with modifiers and the active window, which can include passwords. Leave it off unless you accept that.
  It is also gated by monitoring: with `MonitorOnlyInAway` on (default), keys are only recorded while away
  mode is active. The keyboard hook itself always runs so secure lock can block shortcuts, but it does not log
  outside monitoring.
- Message replies and power-confirm decisions are stored in the messages table and event log.
- The `clipboard` command lets a paired phone or watch replace the PC clipboard (used to push the 2FA code
  to Ctrl+V). It does not read the clipboard back; clipboard capture stays opt-in and local.
- Screenshots of the Windows lock screen contain no credentials, but may reveal account names.

## Transport

HTTP without TLS exposes bearer tokens and screenshot URLs to anyone on the path. Acceptable only on a
trusted LAN or through a VPN. Otherwise:

- Tailscale / WireGuard, or
- HTTPS with a real certificate (Caddy, nginx, Cloudflare Tunnel).

`AllowInsecureTls` exists for self-signed certificates; use it only with a server you control.

## Secure lock

`lock_screen` covers the virtual desktop with `Assets/wallpaper.png` and shows a 2FA card on the primary
monitor. While it is active:

- A low-level keyboard hook swallows every key except digits, backspace, enter and tab, and only while the
  overlay owns the foreground. Win, Alt+Tab, Alt+F4, Ctrl+Esc and Ctrl+Shift+Esc are blocked.
- The overlay is topmost, refuses close events, and a 2-second guard re-raises it after the Windows secure
  desktop closes.
- **Ctrl+Alt+Delete cannot be blocked** by a user-mode app (it is the Windows secure attention sequence).
  Ctrl+Alt+Del opens the Windows security screen; the lock returns when you come back.
- A local administrator can kill the watchdog and agent; the lock is not a kernel-level kiosk.
- The lock state persists (`secure-lock.json`), so a watchdog relaunch restores it. An authorized agent
  exit clears it.

Unlock with the code on the overlay or via `unlock_screen` from the phone. Treat this as strong deterrence,
not a substitute for a Windows account password.

## Linked phones

Each phone stores a stable `uid` and sends it when pairing, so re-pairing replaces the existing record instead
of adding a duplicate. Both sides can list and revoke phones:

- Phone: Settings → Linked phones (shows `THIS PHONE`, linked date, last seen, Remove).
- PC: Settings → Linked phones (Remove asks for a 2FA code).

Revocation is immediate; the removed token fails on its next request.

## Killing and exiting the agent

- Tray → Exit, Settings → Unpair and the remote `agent_exit` command all require a valid TOTP code.
- A detached supervisor (`--supervise`) relaunches the agent if it ends without a DPAPI-signed
  authorized-exit marker. Because it is started in its own process tree, Task Manager's *End task* on the
  agent does not stop it. A valid exit writes the marker, which the supervisor consumes before stopping.
- The supervisor ignores shutdowns (`SM_SHUTTINGDOWN`) and prevents duplicate instances.
- Limits: a local administrator can terminate both processes and delete the marker; this protects against
  casual or accidental disabling, not against someone with full admin control of the PC.

## Remote unlock semantics
Windows does not allow a user-mode process to type credentials into the secure logon desktop. Sentinel's
"unlock" therefore means:

1. verify the code, 2. leave away mode, 3. wake the display, 4. dismiss the screensaver.

If you enabled *lock after N seconds*, you still log in at the keyboard. Sentinel never stores your Windows
password or a credential-provider shim.

## Hardening checklist

- [ ] Relay reachable only through Tailscale or HTTPS
- [ ] `RequirePhonePresence` on if you carry the phone anyway
- [ ] `ProtectPowerCommands` on to stop accidental shutdowns
- [ ] `PowerActionMode` set to `confirm` so remote shutdown/restart needs a click at the PC
- [ ] Keystroke capture off unless explicitly needed
- [ ] `LockAfterAwaySeconds` tuned to how quickly you leave
- [ ] Retention shortened if screenshots are sensitive
- [ ] Phone biometric lock + OS screen lock enabled
- [ ] Regular re-pair after losing a device (unpair revokes every phone for that PC)

## Notifications

The phone shows local notifications while the app is running. Optional ntfy push (`ntfy_url` + `ntfy_topic`
on the relay) sends alerts for away mode, security events, messages and replies. Point it at ntfy.sh or a
self-hosted ntfy instance; treat the topic as a secret (anyone who knows it can subscribe).

## Reporting

This is a personal project; keep a private copy of the repo and treat any change to the pairing or TOTP code
as security-sensitive.
