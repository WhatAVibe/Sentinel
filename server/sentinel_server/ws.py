import json
import queue
import time

from flask import request
from flask_sock import Sock

from .security import hash_token


def init_ws(app, db, hub, cfg):
    sock = Sock(app)

    def _agent_ok(token: str | None):
        if not token:
            return None
        return db.one("SELECT * FROM devices WHERE token_hash = ?", (hash_token(token),))

    def _mobile_ok(token: str | None):
        if not token:
            return None
        return db.one("SELECT * FROM mobiles WHERE token_hash = ?", (hash_token(token),))

    @sock.route("/ws/agent")
    def agent_socket(ws):
        device = _agent_ok(request.args.get("token"))
        if not device:
            ws.send(json.dumps({"type": "error", "error": "unauthorized"}))
            return
        device_id = device["id"]
        q = hub.register_agent(device_id)
        try:
            ws.send(json.dumps({"type": "hello", "device_id": device_id}))
            while True:
                try:
                    item = q.get(timeout=20)
                except queue.Empty:
                    ws.send(json.dumps({"type": "ping", "ts": int(time.time() * 1000)}))
                    continue
                if item.get("type") != "command":
                    ws.send(json.dumps(item))
                    continue
                cmd = item["command"]
                try:
                    db.execute(
                        "UPDATE commands SET status='delivered', delivered_at=? WHERE id=? AND status='pending'",
                        (int(time.time() * 1000), cmd["id"]),
                    )
                    ws.send(json.dumps(item))
                except Exception:
                    db.execute(
                        "UPDATE commands SET status='pending', delivered_at=NULL WHERE id=? AND status='delivered'",
                        (cmd["id"],),
                    )
                    break
        finally:
            hub.unregister_agent(device_id, q)

    @sock.route("/ws/client")
    def client_socket(ws):
        mobile = _mobile_ok(request.args.get("token"))
        if not mobile:
            ws.send(json.dumps({"type": "error", "error": "unauthorized"}))
            return
        q = hub.register_client(mobile["device_id"])
        try:
            ws.send(json.dumps({"type": "hello", "device_id": mobile["device_id"]}))
            while True:
                try:
                    item = q.get(timeout=20)
                except queue.Empty:
                    ws.send(json.dumps({"type": "ping", "ts": int(time.time() * 1000)}))
                    continue
                ws.send(json.dumps(item))
        finally:
            hub.unregister_client(mobile["device_id"], q)
