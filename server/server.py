import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from sentinel_server import Config, create_app


def main():
    parser = argparse.ArgumentParser(description="Home Sentinel relay server")
    parser.add_argument("--config", default=str(Path(__file__).with_name("config.json")))
    parser.add_argument("--host", default=None)
    parser.add_argument("--port", type=int, default=None)
    parser.add_argument("--debug", action="store_true")
    args = parser.parse_args()

    cfg = Config.load(args.config)
    if args.host:
        cfg.data["host"] = args.host
    if args.port:
        cfg.data["port"] = args.port

    app = create_app(cfg)
    print(f"  {cfg.data['server_name']} v{cfg.version}")
    print(f"  API      {cfg.public_base()}/api/v1")
    print(f"  Storage  {cfg.storage_dir}")
    print(f"  Listen   http://{cfg.data['host']}:{cfg.data['port']}")
    app.run(
        host=cfg.data["host"],
        port=int(cfg.data["port"]),
        threaded=True,
        debug=args.debug,
        use_reloader=False,
    )


if __name__ == "__main__":
    main()
