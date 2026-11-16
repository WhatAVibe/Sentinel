import json
import queue
import threading


class Hub:
    """Fan-out of live payloads to connected websocket peers."""

    def __init__(self):
        self._agents: dict[str, list[queue.Queue]] = {}
        self._clients: list[queue.Queue] = []
        self._lock = threading.Lock()

    def register_agent(self, device_id: str) -> queue.Queue:
        q: queue.Queue = queue.Queue(maxsize=128)
        with self._lock:
            self._agents.setdefault(device_id, []).append(q)
        return q

    def unregister_agent(self, device_id: str, q: queue.Queue) -> None:
        with self._lock:
            peers = self._agents.get(device_id, [])
            if q in peers:
                peers.remove(q)
            if not peers:
                self._agents.pop(device_id, None)

    def agent_online(self, device_id: str) -> bool:
        with self._lock:
            return bool(self._agents.get(device_id))

    def push_agent(self, device_id: str, payload: dict) -> bool:
        sent = False
        with self._lock:
            peers = list(self._agents.get(device_id, []))
        for q in peers:
            try:
                q.put_nowait(payload)
                sent = True
            except queue.Full:
                pass
        return sent

    def register_client(self) -> queue.Queue:
        q: queue.Queue = queue.Queue(maxsize=256)
        with self._lock:
            self._clients.append(q)
        return q

    def unregister_client(self, q: queue.Queue) -> None:
        with self._lock:
            if q in self._clients:
                self._clients.remove(q)

    def broadcast(self, payload: dict) -> None:
        with self._lock:
            peers = list(self._clients)
        for q in peers:
            try:
                q.put_nowait(payload)
            except queue.Full:
                pass

    def push_to_device_clients(self, device_id: str, payload: dict) -> None:
        payload = {**payload, "device_id": device_id}
        self.broadcast(payload)

    def stats(self) -> dict:
        with self._lock:
            return {"agents": {k: len(v) for k, v in self._agents.items()}, "clients": len(self._clients)}
