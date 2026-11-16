"""Offline / reboot alert checks. Run against a short-threshold test server."""

import json
import sys
import time
import urllib.error
import urllib.request

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8799"
FAILS = []


def call(method, path, body=None, token=None, expect=200):
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json", "User-Agent": "SentinelPowerTest/1.2.1"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=20) as response:
            raw = response.read()
            return response.status, (json.loads(raw) if raw else {})
    except urllib.error.HTTPError as exc:
        raw = exc.read()
        return exc.code, (json.loads(raw) if raw else {})


def check(name, ok, detail=""):
    print(("  ok   " if ok else "  FAIL ") + name + (f"  {detail}" if detail and not ok else ""))
    if not ok:
        FAILS.append(name)


def heartbeat(agent, uptime, away=False):
    return call(
        "POST",
        "/api/v1/agent/heartbeat",
        {
            "away": away,
            "locked": False,
            "agent_version": "test",
            "metrics": {"uptime_seconds": uptime, "hostname": "TEST", "mem_total_mb": 1024},
            "state": {"monitors": 1},
        },
        agent,
    )


def main():
    req = call("POST", "/api/v1/pair/request", {"device_name": "POWER-TEST"})[1]
    rid, code = req["request_id"], req["pair_code"]
    approve = call("POST", "/api/v1/pair/approve", {"request_id": rid, "pair_code": code})[1]
    mobile = approve["mobile_token"]
    device = approve["device"]["id"]
    status = call("GET", f"/api/v1/pair/status?request_id={rid}&token={req['request_token']}")[1]
    agent = status["agent_token"]

    heartbeat(agent, 500)
    print("  ..   waiting for offline detection")
    found = False
    for _ in range(12):
        time.sleep(1)
        events = call("GET", f"/api/v1/devices/{device}/events?since=0&limit=100", token=mobile)[1]["events"]
        if any(e["category"] == "power" and "offline" in e["title"] for e in events):
            found = True
            break
    check("offline alert", found)

    heartbeat(agent, 520)
    events = call("GET", f"/api/v1/devices/{device}/events?since=0&limit=100", token=mobile)[1]["events"]
    check("back online alert", any("back online" in e["title"] for e in events), str(events[-3:]))

    heartbeat(agent, 6000)
    heartbeat(agent, 20)
    events = call("GET", f"/api/v1/devices/{device}/events?since=0&limit=100", token=mobile)[1]["events"]
    check("reboot alert", any("restarted" in e["title"] for e in events), str([e["title"] for e in events]))

    print()
    if FAILS:
        print("FAILED:", ", ".join(FAILS))
        sys.exit(1)
    print("power alert checks passed")


if __name__ == "__main__":
    main()
