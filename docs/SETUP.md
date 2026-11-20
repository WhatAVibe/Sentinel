# Setup guide

## 1. Relay server

### Windows (home server / always-on PC)

```powershell
cd server
python -m pip install -r requirements.txt
python server.py
```

To run it as a service, use [NSSM](https://nssm.cc/):

```powershell
nssm install SentinelRelay "C:\Python313\python.exe" "C:\PCMonitor\server\server.py"
nssm set SentinelRelay AppDirectory "C:\PCMonitor\server"
nssm start SentinelRelay
```

Or register a Task Scheduler job at logon with **Run whether user is logged on or not**.

### Linux

```bash
sudo apt install python3-venv -y
cd /opt/pcmonitor/server
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
```

`/etc/systemd/system/sentinel.service`:

```ini
[Unit]
Description=Sentinel relay
After=network-online.target

[Service]
WorkingDirectory=/opt/pcmonitor/server
ExecStart=/opt/pcmonitor/server/.venv/bin/python /opt/pcmonitor/server/server.py
Restart=always
RestartSec=5
User=sentinel
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
```

```bash
sudo useradd -r -s /bin/false sentinel
sudo chown -R sentinel /opt/pcmonitor/server
sudo systemctl enable --now sentinel
```

### Reachability

The PC and phone must reach the relay. Options, easiest first:

**Tailscale** (recommended)

```powershell
tailscale up
```

Use the machine's `100.x.y.z` address: `http://100.64.1.20:8765`. Traffic is WireGuard-encrypted, no ports
are exposed. Set `"public_url": "http://100.64.1.20:8765"`.

**Reverse proxy with real TLS** (Caddy)

```caddy
sentinel.example.com {
    reverse_proxy 127.0.0.1:8765
}
```

`"public_url": "https://sentinel.example.com"`. Caddy handles WebSockets automatically. For nginx add:

```nginx
proxy_http_version 1.1;
proxy_set_header Upgrade $http_upgrade;
proxy_set_header Connection "upgrade";
proxy_read_timeout 300s;
```

**ngrok / Cloudflare Tunnel**

```powershell
ngrok http 8765
```

Copy the HTTPS URL into `public_url`.

**Plain LAN** — allow TCP 8765 through Windows Firewall; use `http://<lan-ip>:8765`.
Do **not** forward that port on your router without TLS.

## 2. Agent

Build (VS 2026 Release x64 or CLI):

```powershell
cd agent
.\build.bat Release
```

Start `agent\PCMonitor.Agent\bin\Release\net10.0-windows\PCMonitor.Agent.exe`.

- Enter the relay URL, press **Connect**, and pair with the phone.
- **Autostart:** Settings → *Start with Windows* (writes `HKCU\...\Run` with `--tray`).
- Closing the window minimizes to tray; exit from the tray menu.
- The agent does not need admin rights for lock/screenshot/monitoring. `shutdown` works as the current user.

Agent data: `%LOCALAPPDATA%\PCMonitor`.

To move the PC to a new server later: Settings → edit the address → Save & reconnect. If the tokens were
wiped server-side, use *Unpair this PC* and pair again.

## 3. Phone app

```powershell
cd mobile
.\flutter.ps1 pub get
.\flutter.ps1 build apk --release
adb install -r build\app\outputs\flutter-apk\app-release.apk
```

Debug on a device/emulator:

```powershell
.\flutter.ps1 run
```

Release signing: follow Flutter's standard `android/key.properties` setup; until then release APKs are
signed with the debug key (fine for personal use, not for stores).

Windows test build:

```powershell
.\flutter.ps1 run -d windows
```

Note: `flutter.ps1` exists because the Flutter tool rejects paths containing `!`. It creates a junction
at `C:\Users\<you>\pcm-link` and runs Flutter there. Only needed if the checkout path contains `!`; a
normal path such as `Documents\GitHub\Sentinel` can use `flutter` directly.

## 4. Pairing

1. PC: pairing window → server URL → **Connect**. A QR code and 6-character code appear.
2. Phone: **Scan QR**, or **Code** → enter the server URL and the code.
3. On approval the PC flips to **Live** and the phone opens the dashboard.
4. Extra phone: PC *Settings → Pair another phone*.

## 5. ADB direct link (optional)

On the phone:

1. Settings → About phone → tap Build number 7× → Developer options.
2. Enable **USB debugging**; connect USB; accept the RSA prompt.
   *Or* enable **Wireless debugging** and note the `ip:port` + pairing code.

On the PC:

- Settings → **Direct link** → Enable → **Refresh**. The phone should be listed.
- Wireless only: paste the wireless `ip:port` and code into the Pair field → **Wireless pair**, then
  **Connect** with the `ip:port` that wireless debugging shows after pairing.
- The agent automatically runs `adb reverse tcp:8790 tcp:8790` for every usable device.

On the phone: Pair → **Direct** → enter the code shown in PC Settings → **Pair direct**.

Reference CLI equivalent:

```powershell
adb pair 192.168.1.50:37123 123456
adb connect 192.168.1.50:5555
adb reverse tcp:8790 tcp:8790
```

Security settings from the PC control the direct mode; server-side toggles are read-only there.

### Power and boot alerts

The relay raises `power` events when the PC disappears and when it returns:

```json
"offline_alert_seconds": 300,
"power_check_seconds": 20,
"boot_notify": true
```

`offline_alert_seconds` is the silence window before "went offline" (default five minutes). A hard power-off,
power-button shutdown, sleep or network drop is caught this way. On return you get "back online" with the
downtime, and a quicker reboot is reported as "restarted" when system uptime resets. These events notify the
phone like any other warning and are pushed through ntfy when configured.

## 6. Flipper Zero bridge (optional)

A Flipper Zero can act as a pocket remote. It has no network stack of its own, so it talks to your phone
over Bluetooth and the phone relays its commands to the relay — the token and TOTP secret stay on the
phone.

```powershell
cd flipper
ufbt            # build dist/sentinel_flipper.fap
ufbt launch     # install and start over USB
```

Or copy `Sentinel-Flipper-1.1.0.fap` from the release to `/ext/apps/GPIO/`.

1. **Flipper:** Applications → GPIO → **Sentinel Bridge**; leave it on **Link: Bluetooth**.
2. **Phone:** Settings → **Flipper** → **Set up** → **Scan for Flipper**, pick the Flipper (watch icon) and
   accept the pairing prompt. If the Flipper shows a PIN, type it on the phone.
3. The app performs a `PING`/`PONG` handshake and the status screen fills in with the PC name, the live
   2FA code and the countdown.

Close qFlipper while the bridge is in use: it owns the same BLE serial profile. If the phone says
*"No answer from the Flipper"*, the Flipper app is not open — open it and tap Reconnect.

Protocol, the optional ESP32 WiFi board for standalone use, and troubleshooting are documented in
[flipper/README.md](../flipper/README.md).

## 7. Push notifications (optional)
The phone raises local notifications while the app is running. For alerts when it is closed, point the relay
at an ntfy topic (ntfy.sh or self-hosted):

```json
"ntfy_url": "https://ntfy.sh",
"ntfy_topic": "sentinel-your-secret-topic"
```

Install the ntfy app on the phone and subscribe to the same topic. Away mode, security events, messages and
PC replies are pushed. Leave both empty to disable.

## 8. Verification

```powershell
# API contract
python server\tests\smoke.py http://127.0.0.1:8765

# Agent internals (TOTP RFC vectors, multi-monitor capture, local API, adb)
PCMonitor.Agent.exe --selftest "%TEMP%\sentinel.json"

# Full loop, isolated port 8799
powershell -ExecutionPolicy Bypass -File scripts\e2e.ps1
```

## Troubleshooting

| Symptom | Fix |
|---|---|
| Phone says "Cannot reach the server" | Same subnet? Firewall? Use `http://` for LAN, `https://` only with a real cert. |
| Agent shows **Re-pair** | Server token rejected (DB reset or device removed). Unpair and pair again. |
| Pair code fails | Codes expire in 10 minutes and are single-use. Generate a new one. |
| 2FA codes rejected | Clock drift. Phone/PC must be within ±30 s. Sync time. |
| Screenshots are black | Display off, secure desktop (UAC/lock) or protected content. Wake the display first. |
| Disarm says phone not present | Presence is required; connect ADB or disable the requirement on the PC. |
| `adb` not found | Settings → Direct link → Browse to `adb.exe` (usually `%LOCALAPPDATA%\Android\Sdk\platform-tools`). |
| Port 8790 in use | Change Local port in Settings; the reverse tunnel updates on the next ADB refresh. |
| Flutter rejects the path | Use `mobile\flutter.ps1`. |
| Biometrics missing on Windows | Expected: `local_auth` is Android/iOS. The app falls back to the PIN. |
| Agent won't start twice | It enforces a single instance; the second launch focuses the first. |
| Can't exit the agent | Exit needs your 2FA code. Use the tray menu and enter the phone code, or send "Quit PC agent" from Settings on the phone. |
| Agent restarts after being closed | That is the watchdog. Exit with authorization (code) to stop both. |
| Client gets `http_500` in bursts, server log shows `Too many open files` | The relay process hit its fd limit. Add `LimitNOFILE=65536` to the systemd unit, `daemon-reload`, restart. Server 1.2.2 also caps its SQLite connections (fixes the leak itself). |
| Updating the agent | Stop the watchdog process, then the agent, rebuild, and launch. An unauthorized kill alone will be relaunched from the old binary. |
