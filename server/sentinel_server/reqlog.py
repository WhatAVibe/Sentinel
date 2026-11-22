import json
import logging
import time
from logging.handlers import TimedRotatingFileHandler
from pathlib import Path

from flask import g, request

SECRET_KEYS = ("token", "code", "password")


def redact(path: str) -> str:
    if "?" not in path:
        return path
    base, _, query = path.partition("?")
    parts = []
    for chunk in query.split("&"):
        key = chunk.split("=", 1)[0]
        if key.lower() in SECRET_KEYS:
            parts.append(key + "=***")
        else:
            parts.append(chunk)
    return base + "?" + "&".join(parts)


def init_request_logging(app, cfg):
    log_dir = Path(cfg.storage_dir) / "logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    logger = logging.getLogger("sentinel.requests")
    logger.setLevel(logging.INFO)
    logger.propagate = False
    if not logger.handlers:
        file_handler = TimedRotatingFileHandler(
            log_dir / "relay.log", when="midnight", backupCount=14, encoding="utf-8"
        )
        file_handler.setFormatter(logging.Formatter("%(message)s"))
        logger.addHandler(file_handler)
        console = logging.StreamHandler()
        console.setFormatter(logging.Formatter("%(message)s"))
        logger.addHandler(console)

    @app.before_request
    def _before():
        g._started = time.perf_counter()

    @app.after_request
    def _after(response):
        if not cfg.data.get("log_requests", True):
            return response
        try:
            elapsed = (time.perf_counter() - getattr(g, "_started", time.perf_counter())) * 1000
            forwarded = request.headers.get("X-Forwarded-For", "")
            ip = request.headers.get("CF-Connecting-IP") or forwarded.split(",")[0].strip() or request.remote_addr
            record = {
                "ts": int(time.time() * 1000),
                "ip": ip or "",
                "method": request.method,
                "path": redact(request.full_path.rstrip("?")),
                "status": response.status_code,
                "ms": round(elapsed, 1),
                "bytes": response.calculate_content_length() or 0,
                "actor": getattr(g, "actor", None),
                "ua": (request.headers.get("User-Agent") or "")[:120],
            }
            logger.info(json.dumps(record, separators=(",", ":")))
        except Exception:
            pass
        return response
