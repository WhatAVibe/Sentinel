# Changelog

All notable changes to Sentinel. Versions are per component; the installer/product version is what the
release tags use.

## [3.0.0] — 2026-11-23

Installer 3.0.0 · Agent 2.0.0 · Relay 2.0.0 · Phone 2.0.0 (18) · Watch 2.0.0 (11) · Flipper 1.1.0

**Breaking:** no legacy plaintext mode. Existing devices must be unlinked and paired again; old encryption-free
rows do not decrypt.

### Added

- **End-to-end encryption on by default.** The agent generates a 32-byte data key per PC (DPAPI) and
  encrypts event payloads, messages/replies and screenshots with AES-256-GCM (`sentinel:v2:event|message|shot`
  AAD). The relay stores `enc` blobs, clears plaintext columns and stores encrypted screenshots as `.enc`
  files. Phones decrypt events, messages and screenshot bytes in memory; nothing plaintext is written to
  disc/cache.
- **QR key delivery.** The pairing QR is v2 and carries `k` (the data key). The key never transits the relay.
  Manual URL + 6-character pairing is refused for relay sessions because it cannot deliver a key safely.
- **Phone-mediated watch pairing.** Settings → **Pair a Wear OS watch** calls the new relay companion endpoint
  (`POST /devices/<id>/companions`), then sends URL, token, TOTP secret and data key over the Wear OS Data
  Layer (`/sentinel/pair`). The watch saves the session and key and switches to its home screen; no typing on
  the watch. Manual watch pairing still works for commands but has no key.
- **Encrypted-mode enforcement:** `enc_required` on devices; the relay rejects mixed/plaintext writes with
  `400 enc_required`, including agent events, replies, phone messages and screenshots.
- **Encryption status** in phone Settings and on the watch; key state is never displayed or logged.

### Changed

- The relay interface now includes `enc` fields and `enc_required`; `docs/API.md` and `docs/ENCRYPTION.md`
  document the contract.
- Messages sent from the phone are encrypted before leaving the device; the agent decrypts them for display.
- Existing screenshot quotas apply to ciphertext (500 images / 1 GB, 5 manual captures per 30 min, event
  cap), so encrypted storage cannot be flooded.

### Upgrade

Unpair the PC, phone and watch (or purge the relay), install 3.0.0 everywhere, then pair by scanning the PC's
QR and add the watch from the phone. 3.0.0 intentionally has no plaintext fallback.

## [2.1.3] — 2026-11-22

Installer 2.1.3 · Agent 1.6.3 · Relay 1.3.4 · Phone 1.6.1 (17) · Watch 1.3.5 (10) · Flipper 1.1.0

### Added

- **Android home-screen widgets**: a status widget (PC name, Online/Away/Secure lock/Offline, live code,
  countdown), a code-only widget, and a quick-actions widget (Lock, Secure lock, Away, Screenshot). The code
  widget refreshes every 30 seconds; tapping any widget opens Sentinel. Widgets are added from
  **Settings → Widgets & Quick Settings** or from the launcher.
- **Quick Settings tile**: “Lock <PC>” performs a secure lock (or opens the app when actions are locked).
- **Widget options**: “Allow actions without unlocking” (off by default — buttons open the app and its
  PIN/biometrics) and “Show the 2FA code in widgets”. The widget token and TOTP secret are stored in
  Android SharedPreferences encrypted with an Android Keystore AES-GCM key and cleared on unpair.
- **Clear actor reporting**: unlocking or disarming records who did it — `Phone · <name>` / `Watch · <name>`
  for remote commands, `PC · 2FA code`, `PC · recovery password`, `PC · security key` or `PC · direct API`
  for the desktop/local API.

### Security

- **The desktop UI is locked down while away mode or secure lock is active.** Enabling away mode hides the
  dashboard; attempting to open Sentinel now shows only the **Unlock / disable away mode** dialog. Secure
  lock blocks the dashboard, settings, pairing and tray Exit, and the tray's Open/Settings/Start-with-Windows
  and Away items are disabled. The disarm dialog closes itself when away mode ends (for example from the
  phone), so it can no longer linger after a remote disarm. Verified with a scripted run against the live
  agent: dashboard hidden on away, only the code prompt on open, dialog gone after remote disarm, only the
  overlay while secure-locked.

### Changed

- Phone 1.6.1 and agent 1.6.3; installer 2.1.3. Relay and watch builds are unchanged from 2.1.2 and still
  included in the release.

## [2.1.2] — 2026-11-22

Installer 2.1.2 · Agent 1.6.2 · Relay 1.3.4 · Phone 1.6.0 (16) · Watch 1.3.5 (10) · Flipper 1.1.0

### Added

- **Docker packaging**: `Dockerfile`, `docker-compose.yml` and `.dockerignore` for the relay, with a
  persistent `sentinel-data` volume and a healthcheck.
- **Backup tooling**: `server/tools/backup.py` takes a WAL-safe SQLite online backup plus config, screenshots
  and a manifest, with `--keep-days` retention. Documented cron/systemd and Docker restore steps.
- **History export**: `GET /api/v1/devices/<id>/export` returns events and audit as JSON, or events/audit as
  CSV (UTC timestamps). The phone exposes **Settings → Export** with share-sheet delivery.
- **Disk and battery metrics**: the agent reports disk used/total/free and battery percentage/charging. The
  phone shows a Disk bar and a Battery chip, and the relay fires one-shot low-disk and low-battery events
  using the existing power-alert pattern (`disk_alert_percent`, `battery_alert_percent`, both configurable,
  `0` disables).
- **Silent install**: `Sentinel Setup.exe --silent --relay <url> --recovery <password> --report <path>` with
  `--confirm-code`, `--confirm-file` or `--auto-confirm`, plus `--dir` and `--no-startup`. Writes a JSON
  report (pair code, status, errors) for fleet scripts. `--auto-confirm` is documented as the explicit
  trade-off it is.
- **Live watch complications**: `Complications.kt` now uses the same `loadSnapshot()` as the tiles instead
  of placeholders — the ranged complication is a real 30-second TOTP countdown ring, short/long text show
  Online / Away / Secure lock / Offline and the current code.

### Changed

- Relay hot-path queries are indexed: `idx_devices_token` and `idx_mobiles_token` on `token_hash`, used by
  every authenticated request and both WebSocket handshakes.
- README, SETUP, server README, watch README, mobile README and the changelog cover the new Docker, backup,
  export, metrics and complication behaviour.

## [2.1.1] — 2026-11-22

Installer 2.1.1 · Agent 1.6.1 · Relay 1.3.3 · Phone 1.5.1 (15) · Watch 1.3.4 (9) · Flipper 1.1.0

### Security

- **Local direct-mode API now actually rate limits.** Pairing attempts are keyed by loopback IP instead of
  IP:port (every request used a new port, so the old counter never tripped), stale entries are pruned, and
  `/local/pair` is capped at 8 attempts/minute.
- **`/local/unlock` and `/local/totp/check` are capped** at 10 requests/minute per loopback IP. Pairing,
  unlock and code checks go through `AgentHost.CheckCode`, which locks out for 60 seconds after 8 failed
  codes in a minute — shared by the desktop prompt, the local API and relay-issued commands, so a 6-digit
  code or recovery password cannot be brute-forced in one TOTP window.
- **Relay-delivered `unlock`, `away_disable`, `unlock_screen` and `agent_exit` now use the same lockout**
  (`VerifyTotp`) instead of calling `Totp.Verify` directly. The recovery password still only works locally.
- **Removed wildcard CORS from the local agent API** (`Access-Control-Allow-Origin: *`) and from the relay.
  Neither is a browser API; this stops a webpage from probing `127.0.0.1:8790` or the relay with `fetch()`.
- **PBKDF2-SHA256 for the recovery password raised to 600,000 iterations** (OWASP guidance). Existing
  hashes store their iteration count; legacy 120,000-iteration hashes verify once and are rehashed
  automatically on the next successful unlock.
- **Relay websocket fan-out is now per device.** Phones/watch were registered on one flat client list, so
  `push_to_device_clients` leaked screenshots, keystroke/clipboard events, state and replies from every PC
  on the relay to every connected phone. Clients are now keyed by device id like agents already were.
- **Ownership checks added** to `GET /devices/<id>/commands`, `/events`, `/screenshots`,
  `/screenshots/<sid>/file` and `DELETE /screenshots/<sid>`: a mobile token can no longer read, download or
  delete another device's data (IDOR).
- The relay rate limiter now prunes stale keys and documents that its counters are per process; running
  multiple workers would multiply the limits until the counters move to Redis.

### Changed

- Installer and agent metadata 2.1.1 / 1.6.1, relay 1.3.3. Phone and watch builds are unchanged from 2.1.0
  and are still included in the release.

### Documented

- `docs/SECURITY.md` now spells out exactly what the relay stores in plaintext, that SQLite and the
  screenshot folder have no at-rest encryption, and what the local API lockout and per-device fan-out mean
  for shared relays. Hardening guidance recommends full-disk encryption on the relay host.

## [2.1.0] — 2026-11-22

Installer 2.1.0 · Agent 1.6.0 · Relay 1.3.2 · Phone 1.5.1 (15) · Watch 1.3.4 (9) · Flipper 1.1.0

### Added

- **Windows installer**: self-contained setup with terms, install folder, recovery password, relay address,
  QR/6-digit pairing, background install with progress, tray autostart, and **Update / Repair** vs
  **Uninstall** when an install exists. The installed agent is the uninstaller, so the setup file is not
  kept on disk.
- **Automatic device confirmation**: the agent watches the relay while paired and opens a 6-digit prompt as
  soon as a phone or watch approves — even if no pairing window is open. Pairing windows swap the QR for the
  same prompt and close themselves once confirmed. Nothing on the new device works before that.
- **Waiting screens** on the phone and watch: the new device shows its live code and polls the relay,
  then enters the app automatically when the PC confirms.
- **Physical security key (PSK)** unlock through an embedded WebView2 page on `http://localhost:<port>`,
  which triggers the native Windows passkey prompt named **Sentinel**. Works with YubiKeys and a Flipper
  Zero running ZeroFIDO.
- **Notification filters** on the phone for security, power, messages, screenshots, clipboard and everything
  else, persisted across launches.
- **Per-device app versions**: the phone and watch send `X-App-Version`; the relay stores it at pairing time
  and on every request, and the PC shows it (with an OUTDATED badge) in Linked devices.
- **Device rename lock**: names changed in Linked devices are kept, even though the apps keep reporting
  their model name.
- Multi-PC support: one relay serves several PCs, and one phone can pair with all of them.
- Every uninstall/unpair logs out every linked device (`unpaired` push) and invalidates their tokens.
- Tutorials have **Back** on desktop and mobile; **Skip** is remembered forever.
- Recovery password is required during setup and can only be changed (current + new twice). There is no
  clear option; a forgotten password means uninstall/reinstall.
- Complete purge actions: screenshots, all device data, and per-device removal with audit entries.

### Changed

- Rebranded from PCMonitor to **Sentinel**: data under `%APPDATA%\Sentinel`, Android packages
  `com.whatavibe.sentinel` and `com.whatavibe.sentinelwatch`, relay service `sentinel.service`.
- Secure lock is a compact card: 6-digit code + Unlock, with an **Other options** expander for the recovery
  password and the security key.
- Relay 1.3.2 records versions and device kinds, keeps custom names, and returns `pending_confirmation`
  instead of a generic error while a device is unconfirmed.
- Agent payload is compressed into a single file, shrinking the installer from ~195 MB to ~126 MB.
- Builds are unsigned; publisher metadata is `WhatAVibe`.

### Fixed

- Mobile showed "can't verify with server" after a successful pairing; it now understands the pending state.
- Watch handled only 401; pending and unpaired states are now handled too.
- Screenshot dimensions reported as 0×0; live CPU now refreshes every 2 s; activity history is relay-backed.
- Mojibake from the rename (em dashes, middle dots, arrows) in the UI and docs.
- Recovery password flow no longer allows clearing, and verifies the current password.
- URL and token blurring in screenshots, sticky "test connection" status, unpaired lockout states.

### Removed

- `scripts/` and `branding/` helpers and the signing pipeline (`docs/SIGNING.md`). Builds stay unsigned.

### Upgrading from 1.x

The package IDs changed from `com.pcmonitor.*` to `com.whatavibe.*`. Uninstall the old phone/watch apps (the
icon and name are the same, so check the version in About) before installing 1.5.1 / 1.3.4. Re-run the new
setup and choose **Update / Repair** to keep your PC pairing and settings.

## [1.4.1] — 2026-11-21

Agent 1.3.1, phone 1.3.0, watch 1.3.2. Secure lock polish, dashboard 2FA code and tray improvements.

## [1.4.0] — 2026-11-21

Agent 1.3.0, phone 1.3.0, watch 1.3.2. First build with the QR pairing and the notification pipeline.
