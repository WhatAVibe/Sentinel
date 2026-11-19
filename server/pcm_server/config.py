import json
import os
from pathlib import Path

DEFAULTS = {
    "server_name": "Home Sentinel",
    "host": "0.0.0.0",
    "port": 8765,
    "storage_dir": "storage",
    "public_url": "",
    "pair_code_ttl_seconds": 600,
    "command_timeout_seconds": 90,
    "command_ttl_seconds": 120,
    "retention_days": 30,
    "max_upload_mb": 32,
    "allow_registration": True,
    "log_requests": True,
    "ntfy_url": "",
    "ntfy_topic": "",
    "offline_alert_seconds": 300,
    "power_check_seconds": 20,
    "boot_notify": True,
}


class Config:
    def __init__(self, path: Path, data: dict):
        self.path = Path(path)
        self.data = {**DEFAULTS, **(data or {})}
        base = self.path.parent
        storage = Path(self.data["storage_dir"])
        if not storage.is_absolute():
            storage = (base / storage).resolve()
        self.storage_dir = storage
        self.shots_dir = storage / "shots"
        self.db_path = storage / "sentinel.db"
        self.version = "1.2.3"

    @classmethod
    def load(cls, path: str | os.PathLike) -> "Config":
        p = Path(path)
        if not p.exists():
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(json.dumps(DEFAULTS, indent=2), encoding="utf-8")
        data = json.loads(p.read_text(encoding="utf-8") or "{}")
        cfg = cls(p, data)
        cfg.storage_dir.mkdir(parents=True, exist_ok=True)
        cfg.shots_dir.mkdir(parents=True, exist_ok=True)
        return cfg

    def public_base(self) -> str:
        if self.data.get("public_url"):
            return str(self.data["public_url"]).rstrip("/")
        host = self.data.get("host", "0.0.0.0")
        if host in ("0.0.0.0", "::"):
            host = "127.0.0.1"
        return f"http://{host}:{self.data['port']}"
