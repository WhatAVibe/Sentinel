import json
import threading
import time
from pathlib import Path

from flask import Flask, jsonify, render_template_string, request
from werkzeug.middleware.proxy_fix import ProxyFix

from . import api
from .config import Config
from .db import Database
from .hub import Hub
from .power import check_offline_devices
from .reqlog import init_request_logging
from .ws import init_ws

INDEX = """<!doctype html>
<html><head><meta charset="utf-8"><title>{{ name }}</title>
<style>
  body{background:#0f1115;color:#e6e9ef;font-family:Segoe UI,system-ui,sans-serif;display:flex;
       align-items:center;justify-content:center;height:100vh;margin:0}
  .card{background:#161a21;border:1px solid #262b35;border-radius:18px;padding:36px 44px;min-width:380px}
  h1{margin:0 0 6px;font-size:22px;font-weight:600}
  .muted{color:#8b93a1;font-size:13px}
  .row{display:flex;justify-content:space-between;margin-top:14px;font-size:14px}
  .ok{color:#6ee7a8}.dot{display:inline-block;width:8px;height:8px;border-radius:50%;background:#6ee7a8;margin-right:8px}
  code{background:#0b0d11;padding:2px 6px;border-radius:6px;color:#9fb4ff}
</style></head>
<body><div class="card">
  <h1>{{ name }}</h1>
  <div class="muted">Remote control relay &middot; v{{ version }}</div>
  <div class="row"><span>Status</span><span class="ok"><span class="dot"></span>online</span></div>
  <div class="row"><span>Paired PCs</span><span>{{ devices }}</span></div>
  <div class="row"><span>Phones</span><span>{{ mobiles }}</span></div>
  <div class="row"><span>Live sockets</span><span>{{ sockets }}</span></div>
  <div class="row muted">API base <code>{{ base }}/api/v1</code></div>
</div></body></html>"""


def _cleanup_loop(app, cfg, db):
    while True:
        try:
            now = int(time.time() * 1000)
            with db.cursor(write=True) as c:
                c.execute(
                    "UPDATE commands SET status='timeout', completed_at=? "
                    "WHERE status='delivered' AND delivered_at < ?",
                    (now, now - int(cfg.data["command_timeout_seconds"]) * 1000),
                )
                c.execute(
                    "UPDATE pair_requests SET status='expired', secret_tmp=NULL "
                    "WHERE status='pending' AND expires_at < ?",
                    (now,),
                )
                c.execute(
                    "DELETE FROM devices WHERE id IN ("
                    "SELECT device_id FROM pair_requests WHERE status IN ('expired','denied') "
                    "AND secret_tmp IS NULL AND device_id NOT IN (SELECT device_id FROM mobiles))"
                )
                c.execute("DELETE FROM pair_requests WHERE expires_at < ?", (now - 24 * 3600 * 1000,))
                days = int(cfg.data["retention_days"])
                if days > 0:
                    cutoff = now - days * 86400 * 1000
                    c.execute("DELETE FROM events WHERE ts < ?", (cutoff,))
                    c.execute("DELETE FROM audit WHERE ts < ?", (int(time.time()) - days * 86400,))
            if int(cfg.data["retention_days"]) > 0:
                cutoff = (time.time() - int(cfg.data["retention_days"]) * 86400) * 1000
                rows = db.all("SELECT id, path FROM screenshots WHERE ts < ?", (cutoff,))
                for row in rows:
                    try:
                        Path(row["path"]).unlink(missing_ok=True)
                    except OSError:
                        pass
                    db.execute("DELETE FROM screenshots WHERE id=?", (row["id"],))
        except Exception:
            pass
        time.sleep(900)


def _power_loop(cfg, db, hub):
    interval = max(1, int(cfg.data.get("power_check_seconds", 20)))
    while True:
        try:
            check_offline_devices(cfg, db, hub)
        except Exception:
            pass
        time.sleep(interval)


def create_app(cfg: Config) -> Flask:
    app = Flask(__name__)
    app.wsgi_app = ProxyFix(app.wsgi_app, x_for=1, x_proto=1, x_host=1)
    app.config["MAX_CONTENT_LENGTH"] = int(cfg.data["max_upload_mb"]) * 1024 * 1024 + 1024 * 1024
    db = Database(cfg.db_path)
    db.init()
    hub = Hub()
    app.extensions["pcm"] = {"db": db, "hub": hub, "config": cfg}

    app.register_blueprint(api.bp)
    init_ws(app, db, hub, cfg)
    init_request_logging(app, cfg)

    @app.before_request
    def cors_preflight():
        if request.method == "OPTIONS":
            resp = app.make_response(("", 204))
            _cors(resp)
            return resp

    @app.after_request
    def add_cors(resp):
        _cors(resp)
        return resp

    def _cors(resp):
        resp.headers["Access-Control-Allow-Origin"] = request.headers.get("Origin", "*")
        resp.headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type, X-Auth-Token"
        resp.headers["Access-Control-Allow-Methods"] = "GET, POST, PATCH, DELETE, OPTIONS"
        resp.headers["Access-Control-Max-Age"] = "86400"

    @app.get("/")
    def index():
        stats = hub.stats()
        return render_template_string(
            INDEX,
            name=cfg.data["server_name"],
            version=cfg.version,
            devices=db.one("SELECT COUNT(*) AS n FROM devices")["n"],
            mobiles=db.one("SELECT COUNT(*) AS n FROM mobiles")["n"],
            sockets=stats["clients"] + sum(stats["agents"].values()),
            base=cfg.public_base(),
        )

    @app.errorhandler(404)
    def not_found(_e):
        if request.path.startswith("/api/"):
            return jsonify(error="not_found"), 404
        return "not found", 404

    @app.errorhandler(413)
    def too_large(_e):
        return jsonify(error="payload_too_large"), 413

    threading.Thread(target=_cleanup_loop, args=(app, cfg, db), daemon=True).start()
    threading.Thread(target=_power_loop, args=(cfg, db, hub), daemon=True).start()
    return app
