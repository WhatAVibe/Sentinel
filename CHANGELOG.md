# Changelog

All notable changes to Sentinel. Versions are per component; the installer/product version is what the
release tags use.

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
