# Sentinel Watch (Wear OS)

Standalone Wear OS app: status, Live 2FA code on the home screen, Lock, Secure lock, Away/Disarm, Unlock
screen, Screenshot, Ring, **four tiles** including a configurable all-in-one dashboard, and a
**Sentinel code complication** for the watch face.

## Complication

Long-press the watch face -> customize -> pick a complication slot -> **Sentinel**.

- The complication shows **Sentinel** and opens the app on the live code screen when tapped.
- It deliberately shows no number: Wear OS throttles providers to `UPDATE_PERIOD_SECONDS` (minimum 300 s),
  so a face-mounted TOTP would be stale most of the time. Fresh codes live on the home-screen strip, the
  code tile and the Code screen.
- Supported types: `RANGED_VALUE`, `SHORT_TEXT`, `LONG_TEXT`, so it fits text, icon and arc slots.

## Tiles

After installing, open the watch's tile carousel, scroll to **Add tiles**, and add what you want:

| Tile | What it shows | Buttons |
|---|---|---|
| **Sentinel dashboard** | configurable stack: PC name, status, 2FA code, countdown, action buttons | your selection |
| **Sentinel status** | PC name, online/away/secure state, current 2FA code + seconds | Lock, Unlock |
| **Sentinel actions** | the same action buttons as the dashboard, without status | your selection |
| **Sentinel code** | large 2FA code and refresh countdown | - |

Every tile also has an **Open Sentinel** button that opens the app.

### Dashboard layout

Open **Settings -> Tile layout** to choose what the all-in-one tile stacks:

- PC name
- status (Online / Offline / Away / Secure lock)
- 2FA code
- countdown
- any of the action buttons: Lock, Unlock, Away, Secure, Shot, Ring, Copy

Changes are saved immediately and the tiles are refreshed. Order is fixed: name, status, code,
countdown, then buttons in the order above.

Tiles cannot perform network calls on tap. A button launches the app with an action extra, and the
app performs the command and returns to the carousel, still one tap from the tile. **Copy** pushes the
current 2FA code to the PC clipboard. The 2FA code is computed when the tile is built;
`setFreshnessIntervalMillis(30s)` asks the launcher to refresh, but the launcher decides how often, so
the code can be a few seconds stale. Open the app (or the code tile) for the live code.

## Build

Requirements: JDK 17+, Android SDK 36. Create `local.properties`:

```properties
sdk.dir=C\:\\Users\\<you>\\AppData\\Local\\Android\\Sdk
```

```powershell
.\gradlew.bat :app:assembleDebug
# app\build\outputs\apk\debug\app-debug.apk
```

The project uses AGP 9.1 / Kotlin 2.4 with `android.newDsl=false` and
`android.builtInKotlin=false`, matching the Flutter toolchain.

## Install

On the watch: Developer options -> Wireless debugging -> Pair device with pairing code.

```powershell
adb pair <watch-ip>:<pair-port> <code>
adb connect <watch-ip>:<port>
adb -s <watch-ip>:<port> install -r app\build\outputs\apk\debug\app-debug.apk
```

Keep the watch on the charger with wireless debugging open; Wi-Fi sleeps and the device goes
`offline` otherwise. Use `-s` because the phone is usually connected too.

## Pair

1. PC -> Settings -> Pair another phone (shows a 6-character code).
2. Watch -> enter your relay URL (for example `https://sentinel.example.com`) and the code.
3. The watch shows **Waiting for your PC** with its live 6-digit code and checks the relay every 4 seconds.
   Type that code in the PC prompt (or confirm later in Linked devices); the watch opens the home screen as
   soon as the relay reports the confirmation.
4. The watch appears in Linked phones as "Watch" and reports its app version automatically.

The watch stores its token and TOTP secret in app-private storage. Protect it with the watch's
screen lock; remove it from Linked phones if lost.

## Notes

Tile services must override both `onTileResourcesRequest` and `onResourcesRequest`. Without them the
host throws `UnsupportedOperationException: onTileResourcesRequest not implemented` and renders a
blank tile. Previews come from `@drawable/tile_preview`.
