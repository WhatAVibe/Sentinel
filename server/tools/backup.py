"""Consistent backup of a Sentinel relay (SQLite + screenshots).

Usage:
    python server/tools/backup.py --config /opt/sentinel/server/config.json --out /var/backups/sentinel
    python server/tools/backup.py --keep-days 14 --no-shots

Uses sqlite3's online backup API, which is safe with WAL mode and while the
relay is running. Restoring: stop the relay, replace storage/sentinel.db and
storage/shots/ with the contents of the archive, start the relay.
"""

import argparse
import json
import sqlite3
import sys
import tempfile
import zipfile
from datetime import datetime, timezone
from pathlib import Path


def load_config(path: Path) -> dict:
    defaults = {"storage_dir": "storage", "server_name": "Sentinel"}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        print(f"config not found: {path}", file=sys.stderr)
        raise SystemExit(2)
    return {**defaults, **data}


def main() -> int:
    parser = argparse.ArgumentParser(description="Back up a Sentinel relay")
    parser.add_argument("--config", default="server/config.json")
    parser.add_argument("--out", default="backups", help="directory for the archive")
    parser.add_argument("--no-shots", action="store_true", help="skip storage/shots")
    parser.add_argument("--keep-days", type=int, default=0, help="delete archives older than N days")
    args = parser.parse_args()

    config_path = Path(args.config).resolve()
    config = load_config(config_path)
    storage = Path(config["storage_dir"])
    if not storage.is_absolute():
        storage = (config_path.parent / storage).resolve()
    database = storage / "sentinel.db"
    shots = storage / "shots"
    if not database.exists():
        print(f"database not found: {database}", file=sys.stderr)
        return 2

    out_dir = Path(args.out).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    archive = out_dir / f"sentinel-backup-{stamp}.zip"

    with tempfile.TemporaryDirectory() as tmp:
        copy = Path(tmp) / "sentinel.db"
        source = sqlite3.connect(f"file:{database}?mode=ro", uri=True)
        destination = sqlite3.connect(copy)
        try:
            source.backup(destination)
        finally:
            destination.close()
            source.close()

        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zf:
            zf.write(copy, "sentinel.db")
            if config_path.exists():
                zf.write(config_path, "config.json")
            if not args.no_shots and shots.exists():
                for path in sorted(shots.rglob("*")):
                    if path.is_file():
                        zf.write(path, str(Path("shots") / path.relative_to(shots)))
            manifest = {
                "created": datetime.now(timezone.utc).isoformat(),
                "server_name": config.get("server_name", "Sentinel"),
                "source": str(database),
                "shots": not args.no_shots,
            }
            zf.writestr("manifest.json", json.dumps(manifest, indent=2))

    print(archive)
    print(f"  {archive.stat().st_size / 1024 / 1024:.1f} MB")

    if args.keep_days > 0:
        cutoff = datetime.now(timezone.utc).timestamp() - args.keep_days * 86400
        for path in out_dir.glob("sentinel-backup-*.zip"):
            if path.stat().st_mtime < cutoff:
                path.unlink(missing_ok=True)
                print(f"  pruned {path.name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
