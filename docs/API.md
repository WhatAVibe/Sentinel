# Relay API v1

Base: `http://<host>:8765` · all JSON · timestamps are unix **milliseconds** · auth via
`Authorization: Bearer <token>` (or `?token=` for image/WS URLs). Mobile clients may send
`X-Device-Name: <model>`; the relay stores it as the linked phone's display name.

Errors: HTTP 4xx/5xx with `{"error": "snake_case_code"}`.
Common codes: `bad_code`, `too_many_attempts`, `not_found`, `expired`, `unauthorized`, `invalid_mobile_token`,
`unsupported_command`, `phone_not_present`, `capture_busy`, `rate_limited`.

## Public

| Method | Path | Body / Query | Returns |
|---|---|---|---|
| GET | `/api/v1/health` | — | `{ok, name, version, time, public_url}` |
| POST | `/api/v1/pair/request` | `{device_name, platform, fingerprint, agent_version}` | `{request_id, pair_code, expires_at, request_token, server:{name,url}}` |
| GET | `/api/v1/pair/status` | `?request_id=&token=` | `{status, device_id, agent_token?, totp_secret?, device?, server?}` |
| POST | `/api/v1/pair/lookup` | `{pair_code}` | `{request_id, device_name, expires_at, server}` |
| POST | `/api/v1/pair/approve` | `{request_id, pair_code, mobile_name}` | `{device, mobile_token, totp_secret, server}` |
| POST | `/api/v1/pair/deny` | `{request_id}` | `{status:"denied"}` |

Pairing status transitions: `pending → approved → claimed` (agent fetched its token once), or
`expired` / `denied`.

## Agent (bearer: agent token)

| Method | Path | Body | Notes |
|---|---|---|---|
| POST | `/api/v1/agent/heartbeat` | `{away, locked, agent_version, metrics{cpu_percent,mem_used_mb,mem_total_mb,uptime_seconds,user,hostname}, state{monitors,session,screensaver,idle_seconds,...}}` | returns `{ok, server_time, device}` |
| GET | `/api/v1/agent/commands?wait=25` | — | long poll; `{commands:[{id,type,args,created_at}]}` |
| POST | `/api/v1/agent/commands/{id}/result` | `{ok, error, data}` | marks `done`/`failed` |
| POST | `/api/v1/agent/events` | `{events:[{ts,category,severity,title,detail}]}` | up to 500 per call |
| POST | `/api/v1/agent/screenshots` | multipart: `file`, `ts`, `monitor`, `monitor_name`, `width`, `height`, `trigger` | `{ok, screenshot}` |
| POST | `/api/v1/agent/state` | `{away?, locked?, state?, settings?}` | returns updated device |
| GET | `/api/v1/agent/phones` | — | linked phones with timestamps |
| DELETE | `/api/v1/agent/phones/{id}` | — | revoke a phone |
| POST | `/api/v1/agent/messages/{mid}/reply` | `{reply, status}` | stores the PC's answer, emits an event |
| POST | `/api/v1/agent/link-request` | — | new code for an extra phone |

## Mobile (bearer: mobile token)

| Method | Path | Body / Query | Notes |
|---|---|---|---|
| GET | `/api/v1/me` | — | `{mobile, device(+totp_secret), totp_secret, totp_seconds_remaining, server}` |
| DELETE | `/api/v1/me` | — | remove this phone from the PC (keeps other phones) |
| GET | `/api/v1/devices` | — | list (no secrets) |
| GET | `/api/v1/devices/{id}` | — | detail + counts + secret |
| PATCH | `/api/v1/devices/{id}` | `{name?, settings{require_2fa_unlock?, protect_power?}}` | |
| DELETE | `/api/v1/devices/{id}` | — | unpair this PC (removes all phones) |
| POST | `/api/v1/devices/{id}/command` | `{type, args?, code?}` | `{ok, command}` |
| POST | `/api/v1/devices/{id}/message` | `{title?, body, kind?, allow_reply?}` | stores a message and queues it |
| GET | `/api/v1/devices/{id}/messages?limit=` | — | message threads with replies |
| GET | `/api/v1/devices/{id}/phones` | — | linked phones (`current` flags this phone) |
| DELETE | `/api/v1/devices/{id}/phones/{mid}` | — | revoke another phone |
| GET | `/api/v1/devices/{id}/audit?limit=` | — | recent server audit rows |
| POST | `/api/v1/devices/{id}/unlock` | `{code, action:"unlock"\|"away_disable"}` | |
| GET | `/api/v1/devices/{id}/commands?…` | — | last 40 |
| GET | `/api/v1/devices/{id}/commands/{cid}` | — | `{command}` |
| GET | `/api/v1/devices/{id}/events?since=&before=&limit=&category=` | — | ascending batch + `has_more`; use `before=<oldest id>` for older pages, max 500 |
| GET | `/api/v1/devices/{id}/screenshots?before=&limit=` | — | newest-first metadata + `has_more`; `before` is a `ts` |
| GET | `/api/v1/devices/{id}/screenshots/{sid}/file` | — | PNG (`?token=` allowed) |
| DELETE | `/api/v1/devices/{id}/screenshots/{sid}` | — | removes file + row |
| POST | `/api/v1/totp/check` | `{code}` | `{ok, seconds_remaining}` |

## Command types

`lock, unlock, away_enable, away_disable, screenshot, shutdown, restart, sleep, hibernate, logoff,
screen_off, screen_on, message, media, info, cancel_shutdown, alarm, agent_exit, lock_screen, unlock_screen`

Code required: `unlock`, `away_disable`, `agent_exit`, `unlock_screen`; `shutdown`/`restart` when device
`settings.protect_power` is true.

`lock_screen` args: `{message}` — covers every display with the wallpaper and blocks input.
`unlock_screen` requires the TOTP code.

`message` kinds: `toast | dialog | alert | banner | fullscreen`.
`shutdown`/`restart` follow the PC's `power_mode`: `silent`, `notify`, or `confirm` (denial returns
`declined_by_user`).

`message` args: `{title, text, kind: toast|dialog|alert|banner, allow_reply, message_id}`.

## Events

Categories: `window, process, network, search, input, message, clipboard, session, idle, usb, security,
screenshot, system`. Severities: `info, notice, warning`.

Example:

```bash
curl -X POST http://host:8765/api/v1/devices/DEV/command \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"type":"message","args":{"text":"Dinner time"}}'
```

## Objects

**Device**

```json
{
  "id": "hex", "name": "DESKTOP-ABC", "platform": "windows", "online": true,
  "last_seen": 1763000000000, "away": false, "locked": true,
  "agent_version": "1.2.1", "relay": true,
  "settings": {"require_2fa_unlock": true, "protect_power": false,
               "power_mode": "confirm", "power_message": "Your phone asked to {action} this PC."},
  "state": {"monitors": 2, "session": "active", "screensaver": false, "idle_seconds": 12,
            "away_reason": null, "agent_uptime_seconds": 900, "local_api": true, "phone_present": false,
            "secure_lock": false},
  "metrics": {"cpu_percent": 12.4, "mem_used_mb": 8123, "mem_total_mb": 32768,
              "uptime_seconds": 92000, "user": "yourname", "hostname": "DESKTOP-ABC"}
}
```

**Event**: `{id, ts, category, severity, title, detail}` with categories
`window|process|network|search|input|message|clipboard|session|idle|power|security|screenshot|system` and
severities `info|notice|warning`.

The relay also creates `power` events itself: "went offline" (after `offline_alert_seconds` without a
heartbeat), "back online" (with downtime), and "restarted" (system uptime reset).

**Message**: `{id, device_id, direction, kind, title, body, reply, allow_reply, status,
created_at, updated_at}`.

**Command**: `{id, type, args, status, created_at, delivered_at, completed_at, result{ok,error,data}, source}`,
status `pending|delivered|done|failed|timeout|expired`.
Pending commands expire after `command_ttl_seconds` (default 120) and are never delivered afterwards.

**Screenshot**: `{id, ts, monitor, monitor_name, width, height, trigger, size, url}`.

## WebSocket

`ws(s)://<host>/ws/client?token=<mobile_token>`

```json
{"type":"hello","device_id":"hex"}
{"type":"state","device_id":"hex","device":{...}}
{"type":"events","device_id":"hex","events":[...]}
{"type":"command","device_id":"hex","command":{...}}
{"type":"screenshots","device_id":"hex","screenshots":[...]}
{"type":"paired"}
{"type":"ping","ts":1763000000000}
```

`ws(s)://<host>/ws/agent?token=<agent_token>` pushes `{"type":"command","command":{...}}` and pings.

## Direct mode (agent loopback)

Same shapes under `/local`, no `/api/v1`, on `http://127.0.0.1:8790` through `adb reverse`:

`GET /local/health` · `POST /local/pair` · `GET /local/status` · `POST /local/command` ·
`GET /local/commands/{id}` · `POST /local/unlock` · `POST /local/totp/check` · `GET /local/events` ·
`GET /local/screenshots` · `GET /local/screenshots/{id}/file`
