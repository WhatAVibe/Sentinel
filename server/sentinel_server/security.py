import base64
import hashlib
import hmac
import secrets
import struct
import threading
import time

PAIR_ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"


def new_token(nbytes: int = 32) -> str:
    return secrets.token_urlsafe(nbytes)


def hash_token(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def new_pair_code() -> str:
    return "".join(secrets.choice(PAIR_ALPHABET) for _ in range(6))


def new_totp_secret() -> str:
    return base64.b32encode(secrets.token_bytes(20)).decode("ascii").rstrip("=")


def _totp_at(secret: str, counter: int, digits: int = 6) -> str:
    pad = "=" * ((8 - len(secret) % 8) % 8)
    key = base64.b32decode(secret.upper() + pad)
    msg = struct.pack(">Q", counter)
    digest = hmac.new(key, msg, hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    value = struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7FFFFFFF
    return str(value % (10 ** digits)).zfill(digits)


def totp_now(secret: str, step: int = 30, digits: int = 6) -> str:
    return _totp_at(secret, int(time.time()) // step, digits)


def totp_seconds_remaining(step: int = 30) -> int:
    return step - (int(time.time()) % step)


def verify_totp(secret: str, code: str, window: int = 1, step: int = 30) -> bool:
    if not secret or not code:
        return False
    code = str(code).strip()
    if not code.isdigit() or len(code) != 6:
        return False
    counter = int(time.time()) // step
    for drift in range(-window, window + 1):
        if hmac.compare_digest(_totp_at(secret, counter + drift), code):
            return True
    return False


class RateLimiter:
    """Tiny in-memory sliding window limiter keyed by an arbitrary string.

    Counters live in this process, so the limits below assume the single-process
    relay described in the README. Running multiple workers (gunicorn -w N)
    would give each worker its own counters and multiply the effective limit;
    move the counters to Redis before scaling out.
    """

    def __init__(self):
        self._hits: dict[str, list[float]] = {}
        self._lock = threading.Lock()

    def allow(self, key: str, limit: int, window: float) -> bool:
        now = time.time()
        with self._lock:
            if len(self._hits) > 4096:
                stale = [k for k, v in self._hits.items() if not v or now - v[-1] > window * 4]
                for k in stale:
                    self._hits.pop(k, None)
            hits = [t for t in self._hits.get(key, []) if now - t < window]
            if len(hits) >= limit:
                self._hits[key] = hits
                return False
            hits.append(now)
            self._hits[key] = hits
            return True

    def reset(self, key: str) -> None:
        with self._lock:
            self._hits.pop(key, None)


limiter = RateLimiter()
