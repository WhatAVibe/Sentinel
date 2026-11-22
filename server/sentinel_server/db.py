import json
import queue
import sqlite3
import threading
import time
from contextlib import contextmanager
from pathlib import Path

SCHEMA = """
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS devices (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    platform TEXT,
    fingerprint TEXT,
    agent_version TEXT,
    token_hash TEXT NOT NULL,
    totp_secret TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    last_seen INTEGER,
    ip TEXT,
    away INTEGER NOT NULL DEFAULT 0,
    locked INTEGER NOT NULL DEFAULT 0,
    settings TEXT NOT NULL DEFAULT '{}',
    state TEXT NOT NULL DEFAULT '{}',
    metrics TEXT NOT NULL DEFAULT '{}',
    offline_alerted INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS mobiles (
    id TEXT PRIMARY KEY,
    name TEXT,
    device_id TEXT NOT NULL,
    token_hash TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    last_seen INTEGER,
    uid TEXT
);

CREATE TABLE IF NOT EXISTS pair_requests (
    id TEXT PRIMARY KEY,
    code TEXT NOT NULL,
    device_id TEXT,
    device_name TEXT,
    platform TEXT,
    fingerprint TEXT,
    token_hash TEXT NOT NULL,
    secret_tmp TEXT,
    status TEXT NOT NULL DEFAULT 'pending',
    created_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS commands (
    id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL,
    type TEXT NOT NULL,
    args TEXT NOT NULL DEFAULT '{}',
    source TEXT,
    status TEXT NOT NULL DEFAULT 'pending',
    created_at INTEGER NOT NULL,
    delivered_at INTEGER,
    completed_at INTEGER,
    result TEXT
);

CREATE INDEX IF NOT EXISTS idx_commands_device ON commands(device_id, status, created_at);

CREATE TABLE IF NOT EXISTS events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id TEXT NOT NULL,
    ts INTEGER NOT NULL,
    category TEXT NOT NULL,
    severity TEXT NOT NULL DEFAULT 'info',
    title TEXT NOT NULL,
    detail TEXT
);

CREATE INDEX IF NOT EXISTS idx_events_device ON events(device_id, id);
CREATE INDEX IF NOT EXISTS idx_events_ts ON events(ts);

CREATE TABLE IF NOT EXISTS screenshots (
    id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL,
    ts INTEGER NOT NULL,
    monitor INTEGER,
    monitor_name TEXT,
    width INTEGER,
    height INTEGER,
    trigger TEXT,
    path TEXT NOT NULL,
    size INTEGER
);

CREATE INDEX IF NOT EXISTS idx_shots_device ON screenshots(device_id, ts);

CREATE TABLE IF NOT EXISTS audit (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts INTEGER NOT NULL,
    actor TEXT,
    action TEXT NOT NULL,
    detail TEXT
);

CREATE TABLE IF NOT EXISTS messages (
    id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL,
    direction TEXT NOT NULL,
    kind TEXT NOT NULL DEFAULT 'toast',
    title TEXT,
    body TEXT,
    reply TEXT,
    allow_reply INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'sent',
    created_at INTEGER NOT NULL,
    updated_at INTEGER
);

CREATE INDEX IF NOT EXISTS idx_messages_device ON messages(device_id, created_at);
"""


class Database:
    """Bounded connection pool.

    Werkzeug's threaded server creates a thread per connection, so a
    thread-local connection per request leaked three fds (db, -wal, -shm)
    at a time until the process hit EMFILE and every query failed with
    "unable to open database file". The pool caps the fd count regardless
    of how many threads the server spawns.
    """

    def __init__(self, path: Path, pool_size: int = 12):
        self.path = Path(path)
        self._pool: queue.LifoQueue = queue.LifoQueue(maxsize=pool_size)
        self._pool_size = pool_size
        self._open = 0
        self._open_lock = threading.Lock()
        self._write_lock = threading.Lock()

    def _new_connection(self) -> sqlite3.Connection:
        conn = sqlite3.connect(self.path, timeout=30, check_same_thread=False)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA foreign_keys=ON")
        conn.execute("PRAGMA busy_timeout=30000")
        return conn

    def _acquire(self) -> sqlite3.Connection:
        try:
            return self._pool.get_nowait()
        except queue.Empty:
            pass
        with self._open_lock:
            if self._open < self._pool_size:
                self._open += 1
                try:
                    return self._new_connection()
                except Exception:
                    self._open -= 1
                    raise
        return self._pool.get(timeout=30)

    def _release(self, conn: sqlite3.Connection) -> None:
        try:
            self._pool.put_nowait(conn)
        except queue.Full:
            conn.close()
            with self._open_lock:
                self._open -= 1

    def close_all(self) -> None:
        while True:
            try:
                conn = self._pool.get_nowait()
            except queue.Empty:
                break
            conn.close()
        with self._open_lock:
            self._open = 0

    def connect(self) -> sqlite3.Connection:
        return self._acquire()

    @contextmanager
    def cursor(self, write: bool = False):
        conn = self._acquire()
        try:
            if write:
                with self._write_lock:
                    cur = conn.cursor()
                    try:
                        yield cur
                        conn.commit()
                    except Exception:
                        conn.rollback()
                        raise
                    finally:
                        cur.close()
            else:
                cur = conn.cursor()
                try:
                    yield cur
                finally:
                    cur.close()
        finally:
            self._release(conn)

    def init(self):
        conn = self._new_connection()
        try:
            with self._write_lock:
                conn.executescript(SCHEMA)
                columns = [row[1] for row in conn.execute("PRAGMA table_info(mobiles)")]
                if "uid" not in columns:
                    conn.execute("ALTER TABLE mobiles ADD COLUMN uid TEXT")
                device_columns = [row[1] for row in conn.execute("PRAGMA table_info(devices)")]
                if "offline_alerted" not in device_columns:
                    conn.execute("ALTER TABLE devices ADD COLUMN offline_alerted INTEGER NOT NULL DEFAULT 0")
                if "is_primary" not in columns:
                    conn.execute("ALTER TABLE mobiles ADD COLUMN is_primary INTEGER NOT NULL DEFAULT 0")
                if "app_version" not in columns:
                    conn.execute("ALTER TABLE mobiles ADD COLUMN app_version TEXT")
                if "kind" not in columns:
                    conn.execute("ALTER TABLE mobiles ADD COLUMN kind TEXT")
                if "confirmed" not in columns:
                    conn.execute("ALTER TABLE mobiles ADD COLUMN confirmed INTEGER NOT NULL DEFAULT 1")
                conn.commit()
        finally:
            conn.close()

    def one(self, sql: str, params=()):
        with self.cursor() as c:
            c.execute(sql, params)
            row = c.fetchone()
            return dict(row) if row else None

    def all(self, sql: str, params=()):
        with self.cursor() as c:
            c.execute(sql, params)
            return [dict(r) for r in c.fetchall()]

    def execute(self, sql: str, params=()):
        with self.cursor(write=True) as c:
            c.execute(sql, params)
            return c.rowcount

    def execute_returning_id(self, sql: str, params=()):
        with self.cursor(write=True) as c:
            c.execute(sql, params)
            return c.lastrowid

    def audit(self, actor: str, action: str, detail="") -> None:
        if not isinstance(detail, str):
            detail = json.dumps(detail)
        self.execute(
            "INSERT INTO audit (ts, actor, action, detail) VALUES (?,?,?,?)",
            (int(time.time()), actor, action, detail),
        )
