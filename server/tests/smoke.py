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


def call(method, path, body=None, token=None, expect=200, headers=None):
    data = None
    hdrs = {"Content-Type": "application/json"}
    if headers:
        hdrs.update(headers)
    if token:
        hdrs["Authorization"] = f"Bearer {token}"
    if body is not None:
        data = json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, headers=hdrs, method=method)
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

    req = call("POST", "/api/v1/pair/request", {"device_name": "SMOKE-PC", "platform": "windows", "agent_version": "test", "enc_required": False})
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

    phones = call("GET", "/api/v1/agent/phones", token=agent)
    check("phones listed", bool(phones and phones["phones"]), str(phones))
    mid = phones["phones"][0]["id"]
    pending = phones["phones"][0].get("confirmed") is False
    check("new phone is pending", pending, str(phones["phones"][0]))
    conf = call("POST", f"/api/v1/agent/phones/{mid}/confirm", {"code": totp_now(totp_secret)}, agent)
    check("phone confirmed", bool(conf and conf.get("ok")), str(conf))

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

    # encrypted device: relay stores only opaque ciphertext
    ereq = call("POST", "/api/v1/pair/request",
                {"device_name": "SMOKE-ENC-PC", "platform": "windows", "agent_version": "test",
                 "enc_required": True})
    check("encrypted pair request", bool(ereq and ereq.get("pair_code")), str(ereq))
    erid, ecode, ertoken = ereq["request_id"], ereq["pair_code"], ereq["request_token"]

    elookup = call("POST", "/api/v1/pair/lookup", {"pair_code": ecode})
    check("enc_required exposed on lookup", bool(elookup and elookup.get("enc_required") is True), str(elookup))

    eappr = call("POST", "/api/v1/pair/approve",
                 {"request_id": erid, "pair_code": ecode, "mobile_name": "Enc Phone"})
    check("enc_required returned on approve",
          bool(eappr and eappr.get("enc_required") is True
               and eappr.get("device", {}).get("enc_required") is True), str(eappr))
    emobile = eappr["mobile_token"]
    edid = eappr["device"]["id"]
    est = call("GET", f"/api/v1/pair/status?request_id={erid}&token={ertoken}")
    eagent = est["agent_token"]
    ephones = call("GET", "/api/v1/agent/phones", token=eagent)
    call("POST", f"/api/v1/agent/phones/{ephones['phones'][0]['id']}/confirm",
         {"code": totp_now(eappr["totp_secret"])}, eagent)

    plain_ev = call("POST", "/api/v1/agent/events",
                    {"events": [{"ts": int(time.time() * 1000), "category": "window",
                                 "severity": "info", "title": "plaintext leak"}]},
                    eagent, expect=400)
    check("enc_required rejects plaintext events", plain_ev is not None)

    mixed = call("POST", "/api/v1/agent/events",
                 {"events": [{"title": "plaintext leak"},
                             {"enc": "ZW5jLWV2ZW50", "category": "window"}]},
                 eagent, expect=400)
    check("enc_required rejects mixed batch", mixed is not None)

    evs = call("GET", f"/api/v1/devices/{edid}/events?since=0", token=emobile)
    check("rejected batch stores nothing", bool(evs and evs["events"] == []), str(evs))

    opaque = "ZW5jLWV2ZW50LWNpcGhlcnRleHQ="
    enc_ev = call("POST", "/api/v1/agent/events",
                  {"events": [{"ts": int(time.time() * 1000), "category": "window",
                               "severity": "info", "title": "must be dropped",
                               "detail": {"secret": "dropped"}, "enc": opaque}]},
                  eagent)
    check("encrypted event accepted", bool(enc_ev and enc_ev.get("count") == 1), str(enc_ev))

    evs = call("GET", f"/api/v1/devices/{edid}/events?since=0", token=emobile)
    enc_row = evs["events"][-1] if evs and evs.get("events") else {}
    check("encrypted event stored", enc_row.get("enc") == opaque, str(enc_row))
    check("encrypted event title empty", enc_row.get("title") == "", str(enc_row))
    check("encrypted event detail cleared", enc_row.get("detail") is None, str(enc_row))

    try:
        upload("/api/v1/agent/screenshots", eagent,
               {"monitor": 1, "width": 10, "height": 10, "trigger": "manual"}, b"\x89PNGplain")
        plain_shot_ok = False
    except urllib.error.HTTPError as exc:
        plain_shot_ok = exc.code == 400
    check("enc_required rejects plaintext screenshots", plain_shot_ok)

    cipher = bytes(range(32)) + b"not-a-png-ciphertext"
    eshot = upload("/api/v1/agent/screenshots", eagent,
                   {"monitor": 1, "monitor_name": "ENC", "width": 10, "height": 10,
                    "trigger": "manual", "enc": "1"}, cipher)
    check("encrypted screenshot upload", bool(eshot and eshot["screenshot"].get("enc") is True), str(eshot))
    esid = eshot["screenshot"]["id"]

    eshots = call("GET", f"/api/v1/devices/{edid}/screenshots", token=emobile)
    check("encrypted screenshot listed",
          bool(eshots and eshots["screenshots"] and eshots["screenshots"][0].get("enc") is True),
          str(eshots)[:300])

    req = urllib.request.Request(f"{BASE}/api/v1/devices/{edid}/screenshots/{esid}/file?token={emobile}")
    with urllib.request.urlopen(req, timeout=20) as resp:
        eblob = resp.read()
        etype = resp.headers.get("Content-Type", "")
    check("encrypted screenshot bytes match ciphertext", eblob == cipher, f"{len(eblob)} bytes")
    check("encrypted screenshot content type", etype.startswith("application/octet-stream"), etype)

    enc_msg = call("POST", f"/api/v1/devices/{edid}/message",
                   {"enc": "ZW5jLW1zZw==", "kind": "toast", "allow_reply": True}, emobile)
    enc_args = (enc_msg or {}).get("command", {}).get("args", {})
    check("encrypted message accepted",
          bool(enc_msg and enc_msg["message"].get("enc") == "ZW5jLW1zZw=="
               and enc_msg["message"].get("title") is None
               and enc_msg["message"].get("body") is None), str(enc_msg)[:300])
    check("encrypted message forwards enc",
          enc_args.get("enc") == "ZW5jLW1zZw==" and "text" not in enc_args
          and enc_args.get("allow_reply") is True, str(enc_args))
    enc_mid = enc_msg["message"]["id"]

    plain_msg = call("POST", f"/api/v1/devices/{edid}/message", {"body": "plaintext leak"},
                     emobile, expect=400)
    check("enc_required rejects plaintext messages", plain_msg is not None)

    enc_reply = call("POST", f"/api/v1/agent/messages/{enc_mid}/reply",
                     {"enc": "ZW5jLXJlcGx5", "status": "replied"}, eagent)
    check("encrypted reply stored",
          bool(enc_reply and enc_reply["message"].get("enc") == "ZW5jLXJlcGx5"
               and enc_reply["message"].get("reply") is None), str(enc_reply)[:300])

    comp = call("POST", f"/api/v1/devices/{did}/companions",
                {"name": "Smoke Watch", "kind": "watch"}, mobile)
    check("companion watch created",
          bool(comp and comp.get("mobile_token") and comp.get("mobile_id")
               and comp.get("device_id") == did and comp.get("enc_required") is False), str(comp)[:300])
    check("companion exposes no key",
          bool(comp and not any(k in comp for k in ("key", "data_key", "dek"))))
    watch = comp["mobile_token"]
    wme = call("GET", "/api/v1/me", token=watch, headers={"User-Agent": "SentinelWatch"})
    check("companion token works", bool(wme and wme["device"]["id"] == did), str(wme)[:200])
    listed = call("GET", f"/api/v1/devices/{did}/phones", token=mobile)
    watch_row = next((p for p in listed["phones"] if p["id"] == comp["mobile_id"]), {}) if listed else {}
    check("companion stays watch",
          watch_row.get("kind") == "watch" and watch_row.get("confirmed") is True, str(watch_row))

    # second tenant: its token must not reach the first device
    req2 = call("POST", "/api/v1/pair/request",
                {"device_name": "SMOKE-PC-2", "platform": "windows", "agent_version": "test", "enc_required": False})
    rid2, code2, rtoken2 = req2["request_id"], req2["pair_code"], req2["request_token"]
    appr2 = call("POST", "/api/v1/pair/approve",
                 {"request_id": rid2, "pair_code": code2, "mobile_name": "Other Phone"})
    mobile2 = appr2["mobile_token"]
    did2 = appr2["device"]["id"]
    secret2 = appr2["totp_secret"]
    st2 = call("GET", f"/api/v1/pair/status?request_id={rid2}&token={rtoken2}")
    agent2 = st2["agent_token"]
    phones2 = call("GET", "/api/v1/agent/phones", token=agent2)
    call("POST", f"/api/v1/agent/phones/{phones2['phones'][0]['id']}/confirm",
         {"code": totp_now(secret2)}, agent2)

    cross = call("GET", f"/api/v1/devices/{did}/events?since=0", token=mobile2, expect=403)
    check("cross-device events blocked", cross is not None)
    cross = call("GET", f"/api/v1/devices/{did}/screenshots", token=mobile2, expect=403)
    check("cross-device screenshots blocked", cross is not None)
    cross = call("GET", f"/api/v1/devices/{did}/commands", token=mobile2, expect=403)
    check("cross-device commands blocked", cross is not None)
    cross = call("DELETE", f"/api/v1/devices/{did}/screenshots/{sid}", token=mobile2, expect=403)
    check("cross-device screenshot delete blocked", cross is not None)
    cross = call("GET", f"/api/v1/devices/{did}/screenshots/{sid}/file", token=mobile2, expect=403)
    check("cross-device screenshot file blocked", cross is not None)
    cross = call("POST", f"/api/v1/devices/{did}/companions",
                 {"name": "Sneaky Watch", "kind": "watch"}, mobile2, expect=403)
    check("cross-device companions blocked", cross is not None)

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
