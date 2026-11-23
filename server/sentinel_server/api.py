import csv
import io
import json
import time
import uuid
from datetime import datetime, timezone
from functools import wraps
from pathlib import Path

from flask import Blueprint, current_app, g, jsonify, request, send_file

from .db import Database
from .hub import Hub
from .notify import push
from .power import create_event as create_power_event
from .power import check_metric_alerts
from .security import (
    hash_token,
    limiter,
    new_pair_code,
    new_token,
    new_totp_secret,
    totp_seconds_remaining,
    verify_totp,
)

bp = Blueprint("api", __name__, url_prefix="/api/v1")

ALLOWED_COMMANDS = {
    "lock",
    "unlock",
    "away_enable",
    "away_disable",
    "screenshot",
    "shutdown",
    "restart",
    "sleep",
    "hibernate",
    "logoff",
    "screen_off",
    "screen_on",
    "message",
    "media",
    "info",
    "cancel_shutdown",
    "alarm",
    "agent_exit",
    "lock_screen",
    "unlock_screen",
    "clipboard",
}

CODE_REQUIRED = {"unlock", "away_disable", "agent_exit", "unlock_screen"}

ONLINE_WINDOW_MS = 90_000


def now_ms() -> int:
    return int(time.time() * 1000)


def db() -> Database:
    return current_app.extensions["pcm"]["db"]


def hub() -> Hub:
    return current_app.extensions["pcm"]["hub"]


def cfg():
    return current_app.extensions["pcm"]["config"]


def _json_body() -> dict:
    return request.get_json(silent=True) or {}


def _bearer() -> str | None:
    header = request.headers.get("Authorization", "")
    if header.lower().startswith("bearer "):
        return header.split(" ", 1)[1].strip()
    token = request.args.get("token") or request.headers.get("X-Auth-Token")
    return token.strip() if token else None


def _device_from_token(token: str | None) -> dict | None:
    if not token:
        return None
    return db().one(
        "SELECT * FROM devices WHERE token_hash = ?", (hash_token(token),)
    )


def _mobile_from_token(token: str | None) -> dict | None:
    if not token:
        return None
    row = db().one("SELECT * FROM mobiles WHERE token_hash = ?", (hash_token(token),))
    if not row:
        return None
    device = db().one("SELECT * FROM devices WHERE id = ?", (row["device_id"],))
    if not device:
        return None
    row["device"] = device
    return row


def agent_required(fn):
    @wraps(fn)
    def wrapper(*args, **kwargs):
        device = _device_from_token(_bearer())
        if not device:
            return jsonify(error="invalid_agent_token"), 401
        g.device = device
        g.device_id = device["id"]
        g.actor = f"agent:{device['id'][:8]}"
        return fn(*args, **kwargs)

    return wrapper


def mobile_required(fn):
    @wraps(fn)
    def wrapper(*args, **kwargs):
        mobile = _mobile_from_token(_bearer())
        if not mobile:
            return jsonify(error="invalid_mobile_token"), 401
        g.mobile = mobile
        g.device = mobile["device"]
        g.device_id = mobile["device"]["id"]
        g.actor = f"mobile:{mobile['id'][:8]}"
        if not mobile.get("confirmed", 1) and request.endpoint != "api.me":
            return jsonify(error="pending_confirmation"), 403
        name = (request.headers.get("X-Device-Name") or "").strip()[:64]
        if name and not mobile.get("name_locked") and name != (mobile.get("name") or ""):
            db().execute(
                "UPDATE mobiles SET last_seen=?, name=? WHERE id=?",
                (now_ms(), name, mobile["id"]),
            )
            mobile["name"] = name
        else:
            db().execute("UPDATE mobiles SET last_seen = ? WHERE id = ?", (now_ms(), mobile["id"]))
        app_version = (request.headers.get("X-App-Version") or "").strip()[:32]
        if app_version and app_version != (mobile.get("app_version") or ""):
            db().execute("UPDATE mobiles SET app_version = ? WHERE id = ?", (app_version, mobile["id"]))
            mobile["app_version"] = app_version
        ua = request.headers.get("User-Agent") or ""
        if "SentinelWatch" in ua:
            kind = "watch"
        else:
            kind = mobile.get("kind") or "phone"
        if kind != (mobile.get("kind") or "phone"):
            db().execute("UPDATE mobiles SET kind = ? WHERE id = ?", (kind, mobile["id"]))
            mobile["kind"] = kind
        return fn(*args, **kwargs)

    return wrapper


def parse_json(value, default):
    if value is None:
        return default
    if isinstance(value, (dict, list)):
        return value
    try:
        return json.loads(value)
    except (TypeError, ValueError):
        return default


def enc_field(value) -> str | None:
    if not isinstance(value, str):
        return None
    value = value.strip()
    return value or None


def flag(value) -> bool:
    return str(value or "").strip().lower() in ("1", "true", "yes", "on")


def serialize_device(row: dict, include_secret: bool = False) -> dict:
    settings = parse_json(row.get("settings"), {})
    state = parse_json(row.get("state"), {})
    metrics = parse_json(row.get("metrics"), {})
    online = bool(row.get("last_seen")) and (now_ms() - row["last_seen"] < ONLINE_WINDOW_MS)
    data = {
        "id": row["id"],
        "name": row["name"],
        "platform": row.get("platform") or "windows",
        "agent_version": row.get("agent_version"),
        "online": online,
        "last_seen": row.get("last_seen") or 0,
        "away": bool(row.get("away")),
        "locked": bool(row.get("locked")),
        "enc_required": bool(row.get("enc_required")),
        "settings": settings,
        "state": state,
        "metrics": metrics,
        "relay": hub().agent_online(row["id"]),
        "linked_devices": db().one(
            "SELECT COUNT(*) AS n FROM mobiles WHERE device_id=? AND confirmed=1", (row["id"],)
        )["n"],
        "created_at": row.get("created_at"),
    }
    if include_secret:
        data["totp_secret"] = row.get("totp_secret")
    return data


def serialize_command(row: dict) -> dict:
    return {
        "id": row["id"],
        "type": row["type"],
        "args": parse_json(row.get("args"), {}),
        "status": row["status"],
        "created_at": row.get("created_at"),
        "delivered_at": row.get("delivered_at"),
        "completed_at": row.get("completed_at"),
        "result": parse_json(row.get("result"), None),
        "source": row.get("source"),
    }


def serialize_event(row: dict) -> dict:
    return {
        "id": row["id"],
        "ts": row["ts"],
        "category": row["category"],
        "severity": row["severity"],
        "title": row["title"],
        "detail": parse_json(row.get("detail"), None),
        "enc": row.get("enc"),
    }


def serialize_shot(row: dict) -> dict:
    return {
        "id": row["id"],
        "ts": row["ts"],
        "monitor": row.get("monitor"),
        "monitor_name": row.get("monitor_name"),
        "width": row.get("width"),
        "height": row.get("height"),
        "trigger": row.get("trigger"),
        "size": row.get("size"),
        "enc": bool(row.get("enc")),
        "url": f"/api/v1/devices/{row['device_id']}/screenshots/{row['id']}/file",
    }


def serialize_phone(row: dict, current: bool = False) -> dict:
    return {
        "id": row["id"],
        "name": row.get("name") or "Phone",
        "created_at": row.get("created_at"),
        "last_seen": row.get("last_seen"),
        "current": current,
        "primary": bool(row.get("is_primary")),
        "confirmed": bool(row.get("confirmed", 1)),
        "app_version": row.get("app_version") or "",
        "kind": row.get("kind") or "phone",
    }


def serialize_message(row: dict) -> dict:
    return {
        "id": row["id"],
        "device_id": row["device_id"],
        "direction": row["direction"],
        "kind": row["kind"],
        "title": row.get("title"),
        "body": row.get("body"),
        "reply": row.get("reply"),
        "allow_reply": bool(row.get("allow_reply")),
        "status": row["status"],
        "created_at": row["created_at"],
        "updated_at": row.get("updated_at"),
        "enc": row.get("enc"),
    }


def device_settings(device: dict) -> dict:
    return {
        "require_2fa_unlock": True,
        "protect_power": False,
        "power_mode": "confirm",
        "power_message": "",
        **parse_json(device.get("settings"), {}),
    }


def issue_command(device_id: str, ctype: str, args: dict | None = None, source: str = "remote") -> dict:
    cid = uuid.uuid4().hex
    row = {
        "id": cid,
        "device_id": device_id,
        "type": ctype,
        "args": args or {},
        "source": source,
        "status": "pending",
        "created_at": now_ms(),
    }
    db().execute(
        "INSERT INTO commands (id, device_id, type, args, source, status, created_at) "
        "VALUES (?,?,?,?,?,?,?)",
        (cid, device_id, ctype, json.dumps(row["args"]), source, "pending", row["created_at"]),
    )
    db().audit(source, "command.created", {"device": device_id, "command": row})
    hub().push_agent(
        device_id,
        {
            "type": "command",
            "command": {
                "id": cid,
                "type": ctype,
                "args": row["args"],
                "created_at": row["created_at"],
            },
        },
    )
    hub().push_to_device_clients(
        device_id,
        {"type": "command", "command": serialize_command({**row, "delivered_at": None, "completed_at": None, "result": None})},
    )
    return serialize_command({**row, "delivered_at": None, "completed_at": None, "result": None})


# --------------------------------------------------------------------------- public


@bp.get("/health")
def health():
    return jsonify(
        ok=True,
        name=cfg().data["server_name"],
        version=cfg().version,
        time=now_ms(),
        public_url=cfg().public_base(),
    )


@bp.post("/pair/request")
def pair_request():
    if not cfg().data.get("allow_registration", True):
        return jsonify(error="registration_disabled"), 403

    body = _json_body()
    agent = _device_from_token(_bearer())
    enc_required = 1 if body.get("enc_required") else 0
    code = new_pair_code()
    rid = uuid.uuid4().hex
    token = new_token()
    now = now_ms()
    ttl = int(cfg().data.get("pair_code_ttl_seconds", 600)) * 1000

    if agent:
        device_id = agent["id"]
        name = agent["name"]
        platform = agent.get("platform")
        fingerprint = agent.get("fingerprint")
        tmp_secret = None
        if "enc_required" in body:
            db().execute(
                "UPDATE devices SET enc_required = ? WHERE id = ?", (enc_required, device_id)
            )
    else:
        device_id = uuid.uuid4().hex
        name = (body.get("device_name") or "Windows PC").strip()[:64]
        platform = (body.get("platform") or "windows")[:32]
        fingerprint = (body.get("fingerprint") or "")[:128]
        totp_secret = new_totp_secret()
        tmp_secret = token
        db().execute(
            "INSERT INTO devices (id, name, platform, fingerprint, agent_version, token_hash, "
            "totp_secret, created_at, settings, state, metrics, enc_required) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
            (
                device_id,
                name,
                platform,
                fingerprint,
                (body.get("agent_version") or "")[:32],
                hash_token(token),
                totp_secret,
                now,
                json.dumps({}),
                json.dumps({}),
                json.dumps({}),
                enc_required,
            ),
        )

    db().execute(
        "INSERT INTO pair_requests (id, code, device_id, device_name, platform, fingerprint, "
        "token_hash, status, created_at, expires_at, secret_tmp) VALUES (?,?,?,?,?,?,?,?,?,?,?)",
        (rid, code, device_id, name, platform, fingerprint, hash_token(token), "pending", now, now + ttl, tmp_secret),
    )
    db().audit("agent" if agent else "unknown", "pair.request", {"request": rid, "device": device_id})

    return jsonify(
        request_id=rid,
        pair_code=code,
        expires_at=now + ttl,
        request_token=token,
        server={"name": cfg().data["server_name"], "url": cfg().public_base()},
        existing_device=bool(agent),
    )


@bp.get("/pair/status")
def pair_status():
    rid = request.args.get("request_id", "")
    token = request.args.get("token") or _bearer()
    row = db().one("SELECT * FROM pair_requests WHERE id = ?", (rid,))
    if not row or not token or hash_token(token) != row["token_hash"]:
        return jsonify(error="not_found"), 404
    if row["status"] == "pending" and now_ms() > row["expires_at"]:
        db().execute("UPDATE pair_requests SET status = 'expired' WHERE id = ?", (rid,))
        row["status"] = "expired"

    if row["status"] == "approved":
        device = db().one("SELECT * FROM devices WHERE id = ?", (row["device_id"],))
        plain = row.get("secret_tmp")
        db().execute("UPDATE pair_requests SET status = 'claimed', secret_tmp = NULL WHERE id = ?", (rid,))
        if device:
            hub().push_to_device_clients(device["id"], {"type": "paired"})
        return jsonify(
            status="approved",
            device_id=row["device_id"],
            agent_token=plain,
            totp_secret=device["totp_secret"] if device else None,
            device=serialize_device(device, include_secret=True) if device else None,
            server={"name": cfg().data["server_name"], "url": cfg().public_base()},
        )
    return jsonify(status=row["status"])


@bp.post("/pair/lookup")
def pair_lookup():
    if not limiter.allow(f"lookup:{request.remote_addr}", 20, 60):
        return jsonify(error="rate_limited"), 429
    body = _json_body()
    code = (body.get("pair_code") or "").strip().upper()
    if len(code) != 6:
        return jsonify(error="bad_code"), 400
    row = db().one(
        "SELECT * FROM pair_requests WHERE code=? AND status='pending' AND expires_at > ? "
        "ORDER BY created_at DESC LIMIT 1",
        (code, now_ms()),
    )
    if not row:
        return jsonify(error="not_found"), 404
    device = db().one("SELECT * FROM devices WHERE id=?", (row["device_id"],))
    return jsonify(
        request_id=row["id"],
        device_name=(device["name"] if device else row["device_name"]) or "Windows PC",
        enc_required=bool(device.get("enc_required")) if device else False,
        expires_at=row["expires_at"],
        server={"name": cfg().data["server_name"], "url": cfg().public_base()},
    )


@bp.post("/pair/approve")
def pair_approve():
    body = _json_body()
    rid = (body.get("request_id") or "").strip()
    code = (body.get("pair_code") or "").strip().upper()
    if not limiter.allow(f"pair:{request.remote_addr}", 12, 60):
        return jsonify(error="rate_limited"), 429

    row = db().one("SELECT * FROM pair_requests WHERE id = ?", (rid,))
    if not row:
        return jsonify(error="not_found"), 404
    if row["status"] not in ("pending", "approved"):
        return jsonify(error=row["status"]), 409
    if now_ms() > row["expires_at"]:
        db().execute("UPDATE pair_requests SET status='expired' WHERE id=?", (rid,))
        return jsonify(error="expired"), 410
    if code != row["code"]:
        db().audit("mobile", "pair.bad_code", {"request": rid})
        return jsonify(error="bad_code"), 403

    device = db().one("SELECT * FROM devices WHERE id = ?", (row["device_id"],))
    if not device:
        return jsonify(error="device_missing"), 404

    mobile_token = new_token()
    mobile_uid = (body.get("mobile_uid") or "").strip()[:64]
    mobile_name = (body.get("mobile_name") or "Phone")[:64]
    app_version = (request.headers.get("X-App-Version") or "").strip()[:32]
    ua = request.headers.get("User-Agent") or ""
    kind = "watch" if "SentinelWatch" in ua else "phone"
    existing = None
    if mobile_uid:
        existing = db().one(
            "SELECT * FROM mobiles WHERE device_id = ? AND uid = ? AND uid <> ''",
            (device["id"], mobile_uid),
        )
        if not existing:
            # adopt a pre-uid row with the same name so re-pairing does not duplicate it
            existing = db().one(
                "SELECT * FROM mobiles WHERE device_id = ? AND uid IS NULL AND name = ? "
                "ORDER BY created_at DESC LIMIT 1",
                (device["id"], mobile_name),
            )
            if existing:
                db().execute("UPDATE mobiles SET uid = ? WHERE id = ?", (mobile_uid, existing["id"]))
    if existing:
        mid = existing["id"]
        db().execute(
            "UPDATE mobiles SET name = CASE WHEN COALESCE(name_locked, 0) = 1 THEN name ELSE ? END, "
            "token_hash = ?, last_seen = ?, confirmed = 0, app_version = ?, kind = ? WHERE id = ?",
            (mobile_name, hash_token(mobile_token), now_ms(), app_version or None, kind, mid),
        )
    else:
        mid = uuid.uuid4().hex
        db().execute(
            "INSERT INTO mobiles (id, name, device_id, token_hash, created_at, uid, confirmed, "
            "app_version, kind) VALUES (?,?,?,?,?,?,0,?,?)",
            (mid, mobile_name, device["id"], hash_token(mobile_token), now_ms(), mobile_uid or None,
             app_version or None, kind),
        )
    db().execute("UPDATE pair_requests SET status='approved' WHERE id=?", (rid,))
    db().audit("mobile", "pair.approved", {"request": rid, "device": device["id"]})
    hub().push_to_device_clients(device["id"], {"type": "paired"})

    return jsonify(
        device=serialize_device(device, include_secret=True),
        enc_required=bool(device.get("enc_required")),
        mobile_token=mobile_token,
        mobile_id=mid,
        confirmed=False,
        totp_secret=device["totp_secret"],
        server={"name": cfg().data["server_name"], "url": cfg().public_base()},
    )


@bp.post("/pair/deny")
def pair_deny():
    body = _json_body()
    rid = (body.get("request_id") or "").strip()
    row = db().one("SELECT * FROM pair_requests WHERE id = ?", (rid,))
    if not row:
        return jsonify(error="not_found"), 404
    db().execute("UPDATE pair_requests SET status='denied' WHERE id=?", (rid,))
    db().audit("mobile", "pair.denied", {"request": rid})
    return jsonify(status="denied")


# --------------------------------------------------------------------------- agent


@bp.post("/agent/heartbeat")
@agent_required
def agent_heartbeat():
    body = _json_body()
    now = now_ms()
    metrics = body.get("metrics") or {}
    state = body.get("state") or {}
    current = db().one("SELECT * FROM devices WHERE id = ?", (g.device_id,)) or g.device
    previous_last = current.get("last_seen") or 0
    previous_alert = bool(current.get("offline_alerted"))
    previous_metrics = parse_json(current.get("metrics"), {})
    threshold = int(cfg().data.get("offline_alert_seconds", 300)) * 1000

    db().execute(
        "UPDATE devices SET last_seen=?, away=?, locked=?, metrics=?, state=?, agent_version=?, ip=? WHERE id=?",
        (
            now,
            1 if body.get("away") else 0,
            1 if body.get("locked") else 0,
            json.dumps(metrics),
            json.dumps(state),
            (body.get("agent_version") or "")[:32],
            (request.remote_addr or "")[:64],
            g.device_id,
        ),
    )
    if body.get("name"):
        db().execute("UPDATE devices SET name=? WHERE id=?", (str(body["name"]).strip()[:64], g.device_id))
    device = db().one("SELECT * FROM devices WHERE id=?", (g.device_id,))
    payload = serialize_device(device)
    hub().push_to_device_clients(g.device_id, {"type": "state", "device": payload})

    offline_for = (now - previous_last) if previous_last else 0
    was_offline = previous_alert or (previous_last > 0 and offline_for > threshold)
    if was_offline:
        db().execute("UPDATE devices SET offline_alerted = 0 WHERE id = ?", (g.device_id,))
        minutes = max(1, round(offline_for / 60000))
        create_power_event(
            db(),
            hub(),
            cfg(),
            g.device_id,
            f"{device['name']} is back online",
            {
                "offline_seconds": round(offline_for / 1000),
                "minutes": minutes,
                "message": f"{device['name']} is back online after about {minutes} minute(s).",
            },
            severity="notice",
        )
    elif cfg().data.get("boot_notify", True):
        previous_uptime = int(previous_metrics.get("uptime_seconds") or 0)
        current_uptime = int(metrics.get("uptime_seconds") or 0)
        if previous_uptime > 120 and current_uptime + 60 < previous_uptime:
            create_power_event(
                db(),
                hub(),
                cfg(),
                g.device_id,
                f"{device['name']} restarted",
                {
                    "previous_uptime_seconds": previous_uptime,
                    "uptime_seconds": current_uptime,
                    "message": f"{device['name']} rebooted and is back online.",
                },
                severity="notice",
            )

    check_metric_alerts(cfg(), db(), hub(), g.device_id, device["name"], metrics)

    return jsonify(ok=True, server_time=now, device=payload)


@bp.get("/agent/commands")
@agent_required
def agent_commands():
    wait = min(int(request.args.get("wait", 25)), 25)
    deadline = time.time() + wait

    # Commands are short-lived. A phone may queue something while the PC is
    # asleep, locked or shutting down; without this, the command would sit in
    # the queue and fire after the next boot. Expire anything older than
    # command_ttl_seconds before looking for work.
    ttl_ms = int(cfg().data.get("command_ttl_seconds", 120)) * 1000
    now = now_ms()
    cutoff = now - ttl_ms
    db().execute(
        "UPDATE commands SET status='expired', completed_at=? "
        "WHERE device_id=? AND status='pending' AND created_at < ?",
        (now, g.device_id, cutoff),
    )

    while True:
        if hub().agent_online(g.device_id):
            # The websocket channel owns delivery while it is connected.
            time.sleep(0.5)
            if time.time() >= deadline:
                return jsonify(commands=[])
            continue
        rows = db().all(
            "SELECT * FROM commands WHERE device_id=? AND status='pending' AND created_at >= ? "
            "ORDER BY created_at LIMIT 10",
            (g.device_id, cutoff),
        )
        if rows:
            now = now_ms()
            fresh = [r for r in rows if r["created_at"] >= now - ttl_ms]
            stale = [r for r in rows if r["created_at"] < now - ttl_ms]
            if stale:
                db().execute(
                    f"UPDATE commands SET status='expired', completed_at=? "
                    f"WHERE id IN ({','.join('?' * len(stale))})",
                    (now, *[r["id"] for r in stale]),
                )
            if fresh:
                db().execute(
                    f"UPDATE commands SET status='delivered', delivered_at=? "
                    f"WHERE id IN ({','.join('?' * len(fresh))})",
                    (now, *[r["id"] for r in fresh]),
                )
                return jsonify(
                    commands=[
                        {"id": r["id"], "type": r["type"], "args": parse_json(r["args"], {}), "created_at": r["created_at"]}
                        for r in fresh
                    ]
                )
        if time.time() >= deadline:
            return jsonify(commands=[])
        time.sleep(0.4)


@bp.post("/agent/commands/<cid>/result")
@agent_required
def agent_command_result(cid):
    body = _json_body()
    row = db().one("SELECT * FROM commands WHERE id=? AND device_id=?", (cid, g.device_id))
    if not row:
        return jsonify(error="not_found"), 404
    if row["status"] in ("done", "failed", "expired", "timeout"):
        return jsonify(ok=True, status=row["status"])
    ok = bool(body.get("ok", True))
    error = body.get("error")
    status = "expired" if (not ok and str(error) == "expired") else ("done" if ok else "failed")
    result = {
        "ok": ok,
        "error": error,
        "data": body.get("data"),
    }
    db().execute(
        "UPDATE commands SET status=?, completed_at=?, result=? WHERE id=?",
        (status, now_ms(), json.dumps(result), cid),
    )
    result_row = db().one("SELECT * FROM commands WHERE id=?", (cid,))
    hub().push_to_device_clients(
        g.device_id, {"type": "command", "command": serialize_command(result_row)}
    )
    return jsonify(ok=True)


@bp.get("/agent/events")
@agent_required
def agent_events_history():
    limit = min(int(request.args.get("limit", 200) or 200), 500)
    before = int(request.args.get("before", 0) or 0)
    if before > 0:
        rows = db().all(
            "SELECT * FROM events WHERE device_id=? AND id < ? ORDER BY id DESC LIMIT ?",
            (g.device_id, before, limit),
        )
    else:
        rows = db().all(
            "SELECT * FROM events WHERE device_id=? ORDER BY id DESC LIMIT ?",
            (g.device_id, limit),
        )
    return jsonify(events=[serialize_event(r) for r in rows], has_more=len(rows) >= limit)


@bp.post("/agent/events")
@agent_required
def agent_events():
    body = _json_body()
    events = body.get("events") or []
    if not isinstance(events, list):
        return jsonify(error="bad_events"), 400
    required = bool(g.device.get("enc_required"))
    if required and any(not enc_field(ev.get("enc")) for ev in events if isinstance(ev, dict)):
        return jsonify(error="enc_required"), 400
    limit = int(cfg().data.get("events_per_minute", 600))
    accepted = []
    for ev in events[:500]:
        if not isinstance(ev, dict):
            continue
        if not limiter.allow(f"events:{g.device_id}", limit, 60):
            break
        accepted.append(ev)
    if events and not accepted:
        return jsonify(error="rate_limited", retry_after=60), 429
    events = accepted
    out = []
    for ev in events[:500]:
        enc = enc_field(ev.get("enc"))
        title = str(ev.get("title") or "")[:400]
        if not enc and not title:
            continue
        detail = None if enc else json.dumps(ev.get("detail")) if ev.get("detail") is not None else None
        tid = db().execute_returning_id(
            "INSERT INTO events (device_id, ts, category, severity, title, detail, enc) VALUES (?,?,?,?,?,?,?)",
            (
                g.device_id,
                int(ev.get("ts") or now_ms()),
                str(ev.get("category") or "system")[:32],
                str(ev.get("severity") or "info")[:16],
                "" if enc else title,
                detail,
                enc,
            ),
        )
        out.append({"id": tid, "ts": ev.get("ts"), "category": ev.get("category")})
    if out:
        rows = db().all(
            "SELECT * FROM events WHERE id >= ? AND device_id = ? ORDER BY id",
            (out[0]["id"], g.device_id),
        )
        hub().push_to_device_clients(
            g.device_id,
            {"type": "events", "events": [serialize_event(r) for r in rows]},
        )
        important = [
            e for e in events
            if not enc_field(e.get("enc"))
            and (str(e.get("severity")) in ("warning",) or str(e.get("category")) in ("security", "message"))
        ]
        if important:
            titles = "; ".join(str(e.get("title") or "")[:90] for e in important[:3])
            push(cfg(), str(important[0].get("title") or "Sentinel")[:100], titles)
    return jsonify(ok=True, count=len(out))


@bp.post("/agent/screenshots")
@agent_required
def agent_screenshots():
    f = request.files.get("file")
    if not f:
        return jsonify(error="file_required"), 400
    trigger = (request.form.get("trigger") or "manual")[:32]
    encrypted = flag(request.form.get("enc"))
    if g.device.get("enc_required") and not encrypted:
        return jsonify(error="enc_required"), 400
    if not limiter.allow(
        f"shots:{g.device_id}", int(cfg().data.get("screenshot_uploads_per_minute", 30)), 60
    ):
        return jsonify(error="rate_limited", retry_after=60), 429
    if trigger not in ("away", "activity") and not limiter.allow(
        f"shots-manual:{g.device_id}",
        int(cfg().data.get("manual_screenshots_per_30m", 5)),
        1800,
    ):
        return jsonify(
            error="rate_limited", retry_after=1800, detail="manual screenshot limit"
        ), 429
    raw = f.read()
    limit = int(cfg().data.get("max_upload_mb", 32)) * 1024 * 1024
    if len(raw) > limit:
        return jsonify(error="too_large"), 413

    sid = uuid.uuid4().hex
    folder = Path(cfg().shots_dir) / g.device_id
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / f"{sid}{'.enc' if encrypted else '.png'}"
    path.write_bytes(raw)

    row = {
        "id": sid,
        "device_id": g.device_id,
        "ts": int(request.form.get("ts") or now_ms()),
        "monitor": int(request.form.get("monitor") or 0),
        "monitor_name": (request.form.get("monitor_name") or "")[:128],
        "width": int(request.form.get("width") or 0),
        "height": int(request.form.get("height") or 0),
        "trigger": (request.form.get("trigger") or "manual")[:32],
        "path": str(path),
        "size": len(raw),
        "enc": "1" if encrypted else None,
    }
    db().execute(
        "INSERT INTO screenshots (id, device_id, ts, monitor, monitor_name, width, height, trigger, path, size, enc) "
        "VALUES (?,?,?,?,?,?,?,?,?,?,?)",
        (sid, row["device_id"], row["ts"], row["monitor"], row["monitor_name"], row["width"], row["height"], row["trigger"], row["path"], row["size"], row["enc"]),
    )
    trim_screenshots(g.device_id)
    hub().push_to_device_clients(
        g.device_id, {"type": "screenshots", "screenshots": [serialize_shot(row)]}
    )
    return jsonify(ok=True, screenshot=serialize_shot(row))


def trim_screenshots(device_id: str) -> int:
    max_count = int(cfg().data.get("screenshot_max_per_device", 500))
    max_bytes = int(cfg().data.get("screenshot_max_mb_per_device", 1024)) * 1024 * 1024
    rows = db().all(
        "SELECT id, path, size FROM screenshots WHERE device_id=? ORDER BY ts DESC, id DESC",
        (device_id,),
    )
    total = 0
    kept = 0
    removed = 0
    for row in rows:
        size = int(row.get("size") or 0)
        total += size
        if kept < max_count and total <= max_bytes:
            kept += 1
            continue
        try:
            Path(row["path"]).unlink(missing_ok=True)
        except OSError:
            pass
        db().execute("DELETE FROM screenshots WHERE id=?", (row["id"],))
        removed += 1
    return removed


@bp.post("/agent/state")
@agent_required
def agent_state():
    body = _json_body()
    if "away" in body:
        db().execute("UPDATE devices SET away=? WHERE id=?", (1 if body["away"] else 0, g.device_id))
    if "locked" in body:
        db().execute("UPDATE devices SET locked=? WHERE id=?", (1 if body["locked"] else 0, g.device_id))
    if "state" in body:
        db().execute("UPDATE devices SET state=? WHERE id=?", (json.dumps(body["state"]), g.device_id))
    if "settings" in body:
        db().execute("UPDATE devices SET settings=? WHERE id=?", (json.dumps(body["settings"]), g.device_id))
    if body.get("name"):
        db().execute("UPDATE devices SET name=? WHERE id=?", (str(body["name"]).strip()[:64], g.device_id))
    device = db().one("SELECT * FROM devices WHERE id=?", (g.device_id,))
    payload = serialize_device(device)
    hub().push_to_device_clients(g.device_id, {"type": "state", "device": payload})
    return jsonify(ok=True, device=payload)


@bp.get("/agent/phones")
@agent_required
def agent_phones():
    rows = db().all(
        "SELECT * FROM mobiles WHERE device_id = ? ORDER BY created_at", (g.device_id,)
    )
    return jsonify(phones=[serialize_phone(row) for row in rows])


@bp.delete("/agent/phones/<mid>")
@agent_required
def agent_remove_phone(mid):
    row = db().one("SELECT * FROM mobiles WHERE id = ? AND device_id = ?", (mid, g.device_id))
    if not row:
        return jsonify(error="not_found"), 404
    if row.get("is_primary"):
        return jsonify(error="primary_device"), 409
    db().execute("DELETE FROM mobiles WHERE id = ?", (mid,))
    db().audit(g.actor, "phone.removed", {"device": g.device_id, "phone": mid})
    hub().push_to_device_clients(g.device_id, {"type": "phones"})
    return jsonify(ok=True)


@bp.post("/agent/phones/<mid>/confirm")
@agent_required
def agent_confirm_phone(mid):
    row = db().one("SELECT * FROM mobiles WHERE id = ? AND device_id = ?", (mid, g.device_id))
    if not row:
        return jsonify(error="not_found"), 404
    if not limiter.allow(f"confirm:{mid}", 8, 60):
        return jsonify(error="too_many_attempts"), 429
    body = _json_body()
    code = str(body.get("code") or "").strip()
    if not verify_totp(g.device["totp_secret"], code):
        db().audit(g.actor, "phone.confirm_bad_code", {"device": g.device_id, "phone": mid})
        return jsonify(error="bad_code"), 403
    limiter.reset(f"confirm:{mid}")
    db().execute("UPDATE mobiles SET confirmed = 1 WHERE id = ?", (mid,))
    db().audit(g.actor, "phone.confirmed", {"device": g.device_id, "phone": mid})
    hub().push_to_device_clients(g.device_id, {"type": "phones"})
    return jsonify(ok=True, phone=serialize_phone(db().one("SELECT * FROM mobiles WHERE id=?", (mid,))))


@bp.patch("/agent/phones/<mid>")
@agent_required
def agent_patch_phone(mid):
    row = db().one("SELECT * FROM mobiles WHERE id = ? AND device_id = ?", (mid, g.device_id))
    if not row:
        return jsonify(error="not_found"), 404
    body = _json_body()
    if "name" in body:
        name = str(body.get("name") or "").strip()[:64]
        if name:
            db().execute("UPDATE mobiles SET name = ?, name_locked = 1 WHERE id = ?", (name, mid))
    if "primary" in body:
        if bool(body.get("primary")):
            db().execute("UPDATE mobiles SET is_primary = 0 WHERE device_id = ?", (g.device_id,))
            db().execute("UPDATE mobiles SET is_primary = 1 WHERE id = ?", (mid,))
        else:
            db().execute("UPDATE mobiles SET is_primary = 0 WHERE id = ?", (mid,))
    db().audit(g.actor, "phone.patch", {"device": g.device_id, "phone": mid, "body": body})
    hub().push_to_device_clients(g.device_id, {"type": "phones"})
    updated = db().one("SELECT * FROM mobiles WHERE id=?", (mid,))
    return jsonify(ok=True, phone=serialize_phone(updated))


@bp.post("/agent/screenshots/purge")
@agent_required
def agent_purge_screenshots():
    rows = db().all("SELECT id, path FROM screenshots WHERE device_id = ?", (g.device_id,))
    removed = 0
    for row in rows:
        try:
            path = Path(row["path"])
            if path.exists():
                path.unlink()
                removed += 1
        except OSError:
            pass
    db().execute("DELETE FROM screenshots WHERE device_id = ?", (g.device_id,))
    db().audit(g.actor, "screenshots.purged", {"device": g.device_id, "count": len(rows)})
    hub().push_to_device_clients(g.device_id, {"type": "shots", "screenshots": []})
    return jsonify(ok=True, purged=len(rows), removed=removed)


@bp.delete("/agent/device")
@agent_required
def agent_purge_device():
    device_id = g.device_id
    hub().push_to_device_clients(device_id, {"type": "unpaired"})
    shots = db().all("SELECT path FROM screenshots WHERE device_id = ?", (device_id,))
    for row in shots:
        try:
            path = Path(row["path"])
            if path.exists():
                path.unlink()
        except OSError:
            pass
    db().execute("DELETE FROM mobiles WHERE device_id = ?", (device_id,))
    db().execute("DELETE FROM commands WHERE device_id = ?", (device_id,))
    db().execute("DELETE FROM events WHERE device_id = ?", (device_id,))
    db().execute("DELETE FROM screenshots WHERE device_id = ?", (device_id,))
    db().execute("DELETE FROM messages WHERE device_id = ?", (device_id,))
    db().execute("DELETE FROM devices WHERE id = ?", (device_id,))
    db().audit(g.actor, "device.purged", {"device": device_id, "phones": len(shots)})
    return jsonify(ok=True)


@bp.post("/agent/link-request")
@agent_required
def agent_link_request():
    body = _json_body()
    rid = uuid.uuid4().hex
    code = new_pair_code()
    now = now_ms()
    ttl = int(cfg().data.get("pair_code_ttl_seconds", 600)) * 1000
    token = new_token()
    db().execute(
        "INSERT INTO pair_requests (id, code, device_id, device_name, platform, fingerprint, "
        "token_hash, status, created_at, expires_at, secret_tmp) VALUES (?,?,?,?,?,?,?,?,?,?,?)",
        (rid, code, g.device_id, g.device["name"], g.device.get("platform"), g.device.get("fingerprint"), hash_token(token), "pending", now, now + ttl, None),
    )
    return jsonify(request_id=rid, pair_code=code, expires_at=now + ttl, request_token=token,
                   server={"name": cfg().data["server_name"], "url": cfg().public_base()})


# --------------------------------------------------------------------------- mobile


@bp.get("/me")
@mobile_required
def me():
    device = g.device
    return jsonify(
        mobile={
            "id": g.mobile["id"],
            "name": g.mobile["name"],
            "created_at": g.mobile["created_at"],
            "last_seen": g.mobile["last_seen"],
            "primary": bool(g.mobile.get("is_primary")),
            "confirmed": bool(g.mobile.get("confirmed", 1)),
        },
        device=serialize_device(device, include_secret=True),
        totp_secret=device["totp_secret"],
        totp_seconds_remaining=totp_seconds_remaining(),
        server={"name": cfg().data["server_name"], "url": cfg().public_base(), "version": cfg().version},
    )


@bp.delete("/me")
@mobile_required
def remove_me():
    if g.mobile.get("is_primary"):
        return jsonify(error="primary_device"), 409
    db().execute("DELETE FROM mobiles WHERE id=?", (g.mobile["id"],))
    db().audit("mobile", "mobile.removed", {"mobile": g.mobile["id"], "device": g.device_id})
    return jsonify(ok=True)


@bp.get("/devices")
@mobile_required
def list_devices():
    rows = db().all("SELECT * FROM devices WHERE id IN (SELECT device_id FROM mobiles WHERE id=?)", (g.mobile["id"],))
    return jsonify(devices=[serialize_device(r) for r in rows])


@bp.get("/devices/<did>")
@mobile_required
def get_device(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    device = db().one("SELECT * FROM devices WHERE id=?", (did,))
    shot_count = db().one("SELECT COUNT(*) AS n FROM screenshots WHERE device_id=?", (did,))
    event_count = db().one("SELECT COUNT(*) AS n FROM events WHERE device_id=?", (did,))
    data = serialize_device(device, include_secret=True)
    data["counts"] = {"screenshots": shot_count["n"], "events": event_count["n"]}
    data["totp_seconds_remaining"] = totp_seconds_remaining()
    return jsonify(device=data)


@bp.patch("/devices/<did>")
@mobile_required
def patch_device(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    body = _json_body()
    if "name" in body:
        db().execute("UPDATE devices SET name=? WHERE id=?", (str(body["name"])[:64], did))
    if "settings" in body:
        current = device_settings(g.device)
        merged = {**current, **(body["settings"] or {})}
        db().execute("UPDATE devices SET settings=? WHERE id=?", (json.dumps(merged), did))
    db().audit("mobile", "device.patch", {"device": did, "body": body})
    device = db().one("SELECT * FROM devices WHERE id=?", (did,))
    return jsonify(ok=True, device=serialize_device(device, include_secret=True))


@bp.delete("/devices/<did>")
@mobile_required
def delete_device(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    db().execute("DELETE FROM mobiles WHERE device_id=?", (did,))
    db().execute("DELETE FROM devices WHERE id=?", (did,))
    db().audit("mobile", "device.removed", {"device": did})
    return jsonify(ok=True)


@bp.post("/devices/<did>/command")
@mobile_required
def create_command(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    body = _json_body()
    ctype = (body.get("type") or "").strip()
    if ctype not in ALLOWED_COMMANDS:
        return jsonify(error="unsupported_command"), 400
    settings = device_settings(g.device)
    code = str(body.get("code") or "").strip()

    needs_code = ctype in CODE_REQUIRED and settings.get("require_2fa_unlock", True)
    if ctype in ("shutdown", "restart") and settings.get("protect_power"):
        needs_code = True
    if needs_code:
        if not limiter.allow(f"code:{did}", 6, 60):
            db().audit("mobile", "command.rate_limited", {"device": did, "type": ctype})
            return jsonify(error="too_many_attempts"), 429
        if not verify_totp(g.device["totp_secret"], code):
            db().audit("mobile", "command.bad_code", {"device": did, "type": ctype})
            return jsonify(error="bad_code"), 403
        limiter.reset(f"code:{did}")

    args = body.get("args") or {}
    if not isinstance(args, dict):
        args = {}
    if code:
        args["code"] = code
    kind = "watch" if "SentinelWatch" in (request.headers.get("User-Agent") or "") else "phone"
    label = (g.mobile.get("name") or kind)[:48]
    command = issue_command(did, ctype, args, source=f"{kind}:{label}:{g.mobile['id'][:8]}")
    return jsonify(ok=True, command=command)


@bp.post("/devices/<did>/unlock")
@mobile_required
def unlock(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    body = _json_body()
    code = str(body.get("code") or "").strip()
    action = (body.get("action") or "unlock").strip()
    if action not in ("unlock", "away_disable"):
        return jsonify(error="bad_action"), 400
    if not limiter.allow(f"code:{did}", 6, 60):
        db().audit("mobile", "unlock.rate_limited", {"device": did})
        return jsonify(error="too_many_attempts"), 429
    if not verify_totp(g.device["totp_secret"], code):
        db().audit("mobile", "unlock.bad_code", {"device": did})
        return jsonify(error="bad_code"), 403
    limiter.reset(f"code:{did}")
    command = issue_command(did, action, {"code": code, "from_mobile": True}, source=f"mobile:{g.mobile['id'][:8]}")
    db().audit("mobile", "unlock.granted", {"device": did, "action": action})
    return jsonify(ok=True, command=command)


@bp.post("/totp/check")
@mobile_required
def totp_check():
    body = _json_body()
    code = str(body.get("code") or "").strip()
    if not limiter.allow(f"check:{g.device_id}", 8, 60):
        return jsonify(error="too_many_attempts"), 429
    ok = verify_totp(g.device["totp_secret"], code)
    if ok:
        limiter.reset(f"check:{g.device_id}")
    return jsonify(ok=ok, seconds_remaining=totp_seconds_remaining())


@bp.get("/devices/<did>/commands/<cid>")
@mobile_required
def get_command(did, cid):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    row = db().one("SELECT * FROM commands WHERE id=? AND device_id=?", (cid, did))
    if not row:
        return jsonify(error="not_found"), 404
    return jsonify(command=serialize_command(row))


@bp.get("/devices/<did>/commands")
@mobile_required
def list_commands(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    rows = db().all(
        "SELECT * FROM commands WHERE device_id=? ORDER BY created_at DESC LIMIT 40", (did,)
    )
    return jsonify(commands=[serialize_command(r) for r in rows])


@bp.get("/devices/<did>/events")
@mobile_required
def list_events(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    since = int(request.args.get("since", 0))
    before = int(request.args.get("before", 0))
    limit = min(int(request.args.get("limit", 100)), 500)
    category = request.args.get("category")
    sql = "SELECT * FROM events WHERE device_id=? AND id > ?"
    params = [did, since]
    if before > 0:
        sql += " AND id < ?"
        params.append(before)
    if category and category != "all":
        sql += " AND category=?"
        params.append(category)
    sql += " ORDER BY id DESC LIMIT ?"
    params.append(limit + 1)
    rows = db().all(sql, tuple(params))
    has_more = len(rows) > limit
    rows = rows[:limit]
    return jsonify(events=[serialize_event(r) for r in rows][::-1], has_more=has_more)


@bp.get("/devices/<did>/audit")
@mobile_required
def device_audit(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    limit = min(int(request.args.get("limit", 100)), 300)
    rows = db().all(
        "SELECT * FROM audit WHERE detail LIKE ? OR actor = ? ORDER BY id DESC LIMIT ?",
        (f"%{did}%", f"mobile:{g.mobile['id'][:8]}", limit),
    )
    audit = []
    for row in rows:
        audit.append(
            {
                "id": row["id"],
                "ts": row["ts"] * 1000,
                "actor": row["actor"],
                "action": row["action"],
                "detail": parse_json(row["detail"], row["detail"]),
            }
        )
    return jsonify(audit=audit)


@bp.get("/devices/<did>/export")
@mobile_required
def export_device(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    if not limiter.allow(f"export:{g.mobile['id']}", 6, 60):
        return jsonify(error="too_many_attempts"), 429

    what = (request.args.get("what") or "all").lower()
    fmt = (request.args.get("format") or "json").lower()
    if what not in ("events", "audit", "all"):
        return jsonify(error="bad_what"), 400
    if fmt not in ("json", "csv"):
        return jsonify(error="bad_format"), 400

    device = db().one("SELECT * FROM devices WHERE id = ?", (did,))
    events = []
    audit = []
    if what in ("events", "all"):
        for row in db().all("SELECT * FROM events WHERE device_id = ? ORDER BY id", (did,)):
            events.append(
                {
                    "id": row["id"],
                    "ts": row["ts"],
                    "category": row["category"],
                    "severity": row["severity"],
                    "title": row["title"],
                    "detail": parse_json(row["detail"], row["detail"]),
                }
            )
    if what in ("audit", "all"):
        for row in db().all(
            "SELECT * FROM audit WHERE detail LIKE ? ORDER BY id", (f"%{did}%",)
        ):
            audit.append(
                {
                    "id": row["id"],
                    "ts": row["ts"] * 1000,
                    "actor": row["actor"],
                    "action": row["action"],
                    "detail": parse_json(row["detail"], row["detail"]),
                }
            )

    stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    safe_name = "".join(c if c.isalnum() else "-" for c in (device["name"] if device else "device"))[:32]
    filename = f"sentinel-{safe_name}-{stamp}-{what}.{fmt}"

    if fmt == "json":
        payload = {
            "exported_at": datetime.now(timezone.utc).isoformat(),
            "server_version": cfg().version,
            "device": {
                "id": did,
                "name": device["name"] if device else "",
                "platform": device["platform"] if device else "",
            },
            "what": what,
            "events": events,
            "audit": audit,
        }
        body = json.dumps(payload, indent=2, ensure_ascii=False)
        response = current_app.response_class(body, mimetype="application/json")
    else:
        if what == "all":
            return jsonify(error="choose_events_or_audit"), 400
        buffer = io.StringIO()
        writer = csv.writer(buffer)
        if what == "events":
            writer.writerow(["id", "ts_utc", "category", "severity", "title", "detail"])
            for event in events:
                writer.writerow(
                    [
                        event["id"],
                        datetime.fromtimestamp(event["ts"] / 1000, timezone.utc).isoformat(),
                        event["category"],
                        event["severity"],
                        event["title"],
                        json.dumps(event["detail"], ensure_ascii=False),
                    ]
                )
        else:
            writer.writerow(["id", "ts_utc", "actor", "action", "detail"])
            for row in audit:
                writer.writerow(
                    [
                        row["id"],
                        datetime.fromtimestamp(row["ts"] / 1000, timezone.utc).isoformat(),
                        row["actor"],
                        row["action"],
                        json.dumps(row["detail"], ensure_ascii=False),
                    ]
                )
        response = current_app.response_class(buffer.getvalue(), mimetype="text/csv")

    response.headers["Content-Disposition"] = f'attachment; filename="{filename}"'
    response.headers["Cache-Control"] = "private, no-store"
    db().audit(g.actor, "device.exported", {"device": did, "what": what, "format": fmt})
    return response


@bp.get("/devices/<did>/phones")
@mobile_required
def list_phones(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    rows = db().all(
        "SELECT * FROM mobiles WHERE device_id = ? ORDER BY created_at", (did,)
    )
    return jsonify(
        phones=[serialize_phone(row, current=row["id"] == g.mobile["id"]) for row in rows]
    )


@bp.delete("/devices/<did>/phones/<mid>")
@mobile_required
def remove_phone(did, mid):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    if mid == g.mobile["id"]:
        return jsonify(error="cannot_remove_current"), 400
    row = db().one("SELECT * FROM mobiles WHERE id = ? AND device_id = ?", (mid, did))
    if not row:
        return jsonify(error="not_found"), 404
    db().execute("DELETE FROM mobiles WHERE id = ?", (mid,))
    db().audit(g.actor, "phone.removed", {"device": did, "phone": mid})
    hub().push_to_device_clients(did, {"type": "phones"})
    return jsonify(ok=True)


@bp.post("/devices/<did>/companions")
@mobile_required
def create_companion(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    body = _json_body()
    kind = str(body.get("kind") or "").strip().lower()
    if kind != "watch":
        return jsonify(error="unsupported_kind"), 400
    if not limiter.allow(f"companion:{g.mobile['id']}", 5, 3600):
        return jsonify(error="rate_limited", retry_after=3600), 429
    name = str(body.get("name") or "").strip()[:64] or "Watch"
    token = new_token()
    mid = uuid.uuid4().hex
    now = now_ms()
    db().execute(
        "INSERT INTO mobiles (id, name, device_id, token_hash, created_at, last_seen, uid, "
        "confirmed, app_version, kind) VALUES (?,?,?,?,?,?,NULL,1,'',?)",
        (mid, name, did, hash_token(token), now, now, kind),
    )
    db().audit(g.actor, "companion.created", {"device": did, "mobile": mid, "kind": kind, "name": name})
    hub().push_to_device_clients(did, {"type": "phones"})
    device = db().one("SELECT * FROM devices WHERE id = ?", (did,)) or g.device
    return jsonify(
        mobile_token=token,
        mobile_id=mid,
        device_id=did,
        enc_required=bool(device.get("enc_required")),
    )


@bp.post("/devices/<did>/message")
@mobile_required
def send_message(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    body = _json_body()
    enc = enc_field(body.get("enc"))
    text = str(body.get("body") or body.get("text") or "").strip()
    if not enc and not text:
        return jsonify(error="body_required"), 400
    if not enc and g.device.get("enc_required"):
        return jsonify(error="enc_required"), 400
    kind = str(body.get("kind") or "toast").strip()[:16]
    if kind not in ("toast", "dialog", "alert", "banner", "fullscreen"):
        kind = "toast"
    title = str(body.get("title") or "Message from your phone").strip()[:120]
    allow_reply = bool(body.get("allow_reply"))
    mid = uuid.uuid4().hex
    now = now_ms()
    db().execute(
        "INSERT INTO messages (id, device_id, direction, kind, title, body, reply, allow_reply, status, created_at, updated_at, enc) "
        "VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
        (
            mid, did, "to_pc", kind,
            None if enc else title,
            None if enc else text[:2000],
            None, 1 if allow_reply else 0, "sent", now, now, enc,
        ),
    )
    args = {
        "message_id": mid,
        "kind": kind,
        "allow_reply": allow_reply,
    }
    if enc:
        args["enc"] = enc
    else:
        args["title"] = title
        args["text"] = text[:2000]
    command = issue_command(
        did,
        "message",
        args,
        source=f"mobile:{g.mobile['id'][:8]}",
    )
    push(cfg(), "Sentinel message", "Encrypted message" if enc else text[:400])
    row = db().one("SELECT * FROM messages WHERE id = ?", (mid,))
    return jsonify(ok=True, message=serialize_message(row), command=command)


@bp.get("/devices/<did>/messages")
@mobile_required
def list_messages(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    limit = min(int(request.args.get("limit", 50)), 200)
    rows = db().all(
        "SELECT * FROM messages WHERE device_id = ? ORDER BY created_at DESC LIMIT ?", (did, limit)
    )
    return jsonify(messages=[serialize_message(r) for r in rows])


@bp.post("/agent/messages/<mid>/reply")
@agent_required
def message_reply(mid):
    row = db().one("SELECT * FROM messages WHERE id = ? AND device_id = ?", (mid, g.device_id))
    if not row:
        return jsonify(error="not_found"), 404
    body = _json_body()
    enc = enc_field(body.get("enc"))
    reply = str(body.get("reply") or "").strip()[:2000]
    if not enc and reply and g.device.get("enc_required"):
        return jsonify(error="enc_required"), 400
    if enc:
        reply = ""
    status = str(body.get("status") or ("replied" if (reply or enc) else "dismissed"))[:16]
    now = now_ms()
    db().execute(
        "UPDATE messages SET reply = ?, status = ?, updated_at = ?, enc = ? WHERE id = ?",
        (reply or None, status, now, enc, mid),
    )
    updated = db().one("SELECT * FROM messages WHERE id = ?", (mid,))
    hub().push_to_device_clients(
        g.device_id, {"type": "message", "message": serialize_message(updated)}
    )
    if reply:
        event_id = db().execute_returning_id(
            "INSERT INTO events (device_id, ts, category, severity, title, detail) VALUES (?,?,?,?,?,?)",
            (
                g.device_id,
                now,
                "message",
                "notice",
                f"Reply from {row.get('title') or 'PC'}",
                json.dumps({"reply": reply, "message_id": mid}),
            ),
        )
        event_row = db().one("SELECT * FROM events WHERE id = ?", (event_id,))
        hub().push_to_device_clients(
            g.device_id, {"type": "events", "events": [serialize_event(event_row)]}
        )
        push(cfg(), f"Reply from {g.device['name']}", reply[:380], tags="speech_balloon")
    db().audit(g.actor, "message.reply", {"message": mid, "status": status})
    return jsonify(ok=True, message=serialize_message(updated))


@bp.get("/devices/<did>/screenshots")
@mobile_required
def list_shots(did):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    limit = min(int(request.args.get("limit", 60)), 200)
    before = int(request.args.get("before", 0))
    sql = "SELECT * FROM screenshots WHERE device_id=?"
    params = [did]
    if before > 0:
        sql += " AND ts < ?"
        params.append(before)
    sql += " ORDER BY ts DESC LIMIT ?"
    params.append(limit + 1)
    rows = db().all(sql, tuple(params))
    has_more = len(rows) > limit
    rows = rows[:limit]
    return jsonify(screenshots=[serialize_shot(r) for r in rows], has_more=has_more)


@bp.get("/devices/<did>/screenshots/<sid>/file")
@mobile_required
def shot_file(did, sid):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    row = db().one("SELECT * FROM screenshots WHERE id=? AND device_id=?", (sid, did))
    if not row or not Path(row["path"]).exists():
        return jsonify(error="not_found"), 404
    response = send_file(
        row["path"],
        mimetype="application/octet-stream" if row.get("enc") else "image/png",
        max_age=0,
    )
    response.headers["Cache-Control"] = "private, no-store"
    return response


@bp.delete("/devices/<did>/screenshots/<sid>")
@mobile_required
def shot_delete(did, sid):
    if did != g.device_id:
        return jsonify(error="forbidden"), 403
    row = db().one("SELECT * FROM screenshots WHERE id=? AND device_id=?", (sid, did))
    if not row:
        return jsonify(error="not_found"), 404
    try:
        Path(row["path"]).unlink(missing_ok=True)
    except OSError:
        pass
    db().execute("DELETE FROM screenshots WHERE id=?", (sid,))
    return jsonify(ok=True)
