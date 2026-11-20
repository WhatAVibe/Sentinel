# Changelog

## 1.1

**Fixed**
- Crash reported as "NULL pointer dereference" when pressing **Back** out of the status screen (most
  reliably right after Connect/Pair). Root cause: the status `TextBox` held a raw pointer into a
  `FuriString` that was reset and rebuilt on every incoming line — a classic dangling-pointer setup once
  the GUI thread redrew the box a moment after the string had moved. See `README.md` → *Design notes* for
  the full writeup.
- Added `app->alive` guards to every callback that can be invoked from the link's worker thread or the BT
  stack (`sentinel_rx_callback`, `sentinel_tick`, `sentinel_custom_event`, `sentinel_navigation_event`), so
  a stray event can no longer reach a view dispatcher that's mid-teardown.
- Added consistent `NULL` checks on every callback's `context` argument.

**Changed**
- Replaced the single "status = log dump" screen with dedicated views:
  - **Status** — a hand-drawn, glanceable panel (name, online/away/secure, session, TOTP code + countdown).
  - **2FA** (menu → **2FA Code**) — just the code: large digits, the remaining seconds counted down every
    second, and a shrinking progress bar. No log text, nothing else.
  - **Log** — its own panel with a header (transport) and its own scroll position (Up/Down). New lines no
    longer move the view; it opens at the top and stays where you leave it.
  - Press **OK** on the Status screen to jump straight to the Log.
- Added an app icon (`icon.png`, 10×10 RGBA) for the Applications list.
- The BLE serial callback is unhooked before the profile is restored on exit, so a late serial event can
  never reach a freed app.
- **WiFi board:** proper `esp32s2` target for the official ESP32-S2-WROVER devboard (UART0 on GPIO43/44),
  USB CDC kept so the board can be flashed and configured without the BOOT+RESET dance, and the command
  protocol mirrored on USB (`SSID:`/`PASS:`/`URL:`/`PAIR:`/`CONNECT`/`STATUS`). Classic ESP32 keeps
  `esp32dev` with UART0 on GPIO1/3. Release builds ship merged 4 MB images for both.

## 1.0

- Initial release: Bluetooth + optional ESP32 WiFi-board transport, lock/secure/away/unlock/screenshot
  actions, TOTP read-out, config persistence, rolling trace log.
