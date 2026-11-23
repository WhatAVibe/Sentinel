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
| POST | `/api/v1/pair/request` | `{device_name, platform, fingerprint, agent_version, enc_required?}` | `{request_id, pair_code, expires_at, request_token, server:{name,url}}` |
| GET | `/api/v1/pair/status` | `?request_id=&token=` | `{status, device_id, agent_token?, totp_secret?, device?, server?}` |
| POST | `/api/v1/pair/lookup` | `{pair_code}` | `{request_id, device_name, enc_required, expires_at, server}` |
| POST | `/api/v1/pair/approve` | `{request_id, pair_code, mobile_name}` | `{device, enc_required, mobile_token, totp_secret, server}` |
| POST | `/api/v1/pair/deny` | `{request_id}` | `{status:"denied"}` |

Pairing status transitions: `pending → approved → claimed` (agent fetched its token once), or
`expired` / `denied`.

## Agent (bearer: agent token)

| Method | Path | Body | Notes |
|---|---|---|---|
| POST | `/api/v1/agent/heartbeat` | `{away, locked, agent_version, metrics{cpu_percent,mem_used_mb,mem_total_mb,uptime_seconds,user,hostname}, state{monitors,session,screensaver,idle_seconds,...}}` | returns `{ok, server_time, device}` |
| GET | `/api/v1/agent/commands?wait=25` | — | long poll; `{commands:[{id,type,args,created_at}]}` |
| POST | `/api/v1/agent/commands/{id}/result` | `{ok, error, data}` | marks `done`/`failed` |
| POST | `/api/v1/agent/events` | `{events:[{ts,category,severity,title?,detail?,enc?}]}` | up to 500 per call |
| POST | `/api/v1/agent/screenshots` | multipart: `file`, `enc=1` for ciphertext, `ts`, `monitor`, `monitor_name`, `width`, `height`, `trigger` | `{ok, screenshot}` |
| POST | `/api/v1/agent/state` | `{away?, locked?, state?, settings?}` | returns updated device |
| GET | `/api/v1/agent/phones` | — | linked phones with timestamps |
| DELETE | `/api/v1/agent/phones/{id}` | — | revoke a phone |
| POST | `/api/v1/agent/messages/{mid}/reply` | `{reply?, enc?, status?}` | stores the PC's answer or ciphertext, emits an event for plaintext replies |
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
| POST | `/api/v1/devices/{id}/message` | `{title?, body?, enc?, kind?, allow_reply?}` | stores a message (plaintext or `enc`) and queues it |
| POST | `/api/v1/devices/{id}/companions` | `{name?, kind:"watch"}` | phone only, own device only; creates a confirmed watch token |
| GET | `/api/v1/devices/{id}/messages?limit=` | — | message threads with replies |
| GET | `/api/v1/devices/{id}/phones` | — | linked phones (`current` flags this phone) |
| DELETE | `/api/v1/devices/{id}/phones/{mid}` | — | revoke another phone |
| GET | `/api/v1/devices/{id}/audit?limit=` | — | recent server audit rows |
| POST | `/api/v1/devices/{id}/unlock` | `{code, action:"unlock"\|"away_disable"}` | |
| GET | `/api/v1/devices/{id}/commands?…` | — | last 40 |
| GET | `/api/v1/devices/{id}/commands/{cid}` | — | `{command}` |
| GET | `/api/v1/devices/{id}/events?since=&before=&limit=&category=` | — | ascending batch + `has_more`; use `before=<oldest id>` for older pages, max 500 |
| GET | `/api/v1/devices/{id}/screenshots?before=&limit=` | — | newest-first metadata + `has_more`; `before` is a `ts` |
| GET | `/api/v1/devices/{id}/screenshots/{sid}/file` | — | PNG, or `application/octet-stream` when the shot is encrypted (`?token=` allowed) |
| DELETE | `/api/v1/devices/{id}/screenshots/{sid}` | — | removes file + row |
| POST | `/api/v1/totp/check` | `{code}` | `{ok, seconds_remaining}` |

`POST /api/v1/devices/{id}/companions` is how the phone adds a watch without typing a code on it. It is
available to mobile tokens for their own device only, is rate limited (5/hour), and accepts
`{name?, kind: "watch"}`. The response is `{mobile_token, mobile_id, device_id, enc_required}`; the data
key is never returned (the phone already holds it and sends it to the watch over Bluetooth).

## Command types

`lock, unlock, away_enable, away_disable, screenshot, shutdown, restart, sleep, hibernate, logoff,
screen_off, screen_on, message, media, info, cancel_shutdown, alarm, agent_exit, lock_screen, unlock_screen`
`clipboard` also accepted (args `{text}`).

Code required: `unlock`, `away_disable`, `agent_exit`, `unlock_screen`; `shutdown`/`restart` when device
`settings.protect_power` is true.

`lock_screen` args: `{message}` - covers every display with the wallpaper and blocks input.
`unlock_screen` requires the TOTP code.
`clipboard` args: `{text}` - replaces the PC clipboard (max 20,000 chars). Used by the phone's copy button
and the watch's Copy action.

`message` kinds: `toast | dialog | alert | banner | fullscreen`.
`shutdown`/`restart` follow the PC's `power_mode`: `silent`, `notify`, or `confirm` (denial returns
`declined_by_user`).

`message` args: `{title, text, kind: toast|dialog|alert|banner, allow_reply, message_id}`. For an encrypted
phone message the args carry `enc` instead of `title`/`text`; `kind` and `allow_reply` stay plaintext.

## Events

Categories: `window, process, network, search, input, message, clipboard, session, idle, usb, security,
screenshot, system`. Severities: `info, notice, warning`.

Example:

```bash
curl -X POST http://host:8765/api/v1/devices/DEV/command \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"type":"message","args":{"text":"Dinner time"}}'
```

## Encrypted mode

Devices paired with `enc_required` only store ciphertext for user content. `enc` is an opaque string
defined in [ENCRYPTION.md](ENCRYPTION.md); the relay never decrypts it.

- Agent writes and phone messages without `enc` are rejected with `400 {"error":"enc_required"}`: event
  batches as a whole, screenshots without the field `enc=1`, and plaintext message/reply content. A mixed
  event batch stores nothing.
- Screenshot uploads on such devices must include the multipart field `enc=1`. Files are stored as
  `<id>.enc` and the file endpoint returns them as `application/octet-stream`.
- When `enc` is present the relay clears `title`, `detail`, `body` and `reply`, stores `enc`, and includes
  it in every serializer.
- Phone→PC messages forward `enc` in the `message` command args instead of plaintext (`allow_reply` and
  `kind` stay plaintext metadata).
- Relay-generated `power` events remain plaintext; they contain no user content.

## Objects

**Device**

```json
{
  "id": "hex", "name": "DESKTOP-ABC", "platform": "windows", "online": true,
  "last_seen": 1763000000000, "away": false, "locked": true, "enc_required": true,
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

**Event**: `{id, ts, category, severity, title, detail, enc}` with categories
`window|process|network|search|input|message|clipboard|session|idle|power|security|screenshot|system` and
severities `info|notice|warning`. Encrypted rows have `enc` set, `title` empty and `detail` null; render
the decrypted `{title, detail}` instead.

The relay also creates `power` events itself: "went offline" (after `offline_alert_seconds` without a
heartbeat), "back online" (with downtime), and "restarted" (system uptime reset).

**Message**: `{id, device_id, direction, kind, title, body, reply, allow_reply, status,
created_at, updated_at, enc}`. Encrypted rows clear `title`, `body` and `reply`.

**Command**: `{id, type, args, status, created_at, delivered_at, completed_at, result{ok,error,data}, source}`,
status `pending|delivered|done|failed|timeout|expired`.
Pending commands expire after `command_ttl_seconds` (default 120) and are never delivered afterwards.

**Screenshot**: `{id, ts, monitor, monitor_name, width, height, trigger, size, enc, url}`.

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
