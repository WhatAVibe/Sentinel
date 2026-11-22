import threading
import urllib.parse
import urllib.request


def push(cfg, title: str, body: str, tags: str = "shield", priority: int = 3) -> None:
    """Best-effort push through an ntfy topic (self-hosted or ntfy.sh)."""
    base = (cfg.data.get("ntfy_url") or "").rstrip("/")
    topic = (cfg.data.get("ntfy_topic") or "").strip()
    if not base or not topic or not body:
        return

    safe_title = title.encode("ascii", "ignore").decode("ascii") or "Sentinel"

    def send():
        try:
            request = urllib.request.Request(
                f"{base}/{urllib.parse.quote(topic)}",
                data=body[:3500].encode("utf-8"),
                headers={
                    "Title": safe_title[:120],
                    "Priority": str(priority),
                    "Tags": tags,
                },
                method="POST",
            )
            urllib.request.urlopen(request, timeout=8).read()
        except Exception:
            pass

    threading.Thread(target=send, daemon=True).start()
