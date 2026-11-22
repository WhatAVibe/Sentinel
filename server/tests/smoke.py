"""End-to-end smoke test for the relay server. Usage: python tests/smoke.py [base_url]"""

import json
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from sentinel_server.security import totp_now  # noqa: E402

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8799"
FAILS = []


def call(method, path, body=None, token=None, expect=200):
    data = None
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    if body is not None:
        data = json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=40) as resp:
            raw = resp.read()
            status = resp.status
    except urllib.error.HTTPError as exc:
        raw = exc.read()
        status = exc.code
    except Exception as exc:  # noqa: BLE001
        FAILS.append(f"{method} {path} -> transport {exc}")
        return None
    if status != expect:
        FAILS.append(f"{method} {path} -> {status} (expected {expect}): {raw[:300]!r}")
        return None
    try:
        return json.loads(raw)
    except ValueError:
        return raw


def upload(path, token, fields, file_bytes):
    boundary = uuid.uuid4().hex
    parts = []
    for k, v in fields.items():
        parts.append(
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n".encode()
        )
    parts.append(
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"shot.png\"\r\n"
        f"Content-Type: image/png\r\n\r\n".encode() + file_bytes + b"\r\n"
    )
    parts.append(f"--{boundary}--\r\n".encode())
    body = b"".join(parts)
    req = urllib.request.Request(
        BASE + path,
        data=body,
        headers={
            "Content-Type": f"multipart/form-data; boundary={boundary}",
            "Authorization": f"Bearer {token}",
        },
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=40) as resp:
        return json.loads(resp.read())


def check(name, cond, extra=""):
    print(("  ok   " if cond else "  FAIL ") + name + (f"  {extra}" if extra and not cond else ""))
    if not cond:
        FAILS.append(name)


def main():
    health = call("GET", "/api/v1/health")
    check("health", bool(health and health.get("ok")), str(health))

    req = call("POST", "/api/v1/pair/request", {"device_name": "SMOKE-PC", "platform": "windows", "agent_version": "test"})
    check("pair request", bool(req and req.get("pair_code")), str(req))
    rid, code, rtoken = req["request_id"], req["pair_code"], req["request_token"]

    st = call("GET", f"/api/v1/pair/status?request_id={rid}&token={rtoken}")
    check("pair pending", st and st.get("status") == "pending", str(st))

    lookup = call("POST", "/api/v1/pair/lookup", {"pair_code": code})
    check("pair lookup by code", bool(lookup and lookup.get("request_id") == rid), str(lookup))

    lookmiss = call("POST", "/api/v1/pair/lookup", {"pair_code": "ZZZZZZ"}, expect=404)
    check("pair lookup rejects unknown", lookmiss is not None)

    bad = call("POST", "/api/v1/pair/approve", {"request_id": rid, "pair_code": "ZZZZZZ"}, expect=403)
    check("bad pair code rejected", bad is not None)

    appr = call("POST", "/api/v1/pair/approve", {"request_id": rid, "pair_code": code, "mobile_name": "Smoke Phone"})
    check("pair approve", bool(appr and appr.get("mobile_token")), str(appr))
    mobile = appr["mobile_token"]
    did = appr["device"]["id"]
    totp_secret = appr["totp_secret"]

    st = call("GET", f"/api/v1/pair/status?request_id={rid}&token={rtoken}")
    check("agent claims token", bool(st and st.get("agent_token")), str(st))
    agent = st["agent_token"]

    hb = call("POST", "/api/v1/agent/heartbeat", {"away": True, "locked": True, "metrics": {"cpu_percent": 3.2}, "state": {"monitors": 2}}, agent)
    check("heartbeat", bool(hb and hb["device"]["online"]))

    devices = call("GET", "/api/v1/devices", token=mobile)
    check("device list online", devices and devices["devices"][0]["online"] and devices["devices"][0]["away"], str(devices))
    check("device secret hidden from list", "totp_secret" not in devices["devices"][0])

    me = call("GET", "/api/v1/me", token=mobile)
    check("me", bool(me and me["device"]["id"] == did))

    cmd = call("POST", f"/api/v1/devices/{did}/command", {"type": "screenshot"}, mobile)
    check("queue command", bool(cmd and cmd["command"]["status"] == "pending"), str(cmd))
    cid = cmd["command"]["id"]

    poll = call("GET", "/api/v1/agent/commands?wait=2", token=agent)
    got = poll["commands"][0] if poll and poll.get("commands") else None
    check("agent receives command", bool(got and got["id"] == cid), str(poll))

    res = call("POST", f"/api/v1/agent/commands/{cid}/result", {"ok": True, "data": {"monitors": 2}}, agent)
    check("command result", bool(res and res.get("ok")))

    state = call("GET", f"/api/v1/devices/{did}/commands/{cid}", token=mobile)
    check("command done", state and state["command"]["status"] == "done", str(state))

    ev = call(
        "POST",
        "/api/v1/agent/events",
        {"events": [
            {"ts": int(time.time() * 1000), "category": "window", "severity": "info", "title": "Notepad", "detail": {"pid": 123}},
            {"ts": int(time.time() * 1000), "category": "usb", "severity": "notice", "title": "USB inserted"},
        ]},
        agent,
    )
    check("events accepted", bool(ev and ev["count"] == 2), str(ev))

    evs = call("GET", f"/api/v1/devices/{did}/events?since=0", token=mobile)
    check("events listed", evs and len(evs["events"]) == 2, str(evs))
    check("event detail json", evs["events"][0]["detail"] == {"pid": 123}, str(evs["events"][0]))

    shot = upload(f"/api/v1/agent/screenshots", agent, {"monitor": 1, "monitor_name": "DISPLAY1", "width": 1920, "height": 1080, "trigger": "manual"}, b"\x89PNG\r\n\x1a\nfake")
    check("screenshot upload", bool(shot and shot.get("screenshot", {}).get("id")), str(shot))
    sid = shot["screenshot"]["id"]

    shots = call("GET", f"/api/v1/devices/{did}/screenshots", token=mobile)
    check("screenshot list", shots and len(shots["screenshots"]) == 1)

    req = urllib.request.Request(f"{BASE}/api/v1/devices/{did}/screenshots/{sid}/file?token={mobile}")
    with urllib.request.urlopen(req, timeout=20) as resp:
        blob = resp.read()
    check("screenshot bytes", blob.startswith(b"\x89PNG"))

    code_now = totp_now(totp_secret)
    chk = call("POST", "/api/v1/totp/check", {"code": code_now}, mobile)
    check("totp valid", bool(chk and chk.get("ok")), str(chk))
    chk = call("POST", "/api/v1/totp/check", {"code": "000000"}, mobile)
    check("totp invalid", chk and chk.get("ok") is False, str(chk))

    denied = call("POST", f"/api/v1/devices/{did}/command", {"type": "unlock"}, mobile, expect=403)
    check("unlock without code denied", denied is not None)

    unlocked = call("POST", f"/api/v1/devices/{did}/unlock", {"code": totp_now(totp_secret), "action": "away_disable"}, mobile)
    check("unlock accepted", bool(unlocked and unlocked.get("ok")), str(unlocked))

    poll = call("GET", "/api/v1/agent/commands?wait=2", token=agent)
    got = poll["commands"][0] if poll and poll.get("commands") else None
    check("unlock reaches agent with code", bool(got and got["type"] == "away_disable" and got["args"].get("code")), str(poll))

    bad_type = call("POST", f"/api/v1/devices/{did}/command", {"type": "format_disk"}, mobile, expect=400)
    check("unknown command rejected", bad_type is not None)

    audit = call("GET", f"/api/v1/devices/{did}/audit", token=mobile)
    check("audit log", bool(audit and len(audit.get("audit", [])) > 0), str(audit)[:200])

    msg = call(
        "POST",
        f"/api/v1/devices/{did}/message",
        {"body": "Smoke message", "title": "Test", "kind": "dialog", "allow_reply": True},
        mobile,
    )
    check("message queued", bool(msg and msg.get("message", {}).get("id")), str(msg)[:200])
    mid = msg["message"]["id"]

    poll = call("GET", "/api/v1/agent/commands?wait=2", token=agent)
    got = poll["commands"][0] if poll and poll.get("commands") else None
    check("message reaches agent", bool(got and got["type"] == "message" and
                                        got["args"].get("message_id") == mid), str(poll))

    reply = call("POST", f"/api/v1/agent/messages/{mid}/reply",
                 {"reply": "pong", "status": "replied"}, agent)
    check("agent reply accepted", bool(reply and reply.get("ok") and
                                       reply.get("message", {}).get("reply") == "pong"), str(reply))

    evs = call("GET", f"/api/v1/devices/{did}/events?since=0", token=mobile)
    check("reply logged as event", any(e["category"] == "message" for e in evs["events"]), str(evs)[:200])

    exit_denied = call("POST", f"/api/v1/devices/{did}/command", {"type": "agent_exit"}, mobile, expect=403)
    check("agent_exit requires code", exit_denied is not None)

    exit_ok = call("POST", f"/api/v1/devices/{did}/command",
                   {"type": "agent_exit", "code": totp_now(totp_secret)}, mobile)
    check("agent_exit authorized", bool(exit_ok and exit_ok.get("ok")), str(exit_ok)[:200])

    poll = call("GET", "/api/v1/agent/commands?wait=2", token=agent)
    got = poll["commands"][0] if poll and poll.get("commands") else None
    check("exit reaches agent", bool(got and got["type"] == "agent_exit" and got["args"].get("code")),
          str(poll))

    noauth = call("GET", "/api/v1/devices", expect=401)
    check("auth enforced", noauth is not None)

    print()
    if FAILS:
        print(f"FAILED ({len(FAILS)}):")
        for f in FAILS:
            print(" -", f)
        sys.exit(1)
    print("all server checks passed")


if __name__ == "__main__":
    main()
