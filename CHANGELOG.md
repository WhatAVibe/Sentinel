# Changelog

All notable changes to Sentinel. Versions are per component; the installer/product version is what the
release tags use.

## [3.1.0] - 2026-11-27

Installer 3.1.0 · Agent 2.1.0 · Relay 2.0.1 · Phone 2.0.2 (20) · Watch 2.0.1 (12) · Flipper 1.1.0

### Added

- **Sentinel USB key** (`usbkey/`, Rust). Enroll a removable drive, write an AES-256-GCM `SENTINEL.KEY`
  marker bound to the drive's hardware fingerprint, and run a guard that can lock the PC when it is removed
  or release the Sentinel secure lock when it is inserted. The master key is 32 random bytes kept
  DPAPI-protected in `%APPDATA%\Sentinel\usbkey.json`; no secret is written to the drive.
- **Agent USB-key state**: `key_present` / `key_label` in `StateDto`, activity events on insert/remove, and
  authenticated loopback endpoints `POST /local/key/presence` and `POST /local/key/unlock`.
- **Settings card**: Sentinel -> Settings -> Security -> Sentinel USB key, with status, **Manage key** and
  **Refresh** buttons.
- **Installer**: `build.ps1` builds `sentinel-key.exe` with Cargo and packs it next to `Sentinel.exe`; the
  uninstaller removes it.

### Changed

- **Sentinel Key UI restyle.** The Rust window now uses the Sentinel dark palette (`#0B0D11` background,
  `#12161C` cards, `#232A35` borders, `#6C8CFF` accent, `#6EE7A8` success), the Sentinel logo, full-width
  cards, compact status pills and matching 10/18 px radii. Primary, ghost and danger buttons mirror the
  WPF dashboard and installer controls.
- **Stronger drive binding.** Marker format v2 adds vendor/product/revision and the volume GUID to the AAD,
  alongside the hardware serial, volume serial and size. Version 1 markers still verify on the enrolled PC;
  re-enrolling writes v2.
- **Faster presence detection.** The guard now checks the removable drive set every 300 ms using
  `GetLogicalDrives` + `GetDriveTypeW`, and only does full volume identity + marker decryption on a set
  change or every five seconds while a key is present. `sentinel-key probe` reports full-scan timings.
- **Setup inside Sentinel.** Settings -> Security -> Sentinel USB key now has **Set up key**, which opens a
  dark WPF `UsbKeySetupWindow` with drive list, enroll/forget, startup toggle and a link to the full app.
  The Rust CLI is invoked behind the scenes; **Advanced** still opens the full Sentinel Key window.
- **Secure lock is now idempotent.** `EnterSecureLock` returns immediately when an overlay is already active,
  so unplugging the USB key while the phone has secure-locked the PC does not replace the message or stack a
  second lock. The guard reads `/local/status` first and leaves an active lock alone.
- **No USB hint in the lock.** Guard-initiated secure lock now says
  "This PC is locked. Enter your Sentinel code to unlock." It does not name the USB key or suggest looking
  for a drive.
- **Documentation and release preparation.** Added root [BUILD.md](BUILD.md), expanded
  [docs/USB-KEY.md](docs/USB-KEY.md), [SECURITY.md](SECURITY.md), [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md),
  [installer/README.md](installer/README.md), [usbkey/README.md](usbkey/README.md) and
  [usbkey/BUILD.md](usbkey/BUILD.md). The release checklist explicitly requires owner approval before any
  commit push, tag or GitHub release.
- Moved the canonical repository from `C:\Users\lacel\Documents\GitHub\Sentinel` to
  `D:\Projects\Sentinel`. Both trees were verified at commit `f01a64f`, every source file matched, and the
  C: copy was removed.

### Files touched

- New: `usbkey/` (Rust crate), `docs/USB-KEY.md`, `agent/Sentinel/Services/UsbKeyGuard.cs`,
  `agent/Sentinel/Services/UsbKeyTool.cs`, `agent/Sentinel/Views/UsbKeySetupWindow.xaml`,
  `agent/Sentinel/Views/UsbKeySetupWindow.xaml.cs`
- Agent: `Models/ApiModels.cs`, `Services/AgentHost.cs`, `Services/LocalApiServer.cs`, `App.xaml.cs`,
  `Views/SettingsView.xaml`, `Views/SettingsView.xaml.cs`
- Installer: `build.ps1`, `Sentinel.Installer/InstallerCore.cs`,
  `Sentinel.Installer/UninstallWindow.xaml.cs`, `README.md`
- Docs/config: `README.md`, `SECURITY.md`, `CHANGELOG.md`, `BUILD.md`, `docs/ARCHITECTURE.md`,
  `docs/USB-KEY.md`, `docs/LOGGING.md`, `.gitignore`, and the root `../AGENTS.md` project map

### Verification

- `cargo test` in `usbkey/`: 3 passed (marker round trip, tamper rejection, wrong-drive rejection).
- `cargo clippy`: clean.
- `sentinel-key selftest`: DPAPI ok, AES-256-GCM marker 223 bytes on disk, agent connected.
- `sentinel-key` GUI: launched, window title `Sentinel Key`, responding.
- `sentinel-key startup on`: registry value written to
  `"C:\Windows\System32\wscript.exe" "...\sentinel-key-guard.vbs"`; running the VBS produced the expected
  hidden guard attempt in `usbkey.log`.
- `dotnet build Sentinel.sln -c Release`: 0 errors.
- Loopback probe against a live `AgentHost`: `POST /local/key/presence` -> 200, unlock -> 200, wrong id ->
  403, absent -> 200, `SecureLock.Active` false after unlock.
- Real removable drive `E:` (58.6 GB exFAT, disk serial `9C2EE5DD4BD53A0E`, volume `2815975D`): enroll
  wrote a 233-byte `SENTINEL.KEY` (`SNTK` v1), `status` reported `PRESENT`, and `forget` removed both the
  marker and README without touching unrelated files on the drive.
- Legacy v1 marker on that same drive is still recognized as `enrolled` after the v2 identifier change.
- `sentinel-key probe 5` on the mounted drive: 0.5-0.8 ms per full scan, 0.6 ms average, 1 drive, 1 present.
- Sentinel Key UI: dark palette/logo/card layout verified in
  `C:\Users\lacel\AppData\Local\Temp\opencode\sentinel-key-final.png`.
- Sentinel WPF setup window: loaded from the agent assembly and showed `E:` as `ENROLLED`; screenshot at
  `C:\Users\lacel\AppData\Local\Temp\opencode\sentinel-usb-setup2.png`.
- `dotnet build`: 0 errors; installer payload contains the styled `sentinel-key.exe` and the hash matches
  the release binary.
- Secure-lock probe against a live `AgentHost`: first lock kept `"This PC was locked from your phone."`,
  a second `EnterSecureLock` left the message unchanged, and `ExitSecureLock` cleared the state.
- `cargo test`: 5 passed; `cargo clippy`: clean; `probe 5` on the mounted `E:`: 0.5-0.9 ms per full scan.
- Installed binaries were replaced in place with the verified build: `%APPDATA%\Sentinel\Sentinel.exe` hash
  matches the installer payload, and `sentinel-key.exe` hash matches `usbkey\target\release\sentinel-key.exe`.
  The guard is running and the drive now carries a v2 marker (363 bytes, header version 2).

### Release

- Published: **https://github.com/WhatAVibe/Sentinel/releases/tag/v3.1.0**
- Tag `v3.1.0`, commit `937185a`; 10 assets uploaded.
- Live `SHA256SUMS.txt` matches the staged checksum file.
- `latest/download/Sentinel-Setup-3.1.0.exe` and `latest/download/SHA256SUMS.txt` return HTTP 200.

### Open items

- The guard's live insert/remove lock action still needs a physical unplug test while a phone secure lock is
  active. The source-level idempotence, installed binaries and API behavior are verified; the actual screen
  transition is the remaining observation.
- Only the `SENTINEL.KEY` marker is encrypted. The rest of the USB drive is normal storage; use BitLocker To
  Go if the whole drive needs encryption.

## [3.0.3] — 2026-11-24

Installer 3.0.3 · Agent 2.0.3 · Relay 2.0.1 · Phone 2.0.2 (20) · Watch 2.0.1 (12) · Flipper 1.1.0

### Fixed

- **Cancelling pairing no longer leaves you stuck.** The tray now has **Pair this PC** when unpaired, the
  Settings button changes to **Pair this PC** and opens the QR pairing window instead of “Pair this PC
  first.”, and cancelling returns to a usable dashboard instead of a dead end. Opening Sentinel from the
  tray/Start menu while unpaired opens the pairing window.
- **One startup entry.** Only `SentinelAgent` is registered in `HKCU\...\Run`; the supervisor is started by
  the agent, not a second Run value. Upgrades and the agent clean up the legacy `SentinelSupervisor`,
  `Sentinel`, `PCMonitorAgent` and `PCMonitorSupervisor` values, so Task Manager no longer shows two Sentinel
  entries pointing at the same exe.
- **Uninstall removes everything local.** Local data is always wiped (pairing, encryption key, TOTP secret,
  recovery password, logs, screenshots, WebView2 data) and the uninstaller also removes legacy
  `%APPDATA%\PCMonitor` / `%LOCALAPPDATA%\PCMonitor`, `%LOCALAPPDATA%\Sentinel`, the temporary WebAuthn page
  and key log, the Run entries and the uninstall registry key. Relay data is deleted when “Unpair” is checked
  and the relay is reachable.

### Changed

- Agent 2.0.3 and installer 3.0.3; phone, watch and relay unchanged from 3.0.2.

## [3.0.2] — 2026-11-23

Installer 3.0.2 · Agent 2.0.2 · Relay 2.0.1 · Phone 2.0.2 (20) · Watch 2.0.1 (12) · Flipper 1.1.0

### Fixed

- **Away mode now alerts on activity.** Any keyboard/mouse/window activity while away emits a throttled
  `Activity while away` warning (once per minute) with the window/process and the away start time. Before,
  activity only scheduled another silent screenshot, so the phone was never told someone was using the PC.
- **Keystrokes are aggregated, not logged per key.** With capture enabled, the hook now counts keys and
  emits one `Typing activity (N keys)` event per 30 seconds with the active window, instead of one event per
  key. No key names are stored in that event; the history is far less noisy and more private.
- **Automatic away screenshots are silent.** `Screenshot captured` events are no longer created for
  `away`/`activity` captures, and the phone ignores any legacy automatic-shot events, so a locked PC no
  longer spams notifications. Manual and remote captures still notify.
- **Away banner** on the phone home shows `Away mode active`, the reason/since time, counts of captures and
  activity alerts, and explains that only activity triggers notifications.
- **Activity details are tappable.** Tapping any event opens a bottom sheet with the full title, category,
  severity, timestamp and every detail field as selectable text (previously long lines were cut off).
- **Disarm sheet is read-only.** The 6-digit code is displayed, not editable; the action is a single tap.
  Removed the auto-submit-on-edit and the “type from an authenticator” copy.

### Changed

- Agent 2.0.2 and phone 2.0.2; installer 3.0.2. Relay and watch unchanged from 3.0.1.

## [3.0.1] — 2026-11-23

Installer 3.0.1 · Agent 2.0.1 · Relay 2.0.1 · Phone 2.0.1 (19) · Watch 2.0.1 (12) · Flipper 1.1.0

### Fixed

- **Watch pairing actually works.** The watch app now ships under the same applicationId as the phone
  (`com.whatavibe.sentinel`) so the Wear OS Data Layer can deliver `/sentinel/pair`; previously the message
  was sent into the phone’s own app namespace and dropped. Reinstall the watch app (the old
  `com.whatavibe.sentinelwatch` package is removed). Verified end-to-end on the SM-L335U: phone says
  “Watch paired”, the watch opens on the dashboard with the data key and renders the live code.
- **No duplicate “Watch” rows.** `POST /devices/<id>/companions` reuses an existing watch row with the same
  name and deletes older duplicates, instead of adding one per tap. The relay now lists one phone and one
  watch.
- **Secure lock input.** Only digits are accepted and sanitised (including paste). The code auto-submits on
  the 6th digit, but once 7 attempts have failed it requires **Enter** so a stuck keyboard cannot
  auto-submit a lockout. Lockout shows a live countdown and re-enables the field. Ctrl+V is whitelisted in
  the low-level hook (paste), and printable characters are allowed while the overlay owns focus so the
  recovery password can be typed; Win/Alt/Esc/F-keys and Ctrl shortcuts other than A/C/V/X remain blocked.
- **Notifications are readable.** Event details prefer the human `message` field and format as short lines
  instead of `key: value · key: value`; Android uses `BigTextStyle` and up to 900 chars, so the low-disk
  notice is no longer cut off. Added **Hide notification contents** in phone Settings (generic title/body,
  no event text).
- **Version labels.** The phone pairing screen shows `Phone 2.0.1 · watch 2.0.1`, the watch pairing screen
  shows `Watch 2.0.1`, and the PC setup sidebar shows setup/agent/phone/watch versions.
- **Memory hygiene.** Decrypted screenshot bytes are cleared from the in-memory cache when the app goes to
  the background.
- **Relay push text** uses event `detail.message` with newlines for ntfy/webhooks.

### Documented

- README now documents the **public relay** (`https://sentinel.whatavibe.net`), its quotas and limits, the
  pros/cons of trusting someone else’s host, what remains metadata under E2E, and contact
  **admin@whatavibe.net** for questions, comments, concerns or suggestions.

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
