import json
import time

from .notify import push

OFFLINE_CATEGORY = "power"


def now_ms() -> int:
    return int(time.time() * 1000)


def create_event(db, hub, cfg, device_id: str, title: str, detail: dict, severity: str = "warning"):
    ts = now_ms()
    event_id = db.execute_returning_id(
        "INSERT INTO events (device_id, ts, category, severity, title, detail) VALUES (?,?,?,?,?,?)",
        (device_id, ts, OFFLINE_CATEGORY, severity, title, json.dumps(detail)),
    )
    event = {
        "id": event_id,
        "ts": ts,
        "category": OFFLINE_CATEGORY,
        "severity": severity,
        "title": title,
        "detail": detail,
    }
    hub.push_to_device_clients(device_id, {"type": "events", "events": [event]})
    push(cfg, title, detail.get("message") or title, tags="power")
    return event


def check_offline_devices(cfg, db, hub) -> None:
    threshold = int(cfg.data.get("offline_alert_seconds", 300)) * 1000
    if threshold <= 0:
        return
    now = now_ms()
    rows = db.all(
        "SELECT id, name, last_seen, offline_alerted FROM devices "
        "WHERE last_seen IS NOT NULL AND offline_alerted = 0"
    )
    for row in rows:
        offline_for = now - (row["last_seen"] or 0)
        if offline_for < threshold:
            continue
        db.execute("UPDATE devices SET offline_alerted = 1 WHERE id = ?", (row["id"],))
        minutes = max(1, round(offline_for / 60000))
        create_event(
            db,
            hub,
            cfg,
            row["id"],
            f"{row['name']} went offline",
            {
                "offline_seconds": round(offline_for / 1000),
                "minutes": minutes,
                "message": f"{row['name']} has been unreachable for {minutes} minute(s). "
                           "It may have shut down, slept, or lost power.",
            },
        )
