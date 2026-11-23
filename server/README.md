# Sentinel Relay

Flask + SQLite relay for Sentinel. It stores devices, linked phones/watch, events, messages and screenshots,
and brokers commands between the PC agent and the mobile clients. It never sees your Windows password and
cannot unlock the PC on its own.

Version: **2.0.1** (`sentinel_server/config.py`). Protocol: `v1.2`.

## Run

```bash
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
cp config.example.json config.json
.venv/bin/python server.py
```

The API lives under `/api/v1`, the status page at `/`. Default port `8765`.

`config.json` (created on first run if absent, see `config.example.json`):

| Key | Meaning |
|---|---|
| `server_name` | Name shown on pairing screens |
| `public_url` | Public base URL handed to clients during pairing |
| `allow_registration` | Allow new device/phone pairing |
| `pair_code_ttl_seconds` | Pair code lifetime (default 600) |
| `command_ttl_seconds` / `command_timeout_seconds` | Command expiry and execution timeout |
| `storage_dir` | SQLite and screenshot storage (default `storage`) |
| `retention_days` | Event and screenshot retention |
| `offline_alert_seconds` | How long the PC can be quiet before "offline" is raised |
| `disk_alert_percent` | Free-space percentage that triggers a low-disk event (0 disables) |
| `battery_alert_percent` | Battery percentage that triggers a low-battery event while discharging (0 disables) |
| `ntfy_url` / `ntfy_topic` | Optional push notifications |
| `screenshot_max_per_device` / `screenshot_max_mb_per_device` | Per-device screenshot count/byte budget; oldest are deleted |
| `manual_screenshots_per_30m` | Manual/tray/remote screenshot cap |
| `screenshot_uploads_per_minute` | Hard upload rate per device |
| `events_per_minute` | Event ingestion cap per device |

Data lives in `storage/sentinel.db` plus `storage/shots/`. Back up that directory.

## Deploy

Docker (from the repository root):

```bash
docker compose up -d --build
```

The compose file publishes `8765` and keeps `/data` in the `sentinel-data` volume. Put Caddy/Tailscale in
front of it for TLS.

Linux with systemd:

```ini
[Unit]
Description=Sentinel relay
After=network-online.target

[Service]
WorkingDirectory=/opt/sentinel/server
ExecStart=/opt/sentinel/venv/bin/python /opt/sentinel/server/server.py
Restart=always
RestartSec=5
User=sentinel
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
```

Windows works too (`run.bat`); keep it alive with NSSM or a scheduled task. Put real TLS in front of it
(Caddy, nginx, Cloudflare Tunnel) or reach it over Tailscale, and never expose plain HTTP with bearer
tokens. Details in [../docs/SETUP.md](../docs/SETUP.md).

## Tests

```bash
python tests/smoke.py http://127.0.0.1:8799
```

The smoke test expects a relay already running on that port. API reference: [../docs/API.md](../docs/API.md).

## Backup

`storage/` is the whole state (SQLite + screenshots). Use the included tool; it uses SQLite's online backup
API, so it is safe while the relay is running and under WAL:

```bash
python tools/backup.py --config config.json --out /var/backups/sentinel --keep-days 14
```

The archive contains `sentinel.db`, `config.json`, `shots/` and a manifest. Restore by stopping the relay,
replacing `storage/sentinel.db` and `storage/shots/`, then starting it again. Schedule it with cron or a
systemd timer; keep one copy off the host. Without a backup, a lost `sentinel.db` means every PC must be
re-paired.
