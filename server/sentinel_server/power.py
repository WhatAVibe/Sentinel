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


def check_metric_alerts(cfg, db, hub, device_id: str, name: str, metrics: dict) -> None:
    disk_limit = int(cfg.data.get("disk_alert_percent", 10) or 0)
    battery_limit = int(cfg.data.get("battery_alert_percent", 15) or 0)
    if disk_limit <= 0 and battery_limit <= 0:
        return

    row = db.one("SELECT disk_alerted, battery_alerted FROM devices WHERE id = ?", (device_id,)) or {}
    total = float(metrics.get("disk_total_mb") or 0)
    free = float(metrics.get("disk_free_mb") or 0)
    low_disk = disk_limit > 0 and total > 0 and free * 100.0 / total <= disk_limit

    try:
        level = int(metrics.get("battery_percent", -1))
    except (TypeError, ValueError):
        level = -1
    charging = bool(metrics.get("battery_charging"))
    low_battery = battery_limit > 0 and 0 <= level <= battery_limit and not charging

    if low_disk and not row.get("disk_alerted"):
        db.execute("UPDATE devices SET disk_alerted = 1 WHERE id = ?", (device_id,))
        percent = round(free * 100.0 / total, 1)
        create_event(
            db,
            hub,
            cfg,
            device_id,
            f"Low disk space on {name}",
            {
                "free_mb": round(free),
                "total_mb": round(total),
                "free_percent": percent,
                "message": f"{name} has {percent}% free space left "
                           f"({round(free / 1024)} GB of {round(total / 1024)} GB).",
            },
        )
    elif not low_disk and row.get("disk_alerted"):
        db.execute("UPDATE devices SET disk_alerted = 0 WHERE id = ?", (device_id,))

    if low_battery and not row.get("battery_alerted"):
        db.execute("UPDATE devices SET battery_alerted = 1 WHERE id = ?", (device_id,))
        create_event(
            db,
            hub,
            cfg,
            device_id,
            f"Battery low on {name}",
            {
                "battery_percent": level,
                "message": f"{name} is on battery at {level}% and not charging.",
            },
        )
    elif not low_battery and row.get("battery_alerted"):
        db.execute("UPDATE devices SET battery_alerted = 0 WHERE id = ?", (device_id,))
