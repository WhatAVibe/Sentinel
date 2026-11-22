# Architecture

## Components

| Component | Stack | Role |
|---|---|---|
| `agent/` | C# .NET 10, WPF, WinForms tray | Runs on the PC. Captures, executes commands, holds the local TOTP verifier. |
| `server/` | Python 3, Flask, SQLite | Relay, pairing authority, TOTP verifier, storage. |
| `mobile/` | Flutter 3.47 (Android/Windows) | Remote control, timeline, gallery, TOTP generator. |
| `flipper/` | C, Flipper Zero SDK (FAP) | BLE keypad: sends commands to the phone and renders the state it returns. |
| ADB (optional) | adb.exe | Direct phone↔PC transport via `adb reverse`. |

The agent and the phone never address each other directly over the internet. Both connect **outbound** to
the relay, so the PC can sit behind NAT with no port forwarding. In direct mode the phone talks to the
agent's loopback API through the adb tunnel.

## Pairing sequence

```
Agent                         Server                          Phone
  │ POST /pair/request           │                               │
  ├─────────────────────────────▶│ creates device + tokens,      │
  │                             │ stores code (TTL 10 min)       │
  │ ◀── request_id, code ────────┤                               │
  │ shows QR {v,u,r,c,n}         │                               │
  │                             │ ◀── POST /pair/approve ────────┤ scans QR / types code
  │                             │     {request_id, code}         │
  │                             ├── mobile_token, totp_secret ───▶│
  │ GET /pair/status (poll)      │                               │
  ├─────────────────────────────▶│                               │
  │ ◀── agent_token, totp ───────┤ (returned exactly once)        │
  │ stores DPAPI-protected       │                               │
```

The phone can also pair with just the six-character code: `POST /pair/lookup` resolves the newest pending
request for that code (rate limited), then `/pair/approve` runs as usual. `POST /agent/link-request`
(agent-authenticated) creates a request for an *existing* device so additional phones can be added without
touching the PC token.

## Command pipeline

1. Phone → `POST /api/v1/devices/{id}/command` with `{type, args?, code?}`.
2. Server validates the command type and, when required, the TOTP code. It inserts a `commands` row and
   pushes it to the agent's WebSocket if connected.
3. Agent long-polls `GET /agent/commands?wait=25` when the socket is down. A long-poll returns as soon as a
   pending row exists (checked every 400 ms), so latency is ~1 s without a socket.
4. Agent executes inside `CommandExecutor` (`AgentHost.ExecuteCommandAsync`) and posts
   `POST /agent/commands/{id}/result`.
5. Pending commands expire after `command_ttl_seconds` (default 120). The agent also discards anything older
   than three minutes or created more than a minute before it started, and reports `expired`. This stops a
   command queued while the PC was asleep or shutting down from running after the next boot.
6. Statuses: `pending → delivered → done|failed`, `timeout` after 90 s without a result, or `expired`. The
   phone receives `{type:"command"}` pushes and can also poll `/commands/{id}`.

Commands that require a code: `unlock`, `away_disable`, and (optionally) `shutdown`/`restart` when the
device setting `protect_power` is on. The agent **re-verifies** the code locally before acting, so a
compromised relay cannot disarm a PC on its own.

## Activity pipeline

The agent's `MonitorService` produces events from:

| Source | Mechanism | Category |
|---|---|---|
| Foreground window | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` | `window` |
| Process launches | 4 s diff of `Process.GetProcesses()` | `process` |
| Mouse clicks / movement | `WH_MOUSE_LL` + 8 s position sampling | `input` |
| Keystrokes (opt-in) | `WH_KEYBOARD_LL`, modifiers + window | `input` |
| Outbound connections | `netstat -ano` diff, public endpoints | `network` |
| Browser searches | window-title heuristics (Google/Bing/DDG/YouTube) | `search` |
| USB attach/detach | WMI `Win32_USBHub` instance events | `usb` |
| Clipboard text | `WM_CLIPBOARDUPDATE` + SHA-256 dedupe | `clipboard` |
| Lock/unlock/logon | `WTSRegisterSessionNotification` | `session` |
| Idle / return | `GetLastInputInfo` transitions | `idle` |
| Messages + replies | phone command / agent reply | `message` |
| Screenshots, away, unlock | Agent actions | `security`, `screenshot` |

Window events include `previous_app` and `previous_seconds`, so app tabbing has dwell context. Events go to a
bounded queue, flush every ~6 s in batches of 100, and mirror to the local UI. `monitorOnlyInAway` limits
capture to Away Mode; session events are always recorded.

## Messages

Phone → `POST /devices/{id}/message` → server stores a `messages` row, queues a `message` command with
`{message_id, title, text, kind, allow_reply}`, pushes ntfy. The agent shows a single styled popup
(toast/dialog/alert/banner) in the bottom-right. If replies are allowed, the PC types an answer which the
agent posts to `/agent/messages/{id}/reply`; the server stores it, emits a `message` event, pushes it to the
phone, and triggers a notification. Dismissals mark the message `dismissed`.

## Power-action policy

`PowerActionMode` in the agent decides what a remote `shutdown`/`restart` does:

- `silent` — execute immediately (`shutdown.exe /t 10`).
- `notify` — show the alert popup with the templated message, then execute.
- `confirm` — show a topmost Allow/Deny dialog with a 30 s auto-deny. Denial returns
  `declined_by_user` to the phone and logs a security event.

`PowerActionMessage` supports `{action}` (e.g. "shut down"). The server mirrors these in device settings so
the phone can display the policy.

## Secure lock

`lock_screen` covers the virtual desktop with `Assets/wallpaper.png`, dims it, and shows a 2FA card on the
primary monitor. The keyboard hook swallows everything except digits/backspace/enter while the overlay owns
the foreground, so Win, Alt+Tab, Alt+F4 and Ctrl+Shift+Esc are blocked. Ctrl+Alt+Delete is the Windows secure
attention sequence and cannot be intercepted by a user-mode app; the overlay re-asserts topmost after the
secure desktop closes. The lock state is persisted to `secure-lock.json`, so a watchdog relaunch restores it.

Unlock paths: the 6-digit code on the overlay, or `unlock_screen` with a code from the phone. An authorized
agent exit clears the lock, so quitting requires the same code. The overlay window refuses normal close
events and a 2-second guard re-raises it.

## Phone management

Phones are stored per device with a stable `uid` sent at pairing, so re-pairing updates the existing row
instead of adding a duplicate. Both the PC (`GET /agent/phones`) and the phone (`GET /devices/{id}/phones`)
can list linked phones with timestamps, and remove them (`DELETE`). Removal is immediate: the old token stops
working. The PC asks for a TOTP code before removing a phone.


## Power and presence alerts

The agent cannot report a hard power-off, so the relay watches heartbeats:

- Every 20 s the relay checks `last_seen`. After `offline_alert_seconds` (default **300 s / 5 min**) with no
  heartbeat it writes a `power` event ("X went offline"), pushes it to phones, and ntfy-notifies. A flag
  prevents repeats until the PC returns.
- On the next heartbeat the relay emits "X is back online" with the downtime and clears the flag.
- Separately, if reported system uptime drops by more than a minute (a reboot) the relay emits "X restarted".
  This also covers quick reboots shorter than the offline threshold.
- The agent reports graceful events directly: `WM_QUERYENDSESSION` ("Shutdown or logoff requested"),
  sleep/resume (`WM_POWERBROADCAST`), and a "PC booted" event when system uptime is under 10 minutes.

Thresholds live in `server/config.json` (`offline_alert_seconds`, `power_check_seconds`, `boot_notify`).
Alerts arrive as normal events, so the phone's local notifications and ntfy both fire.


## Exit authorization and watchdog

Closing the agent is a security-relevant action, so it is gated even when away mode is off:

- Tray → Exit asks for the TOTP code (the phone code or any authenticator) before shutting down.
- Settings → Unpair this PC also asks for the code.
- The phone can send `agent_exit` with a code; the server and agent both verify it.
- On startup the agent spawns a detached supervisor (`Sentinel.exe --supervise`) through the shell, so
  it is not a child of the agent and Task Manager's *End task* does not take it down. If the agent disappears
  without a DPAPI-signed `authorized-exit` marker (30 s validity), the supervisor relaunches it with `--tray`.
  A valid exit writes the marker, which the supervisor consumes before it exits.
- The supervisor exits during Windows shutdown (SM_SHUTTINGDOWN) and won't arm twice (single-instance mutex).
  The legacy `--watchdog <pid>` child mode still exists for older autostart entries.

A local administrator can still kill both processes (no kernel driver is installed), but casual Task Manager
kills or accidental closes no longer disable monitoring. Updates: stop the watchdog first, then the agent, or
use the authorized exit from the phone.

## Away screenshots

Away mode schedules captures at a random interval between `AwayShotMinSeconds` and `AwayShotMaxSeconds`.
When `AwayShotOnActivity` is on, any window change, click, key or mouse move (15 s cooldown) schedules an
extra capture ~3 s later, so there is evidence when someone actually uses the PC. Manual and away captures
upload immediately and land in the phone gallery.

## Screenshots

`ScreenCapture` enumerates monitors (`EnumDisplayMonitors`), BitBlts each monitor rect from the screen DC
into a 32-bit top-down DIB, builds a `BitmapSource`, and encodes PNG. No GDI+ bitmap is used, and the
process is PerMonitorV2 DPI-aware, so multi-monitor captures are pixel-correct.

Flow: capture all monitors → save to `%LOCALAPPDATA%\Sentinel\shots` → show locally → upload each to
`POST /agent/screenshots` (multipart) → server stores `storage/shots/<device>/<id>.png` → pushes metadata
to phones. Phones fetch bytes with their bearer token (or `?token=`). Away mode captures on an interval
(default 120 s).

## Away Mode

```
idle/phone-left/manual/phone command
        │
        ▼
 EnableAway: screensaver starts, monitoring on, away flag, optional lock timer, periodic captures
        │
        ▼
 Disarm (code + optional ADB presence) → verify TOTP locally → stop captures, wake display, monitoring off
```

The flag is persisted to the server (`away`) on every heartbeat and immediately via `/agent/state`.

## Local API (ADB direct mode)

`LocalApiServer` is a minimal HTTP/1.1 server on `127.0.0.1:8790` (TcpListener, not HttpListener, so no URL
ACL/admin requirement). It speaks the same JSON shapes under `/local`, authenticated with a long-lived
local token that is created on first launch and handed out once through a 6-character, 10-minute code.

`adb reverse tcp:8790 tcp:8790` (set up by `AdbService` for every connected device) makes that loopback port
available to the phone as `127.0.0.1:8790`. Presence can be required for disarming; disconnect can also arm
Away Mode automatically.

## Storage

```
%LOCALAPPDATA%\Sentinel\
├─ config.json        non-secret settings
├─ shots\             local captures (PNG)
└─ logs\agent-*.log   JSONL, 10-day rotation

<server>/storage/
├─ sentinel.db        SQLite (WAL)
└─ shots\<device>\    uploaded captures
```

SQLite tables: `devices`, `mobiles`, `pair_requests`, `commands`, `events`, `screenshots`, `audit`.
Retention runs hourly: old events/screenshots/audit rows and their files are deleted according to
`retention_days`.

## Phone storage and multiple PCs

The phone keeps a list of paired PCs in secure storage, each with its own token and TOTP secret, plus the
active device id. Switching PCs attaches a fresh client; local names are phone-only aliases and never leave
the device. Unpairing calls `DELETE /me`, which revokes only that phone, not the PC or other phones.

Activity and screenshots are relay-side data. The app loads the newest page (`200` events, `120` shots) and
requests older pages with `before`; `has_more` tells it whether to keep the button visible. The PC can be
offline the whole time.

## Flipper Zero bridge

The Flipper app (`flipper/`) is a remote keypad, not a network client. It has no relay token and no TOTP
secret; the phone owns both and performs every request.

```
Flipper                      Phone app                       Relay / Agent
  |  BLE serial (bonded)        |                                 |
  |--- LOCK ------------------> | POST /command {type: lock} ---> |
  |<-- STATE|... ---------------| GET /me (every 12 s)            |
```

- Transport: the stock Flipper BLE serial service (GATT), 512-byte frames, newline-delimited ASCII.
- The phone is the central; the Flipper advertises. Pairing uses the Flipper's display-only PIN and the
  standard bond.
- Commands are a closed set (`PING`, `STATUS`, `LOCK`, `SECURE`, `AWAY`, `UNLOCK`, `SHOT`); the phone maps
  them onto relay command types and computes TOTP locally for the ones that need it.
- `STATE` carries the PC name, online/away/secure flags, session, current code and seconds remaining; the
  Flipper only renders it.
- Liveness: the Flipper sends `PING` every 10 s and expects `PONG`; the phone drops the link and
  reconnects after 22 s of silence, even when Android still reports the connection as up.
- The firmware's BT service opens a qFlipper RPC session on every BLE connection, so the app re-registers
  its serial event callback on `BtStatusConnected`. Without that, received bytes go to the RPC protobuf
  parser and TX fails.
- Optional standalone mode replaces the BLE hop with a UART link (115200, GPIO 13/14) to an ESP32 running
  `flipper/esp32/sentinel_bridge.ino`, which then holds the token in NVS.

## Configuration

**Server** — `server/config.json` (auto-created).

**Agent** — Settings tab, stored in `config.json`:

| Key | Default | Meaning |
|---|---|---|
| `AwayScreensaver` | true | Launch the configured `.scr` on arm |
| `LockAfterAwaySeconds` | 0 | Lock workstation N seconds after arming |
| `AutoScreenshotInAway` | true | Random-interval captures |
| `AwayShotMinSeconds` / `AwayShotMaxSeconds` | 45 / 180 | Random capture window |
| `AwayShotOnActivity` | true | Capture shortly after detected use |
| `CaptureMouse` / `CaptureNetwork` / `DetectSearches` | true | Input, connections, searches |
| `CaptureKeystrokes` | false | Opt-in key capture |
| `PowerActionMode` | confirm | silent / notify / confirm |
| `PowerActionMessage` | "Your phone asked to {action} this PC." | Popup/confirm text |
| `MonitorOnlyInAway` | true | Gate window/app/clipboard capture |
| `CaptureClipboard` / `CaptureUsb` / `CaptureProcesses` | true | Event sources |
| `AutoAwayOnIdle` / `AutoAwayIdleMinutes` | false / 10 | Idle auto-arm |
| `AutoAwayOnPhoneDisconnect` | false | Arm when the trusted ADB phone leaves |
| `RequirePhonePresence` | false | ADB presence needed to disarm |
| `ProtectPowerCommands` | false | Require a code for shutdown/restart |
| `AllowInsecureTls` | false | Accept self-signed HTTPS |
| `AdbEnabled`, `AdbPath`, `LocalPort` | false, auto, 8790 | Direct link |
| `StartWithWindows`, `MinimizeToTray`, `BalloonNotifications` | — | Tray behaviour |

## Versioning

Agent 1.6.3 · server 1.3.4 · app 1.6.1 · watch 1.3.5, flipper 1.1 · relay protocol v1.2. Breaking API changes bump the protocol and the
`min_payload`/`min_loader` style checks can be mirrored from the Flopware setup if the suite is extended.
