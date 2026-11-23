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
  relay on hardware you trust, use TLS/Tailscale, and see **Relay data at rest** below.
- Physical access with the PC powered off or booted from another OS.
- Anything you type into a PC that is already compromised before the agent starts.

## Secrets and storage

| Secret | Where | Protection |
|---|---|---|
| Agent token | PC `config.json` | DPAPI (current user), base64 |
| TOTP secret | PC `config.json` | DPAPI |
| Phone token | phone | `flutter_secure_storage` (KeyStore / DPAPI) |
| Phone TOTP secret | phone | `flutter_secure_storage` |
| Data key (DEK) | PC / phone / watch | DPAPI on the PC, `flutter_secure_storage` on the phone, Keystore-encrypted prefs on the watch; delivered only by QR or the phone→watch channel |
| Widget token + TOTP | phone | SharedPreferences encrypted with an Android Keystore AES-GCM key; written only when widgets are configured, cleared on unpair |
| Tokens (server) | SQLite | SHA-256 hashes only |
| Pairing code | server + PC | 6 chars, 10 min TTL, single use |
| Recovery password | PC `config.json` | DPAPI + PBKDF2-SHA256, 600,000 iterations |

The quick-unlock PIN is a convenience lock for the app, stored as salted SHA-256. It is not a substitute for
the 2FA code and does not protect the relay.

## Authentication

- Agent and phone authenticate with 32-byte random bearer tokens (`Authorization: Bearer …`).
- WS clients pass the same token as a query parameter (WebSocket clients cannot set headers reliably).
- **Live pushes are scoped per device.** A phone's socket only receives payloads for the PC it is paired to;
  the relay does not rely on the app filtering by `device_id`.
- Mobile token access to `/devices/<id>/...` is checked against the device that token belongs to on every
  route, including commands, events, screenshots and screenshot files.
- Pairing approvals are rate limited per IP; codes are compared case-insensitively and single-use.
- Every pairing, command, unlock attempt and bad code is appended to the `audit` table.
- **Local direct-mode API** (`127.0.0.1:8790`, only used with ADB): pairing is capped at 8 attempts/minute,
  `unlock` and `totp_check` at 10 requests/minute, and every code check feeds the same lockout as the
  desktop prompts (8 failures in a minute → 60 s). The API sends no CORS headers, so a webpage open on the
  PC cannot call it with `fetch()`.
- The relay sends no CORS headers either; it is a native-client API, not a browser API.

## Second factor

- TOTP, RFC 6238, SHA-1, 6 digits, 30-second step, ±1 step window.
- Verified **twice**: the relay rejects the request, and the agent checks the same code locally before
  disarming. A malicious/compromised relay therefore cannot unlock the PC by itself.
- Rate limits: the relay allows 6 code attempts/minute per device; the agent locks code verification for
  60 seconds after 8 failures in a minute. The lockout is shared by the secure lock, the prompt dialogs,
  relay-issued `unlock`/`away_disable`/`unlock_screen`/`agent_exit` commands and the local direct API, so a
  6-digit code (or the recovery password at the PC) cannot be brute-forced within a TOTP window.
- Works with any authenticator: the app's Security tab, Yubico Authenticator, a Flipper Zero TOTP app,
  etc. The tray dialog accepts the six digits typed manually.
- **The PC never renders or copies the secret or a generated code.** Codes are only displayed on external
  devices (phone, watch, Flipper); the agent stores the secret with DPAPI solely to verify codes locally.
  Writing the code to the PC clipboard is an explicit action from an external device, not a desktop feature.
- Optional ADB presence requirement adds a "something you have nearby" factor for disarming.
- **Physical security key (FIDO2).** Settings → Physical security key links a YubiKey, a Flipper Zero with
  the FIDO app, or any CTAP2 authenticator. The link uses a short-lived local browser page - the same
  WebAuthn path Discord and Edge use, because Windows 11 25H2 removed the old Win32 WebAuthn exports - and
  the browser shows the native Windows security-key prompt. Once linked, local unlock prompts (secure lock,
  exit, unpair, phone removal) accept a key touch instead of the code. The credential id is matched by the
  platform and stored DPAPI-protected in `config.json`. Unlinking is done from Settings and needs an
  unlocked desktop.

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
  outside monitoring. Captured keys are sent as ordinary events (titles such as `Key A`) with **no redaction
  or per-app pause**, so anything typed into a password manager or a browser login form while monitoring is
  recorded in the relay's database. There is no heuristic to detect password fields.
- Message replies and power-confirm decisions are stored in the messages table and event log.
- The `clipboard` command lets a paired phone or watch replace the PC clipboard (used to push the 2FA code
  to Ctrl+V). It does not read the clipboard back; clipboard capture stays opt-in and local. Values written
  by this command are excluded from clipboard capture, so a pushed code is not recorded in the event log.
- Screenshots of the Windows lock screen contain no credentials, but may reveal account names.

## Relay data at rest

The relay stores everything as plaintext in `server/storage/`:

| File | Contents |
|---|---|
| `sentinel.db` → `events` | Window titles, app names, network connections, searches, clipboard text, USB and session events, and keyboard events when capture is on |
| `sentinel.db` → `screenshots` | Screenshot metadata and file paths |
| `sentinel.db` → `messages` | Messages and replies |
| `sentinel.db` → `audit` | Pairing, commands, unlocks and bad-code attempts |
| `shots/*.png` | Full screenshots of every monitor, including anything visible on screen |
| `logs/` | Request log and errors |

There is **no encryption at rest on the relay's disk** (no SQLCipher, no encrypted bucket). The relay stores
user content as ciphertext produced by the clients, but the SQLite file, backups and screenshot folder still
need protecting. Tokens are only stored as SHA-256 hashes and TOTP secrets are present in the `devices` table
because the relay has to verify codes.

As of 3.0.0, events, messages and screenshots are **end-to-end encrypted** with a per-PC 32-byte data key that
is delivered only through a pairing QR or the phone→watch Bluetooth channel. The relay never receives the
key. See [ENCRYPTION.md](ENCRYPTION.md) for the envelope and handshake. Remaining plaintext:

- Routing/alerts metadata: device name, timestamps, event category/severity, message direction/status,
  screenshot dimensions/count/size, command types.
- Relay-generated system events (offline, low disk, low battery, reboot); they contain no user content.
- Any deliberate plaintext device (manual URL+code pairing without a QR key) and any pre-3.0.0 rows you did
  not purge. 3.0.0 has no legacy fallback; purge and re-pair.

The relay also enforces per-device screenshot quotas (500 images / 1 GB by default, oldest deleted), a
manual-capture cap (5 per 30 minutes) and an event ingestion cap, so a compromised or buggy client cannot
fill the disk indefinitely.

Mitigations:

- Run the relay on a host you control and trust, with full-disk encryption and encrypted backups.
- Keep `retention_days` short and purge screenshots you no longer need.
- Leave `CaptureKeystrokes` and clipboard capture off unless the activity log is worth the exposure.
- Give the relay its own user and restrictive permissions on `storage/`; do not run it as root.
- Encrypt before it leaves the PC if the relay belongs to someone else (or self-host).

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

## Away mode and the desktop UI

Away mode is not just a screensaver: while it is active the local UI is not a management surface.

- Enabling away mode (tray, phone, watch, idle, phone-disconnect or a schedule) hides the dashboard.
- Opening Sentinel while away shows only the **Unlock / disable away mode** prompt. The dashboard, Settings,
  pairing and the tray's Open/Settings/Start-with-Windows/Away items stay disabled until the 2FA code (or
  recovery password/security key) is accepted. Tray Exit is also blocked while secure lock is active.
- When away mode ends — including a remote disarm from the phone — the prompt closes on its own. A stale
  "unlock" dialog can no longer sit on top of a PC that is already disarmed.
- Activities record the actor: `Phone · Samsung SM-S948U1` / `Watch · …` for remote actions, or
  `PC · 2FA code`, `PC · recovery password`, `PC · security key`, `PC · direct API` for local ones.
- This is UI gating, not a kernel boundary: a local administrator can still kill the agent (the watchdog
  restarts it) or replace the binary.

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
- [ ] Relay disk and backups encrypted; `retention_days` kept short
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
as security-sensitive. Report issues privately to **admin@whatavibe.net**.
