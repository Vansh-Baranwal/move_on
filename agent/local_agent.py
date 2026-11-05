"""Local fetch policy: plays the task with on-device pixel analysis only.

Plug-compatible with the LLM providers (decide/report), so it runs through the
same tool-use loop, JSONL logging and POV recording. No API calls, no rate
limits - used to verify the full pipeline and as a fallback brain.
"""

from __future__ import annotations

import re

from local_vision import analyse_frame, offset_degrees, physical_height

DEG_PER_PX = 90.0 / 640.0
NEAR_HEIGHT_PX = 140.0     # blob this tall => within pickup range
ZONE_WIDE_PX = 420.0       # zone blob this wide => drop
CENTRE_TOLERANCE_PX = 24.0


def parse_command(command: str) -> dict:
    text = command.lower()
    colour = next((c for c in ("red", "blue", "yellow", "green") if c in text), None)
    shape = None
    if any(word in text for word in ("round", "ball", "sphere")):
        shape = "round"
    elif any(word in text for word in ("cube", "box", "square")):
        shape = "boxy"
    elif "cylinder" in text:
        shape = "cylinder"
    elif "cone" in text:
        shape = "cone"
    mode = "tallest" if ("tallest" in text or "biggest" in text) else "colour"
    return {"colour": colour, "shape": shape, "mode": mode}


class LocalProvider:
    """Finite-state fetch policy: SCAN -> APPROACH -> PICKUP -> RETURN -> DROP."""

    def __init__(self, command: str | None = None):
        self.command = command or ""
        self.target = parse_command(self.command)
        self.phase = "scan"
        self.scan_turned = 0.0
        self.rejected_bearings: list[float] = []
        self.seen_targets: list[tuple[float, float]] = []  # (bearing, area)
        self.full_sweeps = 0
        self.target_bearing: float | None = None
        self.target_shape_pref = self.target.get("shape")
        self.zone_bearing: float | None = None
        self.pending: str | None = None      # queued engine op after a movement
        self.last_result: dict = {}
        self.prev_retries = 0
        self.collision_turn_sign = 1.0
        self.pickup_tries = 0
        self.drop_tries = 0
        self.after_pivot = False
        self.measurements: list[dict] = []
        self.measure_queue: list[float] = []  # candidate bearings for tallest mode
        self.best_height = -1.0
        self.best_bearing: float | None = None
        self.steps = 0
        self.totals = {"input": 0, "output": 0, "cache_read": 0, "cache_write": 0}

    # -- provider protocol ----------------------------------------------------

    def decide(self, text: str, jpeg: bytes | None = None):
        self.steps += 1
        if self.steps > 55:
            return self._finish("fail_not_reached", "step budget exhausted")

        state = self._parse_state(text)
        if state["retries"] >= 3:
            return self._finish("fail_collision", "gave up after 3 collision retries")
        if state["retries"] > self.prev_retries:
            # Fresh collision: cancel any queued op, pivot away, then sidestep
            # so the retry does not re-enter the same blocked line.
            self.collision_turn_sign *= -1.0
            self.prev_retries = state["retries"]
            if self.pending == "pickup":
                self.pending = None
                return "pickup", {}, {}, "bumped the target - picking it up"
            self.after_pivot = True
            note = f"collision {self.prev_retries}; pivoting"
            return "turn", {"degrees": 70.0 * self.collision_turn_sign}, {}, note
        if self.after_pivot:
            self.after_pivot = False
            return "move", {"distance": 0.5}, {}, "sidestepping after pivot"

        if self.last_result.get("in_zone") is True:
            return self._finish("pass", "object delivered to the drop zone")
        if self.last_result.get("success") is True:
            self.phase = "return"
        if state["holding"]:
            return self._deliver(state, jpeg)
        if self.last_result.get("in_zone") is False:
            self.drop_tries += 1
            if self.drop_tries >= 3:
                return self._finish("fail_not_reached", "object missed the zone after 3 drops")

        frame = analyse_frame(jpeg) if jpeg is not None else None
        note = self._note(frame)
        if frame is not None:
            self._remember(frame, state)

        if self.phase == "scan":
            return self._scan(state, frame, note)
        if self.phase == "return":
            return self._deliver(state, jpeg)
        if self.phase == "measure":
            return self._measure(state, jpeg)
        return self._approach(state, frame, note)

    def report(self, result_json: str) -> None:
        import json
        try:
            self.last_result = json.loads(result_json)
        except Exception:
            self.last_result = {}

    # -- phases -----------------------------------------------------------------

    def _scan(self, state: dict, frame: list[dict] | None, note: str):
        if frame is not None:
            match = self._best_match(frame, state["heading"])
            sweep_done = self.scan_turned >= 340
            if match is not None and (self.target["mode"] != "tallest" or sweep_done):
                blob = match
                self.target_bearing = (state["heading"] + offset_degrees(blob["cx"])) % 360.0
                self.phase = "approach"
                return "turn", {"degrees": round(offset_degrees(blob["cx"]), 1)}, {}, \
                    note + f" | target spotted, bearing {self.target_bearing:.0f}"
            if self.target["mode"] == "tallest":
                for blob in frame:
                    if blob["colour"] == "cyan":
                        continue
                    bearing = (state["heading"] + offset_degrees(blob["cx"])) % 360.0
                    if not any(abs((bearing - seen) % 360) < 25 for seen in self.measure_queue):
                        self.measure_queue.append(bearing)
            if sweep_done and self.target["mode"] == "tallest" and self.measure_queue:
                self.phase = "measure"
                return self._go_measure(state)
            if self.scan_turned >= 360:
                if self.target["mode"] == "tallest" and self.measure_queue:
                    self.phase = "measure"
                    return self._go_measure(state)
                if self.rejected_bearings and self.full_sweeps < 2:
                    # The robot has moved since the rejection; geometry changed.
                    self.full_sweeps += 1
                    self.rejected_bearings = []
                    self.scan_turned = 0.0
                    return "turn", {"degrees": 60.0}, {}, note + " | clean re-sweep after rejections"
                return self._finish("fail_not_reached", "target never appeared in any scan frame")
            self.scan_turned += 60.0
            return "turn", {"degrees": 60.0}, {}, note

        # text-only step right after the previous turn: keep sweeping
        return "turn", {"degrees": 60.0}, {}, "scan continues"

    def _go_measure(self, state: dict):
        if not self.measure_queue:
            return self._finish("fail_not_reached", "nothing to measure")
        self.target_bearing = self.measure_queue[0]
        delta = (self.target_bearing - state["heading"] + 540.0) % 360.0 - 180.0
        return "turn", {"degrees": round(delta, 1)}, {}, "measuring candidate height"

    def _measure(self, state: dict, jpeg: bytes | None):
        depth = self._depth_from_last()
        frame = analyse_frame(jpeg) if jpeg is not None else None
        if depth is not None and depth < 2.8 and frame:
            candidates = [blob for blob in frame if blob["colour"] != "magenta"]
            if candidates:
                blob = max(candidates, key=lambda item: item["h"])
                estimate = physical_height(blob["h"], depth)
                self.measurements.append({"bearing": self.target_bearing, "height": estimate})
                if estimate > self.best_height:
                    self.best_height = estimate
                    self.best_bearing = self.target_bearing
        self.measure_queue = self.measure_queue[1:]
        if self.measure_queue:
            return self._go_measure(state)
        if self.best_bearing is None:
            return self._finish("fail_not_reached", "no measurable object found")
        self.phase = "approach"
        self.target_bearing = self.best_bearing
        delta = (self.target_bearing - state["heading"] + 540.0) % 360.0 - 180.0
        return "turn", {"degrees": round(delta, 1)}, {}, f"tallest candidate at {self.best_bearing:.0f} ({self.best_height:.2f} m est.)"

    def _approach(self, state: dict, frame: list[dict] | None, note: str):
        if self.pending == "pickup":
            self.pending = None
            self.pickup_tries += 1
            return "pickup", {}, {}, "pickup attempt"

        blob = self._best_match(frame, state["heading"]) if frame is not None else None
        if blob is None:
            if self.pickup_tries >= 2 and self.target_bearing is not None:
                # Twice at point-blank and still nothing attached: wrong object.
                self.rejected_bearings.append(self.target_bearing)
                self.pickup_tries = 0
                return self._after_rejection(state, note + " | pickup failed twice; retargeting")
            if self.target_bearing is None:
                self.phase = "scan"
                self.scan_turned = 0.0
                return "turn", {"degrees": 60.0}, {}, "target lost, resuming scan"
            delta = (self.target_bearing - state["heading"] + 540.0) % 360.0 - 180.0
            if abs(delta) > 15:
                return "turn", {"degrees": round(delta, 1)}, {}, note + " | reacquiring target"
            return "move", {"distance": 0.5}, {}, note + " | driving on memory"

        centre = blob["cx"]
        offset = offset_degrees(centre)
        height = float(blob["h"])
        # Depth cross-check first: a true sphere measures ~0.30 m tall; anything
        # taller means the depth ray is on a bigger object - blacklist it.
        if height >= NEAR_HEIGHT_PX and self.target["shape"] == "round" \
                and state["depth"] is not None and state["depth"] < 2.8:
            estimate = physical_height(height, state["depth"])
            if estimate > 0.34:
                self.rejected_bearings.append(self.target_bearing)
                self.target_bearing = None
                self.phase = "scan"
                self.scan_turned = 0.0
                return "turn", {"degrees": 60.0}, {}, \
                    note + f" | object measures {estimate:.2f} m tall, too big for the target; rescanning"
        if abs(offset) > 14.0:
            return "turn", {"degrees": round(offset, 1)}, {}, note + " | centring target"
        if height >= NEAR_HEIGHT_PX:
            self.pending = "pickup"
            return "move", {"distance": 0.25}, {}, note + " | close enough, moving in for pickup"
        return "move", {"distance": 0.8}, {}, note + " | approaching"

    def _deliver(self, state: dict, jpeg: bytes | None):
        if self.pending == "drop":
            self.pending = None
            self.drop_tries += 1
            return "drop", {}, {}, "releasing object"
        if self.last_result.get("success") is True and self.phase != "return":
            self.phase = "return"

        frame = analyse_frame(jpeg) if jpeg is not None else None
        zone = None
        if frame is not None:
            zone = next((blob for blob in frame if blob["colour"] == "cyan"), None)
            if zone is not None:
                self.zone_bearing = (state["heading"] + offset_degrees(zone["cx"])) % 360.0

        if zone is not None:
            offset = offset_degrees(zone["cx"])
            if abs(offset) > 14.0:
                return "turn", {"degrees": round(offset, 1)}, {}, "centring drop zone"
            if zone["w"] >= ZONE_WIDE_PX:
                self.pending = "drop"
                return "move", {"distance": 0.3}, {}, "over the zone, dropping next"
            return "move", {"distance": 0.7}, {}, "carrying to zone"

        if self.zone_bearing is not None:
            delta = (self.zone_bearing - state["heading"] + 540.0) % 360.0 - 180.0
            if abs(delta) > 15:
                return "turn", {"degrees": round(delta, 1)}, {}, "turning toward remembered zone"
            return "move", {"distance": 0.8}, {}, "driving toward remembered zone"

        self.scan_turned += 60.0
        return "turn", {"degrees": 60.0}, {}, "carrying; scanning for the drop zone"

    # -- helpers -----------------------------------------------------------------

    def _matches(self, blob: dict, heading: float) -> bool:
        """Colour + blacklist only - shape is advisory (JPEG smears small blobs)."""
        if blob["colour"] == "cyan":
            return False
        if self.target["colour"] and blob["colour"] != self.target["colour"]:
            return False
        bearing = (heading + offset_degrees(blob["cx"])) % 360.0
        for rejected in self.rejected_bearings:
            if abs((bearing - rejected + 540.0) % 360.0 - 180.0) < 10.0:
                return False
        return True

    def _best_match(self, frame: list[dict], heading: float):
        """Once a bearing is locked, chase the blob nearest to it (the cube's
        two faces can split into two blobs - never flip-flop between them).
        With no lock: smallest match for round targets (the sphere, not a cube
        misread as round), largest otherwise."""
        matches = [blob for blob in frame if self._matches(blob, heading)]
        if not matches:
            return None
        if self.target_bearing is not None:
            matches.sort(key=lambda blob: abs(
                ((heading + offset_degrees(blob["cx"])) % 360.0 - self.target_bearing + 540.0) % 360.0 - 180.0))
        elif self.target["shape"] == "round":
            matches.sort(key=lambda blob: blob["area"])
        else:
            matches.sort(key=lambda blob: -blob["area"])
        return matches[0]

    def _remember(self, frame: list[dict], state: dict) -> None:
        for blob in frame:
            bearing = (state["heading"] + offset_degrees(blob["cx"])) % 360.0
            if blob["colour"] == "cyan":
                self.zone_bearing = bearing
            elif self._matches(blob, state["heading"]):
                self.seen_targets = [(b, a) for (b, a) in self.seen_targets
                                     if abs((b - bearing + 540.0) % 360.0 - 180.0) >= 10.0]
                self.seen_targets.append((bearing, blob["area"]))

    def _after_rejection(self, state: dict, note: str):
        """Hand pursuit to the nearest other seen candidate; rescan if none."""
        self.target_bearing = None
        self.pickup_tries = 0
        for bearing, _area in sorted(self.seen_targets, key=lambda item: item[1]):
            if all(abs((bearing - rejected + 540.0) % 360.0 - 180.0) >= 10.0
                   for rejected in self.rejected_bearings):
                self.phase = "approach"
                self.target_bearing = bearing
                delta = (bearing - state["heading"] + 540.0) % 360.0 - 180.0
                return "turn", {"degrees": round(delta, 1)}, {}, note + f" | retargeting bearing {bearing:.0f}"
        self.phase = "scan"
        self.scan_turned = 0.0
        return "turn", {"degrees": 60.0}, {}, note + " | rescanning"

    def _note(self, frame: list[dict] | None) -> str:
        if frame is None:
            return "no new frame"
        if not frame:
            return "SEE: no objects, only walls and floor"
        parts = [
            f"{blob['colour']} {blob['shape']} (w{blob['w']} h{blob['h']}, {'left' if blob['cx'] < 240 else 'right' if blob['cx'] > 400 else 'centre'})"
            for blob in frame[:4]
        ]
        return "SEE: " + ", ".join(parts)

    def _parse_state(self, text: str) -> dict:
        pattern = (r"state=(\w+) x=(-?[\d.]+) y=(-?[\d.]+) heading=(-?[\d.]+) "
                   r"holding=(True|False) retries=(\d+)(?: depth=(null|[\d.]+m))?")
        match = re.search(pattern, text)
        if not match:
            return {"state": "Idle", "heading": 0.0, "holding": False, "retries": 0, "depth": None}
        depth_raw = match.group(7)
        return {
            "state": match.group(1),
            "x": float(match.group(2)),
            "y": float(match.group(3)),
            "heading": float(match.group(4)),
            "holding": match.group(5) == "True",
            "retries": int(match.group(6)),
            "depth": None if depth_raw in (None, "null") else float(depth_raw.rstrip("m")),
        }

    def _depth_from_last(self):
        return self.last_result.get("depth") if isinstance(self.last_result, dict) else None

    def _finish(self, outcome: str, reason: str):
        return "finish", {"outcome": outcome, "reason": reason}, {}, reason
