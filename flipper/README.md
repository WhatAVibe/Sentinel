# Sentinel Bridge — Flipper Zero app

Control a self-hosted **Sentinel** relay from a Flipper Zero: lock the PC, drop the 2FA-gated secure lock,
toggle away mode, grab a screenshot, and read the live TOTP code — all from your pocket.

The Flipper holds **no secrets**. It draws the menu, sends a short command line, and renders whatever
state the phone returns. The phone keeps the relay token and the TOTP secret and does the network work.

| | |
|---|---|
| **App id** | `sentinel_flipper` |
| **Version** | 1.0 |
| **Firmware API** | 87.1 (official firmware, Momentum and derivatives) |
| **Category** | Applications → GPIO → **Sentinel Bridge** |
| **Requires** | Sentinel phone app 1.2.5+ |

---

## Two transports

| Mode | Needs | Works when the phone is away? |
|---|---|---|
| **Bluetooth** (default) | Sentinel phone app in range | No — the phone *is* the network |
| **WiFi board** (optional) | ESP32 devboard + WiFi | Yes — the board talks to the relay |

```
Bluetooth:  Flipper  ──BLE serial──  Sentinel phone app  ──HTTPS──  relay
WiFi board: Flipper  ──UART 13/14──  ESP32 devboard      ──HTTPS──  relay
```

Switch modes from the first menu entry (**Link: Bluetooth** / **Link: WiFi board**). The choice is saved
in `/ext/apps_data/sentinel_flipper/config.txt`.

In Bluetooth mode the WiFi/relay/pair-code entries disappear: there is nothing to configure on the
Flipper, and nothing sensitive stored on it.

---

## Install

### Prebuilt (recommended)

Download `Sentinel-Flipper-1.0.0.fap` from the [release](https://github.com/WhatAVibe/Sentinel/releases)
and copy it to `/ext/apps/GPIO/`, or use qFlipper: **File manager → apps → GPIO → install from file**.

### From source

Requires [`ufbt`](https://pypi.org/project/ufbt/) (`pip install ufbt`) and the Flipper Zero SDK.

```bash
cd flipper
ufbt            # builds dist/sentinel_flipper.fap
ufbt launch     # builds, uploads and starts it over USB
```

`ufbt update && ufbt` pulls the SDK for your firmware if `ufbt` complains about the API version.
qFlipper locks the USB port — close it before `ufbt launch`.

---

## Bluetooth setup

1. **Flipper:** Applications → GPIO → **Sentinel Bridge**. It starts in **Link: Bluetooth** and shows
   *Bluetooth advertising*. Leave the app open; it only answers the phone while it is running.
2. **Phone:** Settings → **Flipper** → **Set up** → **Scan for Flipper**.
3. Pick your Flipper (watch icon, usually `Flipper` or a custom BLE name). Accept the Android pairing
   prompt; if the Flipper shows a six-digit PIN, type it on the phone.
4. The bridge performs a `PING`/`PONG` handshake, then the status screen fills in with the PC name, the
   current 2FA code and the countdown.

Close **qFlipper** while the bridge is in use: the firmware hands the BLE serial profile to qFlipper's RPC
session whenever something connects, and the app claims it back only on its own connection.

### Link handling

- The Flipper sends `PING` every 10 s while connected; the phone answers `PONG`.
- If the phone sees no traffic for 22 s it treats the link as dead even when Android still reports
  "connected", and reconnects on its own (six attempts, 2–8 s apart, re-subscribing each time).
- If the Flipper app is not running, the handshake fails and the phone says
  *"No answer from the Flipper. Open Sentinel Bridge on it and try again."* rather than pretending to work.
- Back from the status screen returns to the menu; back from the menu leaves the app.

---

## Menu

| Entry | Sends | Result |
|---|---|---|
| Link: Bluetooth / WiFi board | — | Switches transport (saved) |
| Connect | `STATUS` | Opens the status screen and asks the phone for state |
| Lock PC | `LOCK` | `lock` |
| Secure Lock | `SECURE` | `lock_screen` with a message |
| Away On/Off | `AWAY` | `away_enable` / `away_disable` (TOTP handled by the phone) |
| Unlock PC | `UNLOCK` | `unlock_screen` when secure-locked, else `unlock` |
| Screenshot | `SHOT` | `screenshot` |
| Refresh Status | `STATUS` | Re-reads state |
| Forget Pairing | `FORGET` | WiFi board mode only |
| Status Log | — | Last INFO/ERR lines |
| Exit | — | Closes the app and releases BLE |

The status screen shows the PC name, `online / away / secure lock / session`, the live TOTP code with its
countdown, and the last few action results.

---

## Line protocol

Newline-terminated ASCII over the Flipper BLE serial profile (or UART at 115200). One command per line,
512 bytes maximum.

**Flipper → phone**

| Line | Meaning |
|---|---|
| `PING` | Keep-alive; the phone answers `PONG` |
| `STATUS` | Send current state |
| `LOCK` / `SECURE` / `SHOT` | `lock` / `lock_screen` / `screenshot` |
| `UNLOCK` / `AWAY` | Unlock (TOTP from the phone) / toggle away mode |

**Phone → Flipper**

| Line | Meaning |
|---|---|
| `PONG` | Answer to `PING` |
| `INFO|<text>` | Human-readable progress, e.g. `sent screenshot` |
| `ERR|<text>` | Failure shown as `! <text>` in the status log |
| `STATE|online|away|secure|session|code|seconds|name` | Full state, pushed every 12 s while connected |

BLE identifiers (stock Flipper serial service, the same one qFlipper uses):

| | UUID |
|---|---|
| Service | `8fe5b3d5-2e7f-4a98-2a48-7acc60fe0000` |
| Flipper → phone (TX, indications) | `19ed82ae-ed21-4c9d-4145-228e61fe0000` |
| Phone → Flipper (RX, write) | `19ed82ae-ed21-4c9d-4145-228e62fe0000` |

---

## Optional WiFi board mode

Only needed if you want the Flipper to work without the phone. The sketch in [`esp32/`](esp32/) is built
for a classic **ESP32 Dev Module**. The official Flipper WiFi Devboard is an **ESP32-S2-WROVER**: it needs
a different PlatformIO board and different UART pins, and is untested here.

Wiring for a classic ESP32:

| Flipper GPIO | ESP32 |
|---|---|
| 13 (TX) | RX (GPIO3) |
| 14 (RX) | TX (GPIO1) |
| GND | GND |

Flash with PlatformIO (`pio run -t upload` from `esp32/`) or the Arduino IDE with ArduinoJson 7. Then on
the Flipper: **Link: WiFi board**, enter WiFi SSID/password, relay URL and a pairing code from
**PC → Settings → Pair another phone**, and use **Pair / Re-pair**.

The ESP32 stores the relay token in NVS and uses `setInsecure()` for HTTPS, so treat it as a trusted
device.

---

## Security

- Bluetooth mode stores **no token and no TOTP secret on the Flipper**. The phone holds both; the Flipper
  is a remote keypad. Someone who steals it cannot reach the relay without your phone.
- The BLE link uses the bonded Flipper serial profile: pairing shows a PIN on the Flipper and requires it
  on the phone.
- WiFi board mode stores the token in the ESP32's NVS. Revoke that phone entry from **Linked phones** if
  the board is lost.
- Sentinel is for devices you own. Ctrl+Alt+Del cannot be blocked by the PC agent and a local
  administrator can stop it — see the [security model](../docs/SECURITY.md).

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Flipper never appears in the phone's scan | Open Sentinel Bridge on the Flipper, keep it on Link: Bluetooth, and close qFlipper. |
| *"No answer from the Flipper"* | The Flipper is running its stock serial profile — open Sentinel Bridge on it and tap Reconnect. |
| Pairing fails | Remove the Flipper bond in Android Bluetooth settings, restart the phone app, pair again. |
| `Bluetooth not connected` on the Flipper | Connect from **Settings → Flipper** on the phone. |
| Status says `No paired PC` | Pair a PC in the phone app first. |
| `Bluetooth serial profile unavailable` | Another app (qFlipper) owns the BLE profile. Close it and reopen Sentinel Bridge. |
| Actions do nothing | Check relay access on the phone; the Flipper shows the phone's `ERR` line verbatim. |
| Drops every so often | The phone reconnects by itself; keep Sentinel Bridge open. |
| WiFi board: `set WiFi SSID first` | Fill in credentials, press **Connect**, then **Pair / Re-pair**. |
| WiFi board: TOTP blank | The ESP32 cannot reach NTP (UDP 123); check the WiFi/firewall. |

A rolling log of the last BLE session is kept at `/ext/apps_data/sentinel_flipper/trace.log`
(rotated at 8 KB, no secrets). It records menu picks, view switches, BLE connect/disconnect, `INFO`/`ERR`
lines and send results — attach it when reporting a bug.

---

## Source layout

```
flipper/
├─ application.fam        FAP manifest (id, version, stack, category)
├─ src/
│  ├─ sentinel_app.c      UI, menu, config, state rendering, lifecycle
│  ├─ sentinel_link.c     BLE serial + UART transports, line framing
│  └─ sentinel_link.h
├─ esp32/                 optional standalone WiFi bridge sketch
└─ dist/                  built .fap
```

Implementation notes:

- The UI is a `ViewDispatcher` with three views (submenu, text input, text box). Back is handled through a
  dispatcher navigation callback that posts a custom event, so no view is switched from inside the input
  callback.
- The RX worker thread only queues bytes and posts an event; all rendering happens on the GUI thread.
- On `BtStatusConnected` the app re-registers its serial event callback. The firmware's BT service opens a
  qFlipper RPC session on every connection and would otherwise consume the phone's writes and the TX
  channel.
