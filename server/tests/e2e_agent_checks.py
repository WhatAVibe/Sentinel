"""End-to-end checks against a live agent through the relay API."""

import json
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from pcm_server.security import totp_now  # noqa: E402

SESSION = json.loads(Path(sys.argv[1]).read_text())
BASE = SESSION["server"]
TOKEN = SESSION["mobile_token"]
DEVICE = SESSION["device_id"]
FAILS = []


def call(method, path, body=None, expect=200):
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json", "Authorization": f"Bearer {TOKEN}"}
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=25) as response:
            return response.status, json.loads(response.read() or b"{}")
    except urllib.error.HTTPError as exc:
        return exc.code, json.loads(exc.read() or b"{}")


def check(name, ok, detail=""):
    print(("  ok   " if ok else "  FAIL ") + name + (f"  {detail}" if detail and not ok else ""))
    if not ok:
        FAILS.append(name)


status, me = call("GET", "/api/v1/me")
check("me endpoint", status == 200 and me["device"]["id"] == DEVICE, str(me)[:200])

status, devices = call("GET", "/api/v1/devices")
online = bool(devices.get("devices") and devices["devices"][0]["online"])
deadline = time.time() + 40
while not online and time.time() < deadline:
    time.sleep(2)
    status, devices = call("GET", "/api/v1/devices")
    online = bool(devices.get("devices") and devices["devices"][0]["online"])
check("agent online", online, str(devices)[:300])

check("agent heartbeat metrics", bool(devices["devices"][0]["metrics"].get("hostname")), str(devices["devices"][0]))

shots_before = len(call("GET", f"/api/v1/devices/{DEVICE}/screenshots")[1].get("screenshots", []))
status, queued = call("POST", f"/api/v1/devices/{DEVICE}/command", {"type": "screenshot"})
check("screenshot queued", status == 200 and queued["command"]["status"] in ("pending", "delivered"), str(queued)[:200])
cid = queued["command"]["id"] if status == 200 else ""

result = None
deadline = time.time() + 40
while time.time() < deadline:
    _, data = call("GET", f"/api/v1/devices/{DEVICE}/commands/{cid}")
    result = data.get("command")
    if result and result["status"] in ("done", "failed", "timeout"):
        break
    time.sleep(1.5)
check("screenshot executed", bool(result and result["status"] == "done"), str(result)[:300])

_, shots = call("GET", f"/api/v1/devices/{DEVICE}/screenshots")
check("screenshot uploaded", len(shots.get("screenshots", [])) > shots_before, str(shots)[:200])

_, events = call("GET", f"/api/v1/devices/{DEVICE}/events?since=0&limit=50")
titles = [e["title"] for e in events.get("events", [])]
check("activity events flowing", any("Screenshot" in title for title in titles), str(titles[:6]))

status, denied = call("POST", f"/api/v1/devices/{DEVICE}/unlock", {"code": "000000", "action": "unlock"})
check("unlock rejects bad code", status == 403, str(denied))

code = totp_now(SESSION["totp_secret"])
status, allowed = call("POST", f"/api/v1/devices/{DEVICE}/unlock", {"code": code, "action": "away_disable"})
check("unlock accepts live code", status == 200 and allowed.get("ok"), str(allowed))

status, bad = call("POST", f"/api/v1/devices/{DEVICE}/command", {"type": "nonsense"})
check("unknown command rejected", status == 400)

_, away = call("POST", f"/api/v1/devices/{DEVICE}/command", {"type": "away_enable"})
check("away mode accepted", away.get("ok") is True, str(away)[:200])
away_id = away.get("command", {}).get("id", "")
away_result = None
deadline = time.time() + 30
while time.time() < deadline:
    _, data = call("GET", f"/api/v1/devices/{DEVICE}/commands/{away_id}")
    away_result = data.get("command")
    if away_result and away_result["status"] in ("done", "failed", "timeout"):
        break
    time.sleep(1)
check("away mode executed", bool(away_result and away_result["status"] == "done"), str(away_result)[:300])

away_state = False
deadline = time.time() + 25
while time.time() < deadline:
    _, devices = call("GET", "/api/v1/devices")
    if devices["devices"][0]["away"]:
        away_state = True
        break
    time.sleep(2)
check("away state reported", away_state, str(devices["devices"][0].get("state")))

_, undone = call("POST", f"/api/v1/devices/{DEVICE}/unlock", {"code": totp_now(SESSION["totp_secret"]), "action": "unlock"})
check("disarm accepted", undone.get("ok") is True, str(undone)[:200])
cleared = False
deadline = time.time() + 25
while time.time() < deadline:
    _, devices = call("GET", "/api/v1/devices")
    if not devices["devices"][0]["away"]:
        cleared = True
        break
    time.sleep(2)
check("away cleared", cleared, str(devices["devices"][0].get("state")))

print()
if FAILS:
    print("FAILED:", ", ".join(FAILS))
    sys.exit(1)
print("agent end-to-end checks passed")
