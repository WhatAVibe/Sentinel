# Sentinel Relay

Flask + SQLite relay for Sentinel. It stores devices, linked phones/watch, events, messages and screenshots,
and brokers commands between the PC agent and the mobile clients. It never sees your Windows password and
cannot unlock the PC on its own.

Version: **1.3.3** (`sentinel_server/config.py`). Protocol: `v1.2`.

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
| `ntfy_url` / `ntfy_topic` | Optional push notifications |

Data lives in `storage/sentinel.db` plus `storage/shots/`. Back up that directory.

## Deploy

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
