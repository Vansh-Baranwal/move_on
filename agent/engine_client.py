"""HTTP client for the robot engine running inside Unity on localhost:8080.

Only the endpoints from the problem statement plus the operational helpers
(/health, /reset, /record/*). None of these ever return object identity data.
"""

from __future__ import annotations

import time

import requests


class EngineError(RuntimeError):
    """Raised when the engine cannot be reached or returns an error response."""


class EngineClient:
    def __init__(self, base_url: str = "http://localhost:8080", timeout: float = 150.0):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    def wait_ready(self, attempts: int = 60, delay: float = 1.0) -> None:
        last_error: Exception | None = None
        for _ in range(attempts):
            try:
                response = requests.get(f"{self.base_url}/health", timeout=3)
                if response.ok:
                    return
            except requests.RequestException as error:
                last_error = error
            time.sleep(delay)
        raise EngineError(
            f"Engine not reachable at {self.base_url} ({last_error}). "
            "Open the Unity project, press Play, and wait for 'Robot engine API listening'."
        )

    # -- required spec endpoints -------------------------------------------------

    def state(self) -> dict:
        return self._request("GET", "/state")

    def look(self) -> bytes:
        """JPEG frame (640x480). Claude's only view of objects."""
        return self._request("GET", "/look", raw=True)

    def depth(self) -> float | None:
        """Single-ray distance straight ahead, metres, max 10; None if nothing in range."""
        return self._request("GET", "/depth")["depth"]

    def move(self, distance: float) -> dict:
        return self._request("POST", "/move", {"distance": distance})

    def turn(self, degrees: float) -> dict:
        return self._request("POST", "/turn", {"degrees": degrees})

    def pickup(self) -> dict:
        return self._request("POST", "/pickup")

    def drop(self) -> dict:
        return self._request("POST", "/drop")

    # -- operational helpers -----------------------------------------------------

    def push_log(self, entry: dict) -> None:
        """Mirror a log line into the engine's live UI panel. Best-effort: the
        panel is UI-only, so a failed push never affects the run."""
        try:
            self._request("POST", "/log", entry)
        except EngineError:
            pass

    def set_replay(self, active: bool, index: int = 0) -> dict:
        return self._request("POST", "/replay", {"active": active, "index": index})

    def reset(self, seed: int = 0) -> dict:
        """Restore robot pose and retry count; seed > 0 rebuilds a deterministic
        random layout for testing."""
        return self._request("POST", "/reset", {"seed": seed})

    def record_start(self, command: str, frames_dir: str | None = None) -> dict:
        """Begin a POV recording session. frames_dir points the engine at the
        agent's own run directory so all run artifacts live in one place."""
        body: dict = {"command": command}
        if frames_dir:
            body["frames_dir"] = frames_dir
        return self._request("POST", "/record/start", body)

    def record_stop(self) -> dict:
        return self._request("POST", "/record/stop")

    # -- internals ----------------------------------------------------------------

    def _request(self, method: str, path: str, json_body: dict | None = None, raw: bool = False):
        try:
            response = requests.request(method, self.base_url + path, json=json_body, timeout=self.timeout)
        except requests.RequestException as error:
            raise EngineError(f"{method} {path} failed: {error}") from error
        if not response.ok:
            raise EngineError(f"{method} {path} -> HTTP {response.status_code}: {response.text[:200]}")
        return response.content if raw else response.json()
