"""Approves a pending pair request (simulating the phone) and stores the mobile session."""

import json
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

BASE = sys.argv[1]
REPORT = Path(sys.argv[2])
OUT = Path(sys.argv[3])


def call(method, path, body=None, token=None, expect=200):
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=20) as response:
            return response.status, json.loads(response.read() or b"{}")
    except urllib.error.HTTPError as exc:
        return exc.code, json.loads(exc.read() or b"{}")


deadline = time.time() + 40
while not REPORT.exists() and time.time() < deadline:
    time.sleep(0.5)
if not REPORT.exists():
    print("pair report missing")
    sys.exit(1)

report = json.loads(REPORT.read_text())
status, body = call(
    "POST",
    "/api/v1/pair/approve",
    {"request_id": report["request_id"], "pair_code": report["pair_code"], "mobile_name": "E2E Phone"},
)
if status != 200:
    print("approve failed", status, body)
    sys.exit(1)

OUT.write_text(
    json.dumps(
        {
            "mobile_token": body["mobile_token"],
            "totp_secret": body["totp_secret"],
            "device_id": body["device"]["id"],
            "device_name": body["device"]["name"],
            "server": BASE,
        }
    )
)
print("approved", body["device"]["name"], body["device"]["id"])
