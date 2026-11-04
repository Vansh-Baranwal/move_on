#!/usr/bin/env python3
"""Membrane robot agent: Claude tool-use loop over the Unity engine HTTP API.

Usage:
    python agent/main.py --command "Bring the round red thing."
    python agent/main.py --dummy            # exercises every endpoint, no API cost

Per run it writes logs/<run_id>/log.jsonl with one line per step (step, timestamp,
tool, args, result, frame_path) and saves each camera frame as a PNG beside the log.
"""

from __future__ import annotations

import argparse
import base64
import io
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path

import requests

sys.path.insert(0, str(Path(__file__).parent))

from engine_client import EngineClient, EngineError  # noqa: E402
from prompts import SYSTEM_PROMPT, TOOLS  # noqa: E402

try:
    import anthropic
except ImportError:  # pragma: no cover - only needed for live runs
    anthropic = None

try:
    from PIL import Image
except ImportError:  # pragma: no cover
    Image = None

MAX_STEPS_HARD = 60
RUN_OUTCOMES = ("pass", "fail_wrong_object", "fail_not_reached", "fail_collision")
MOVEMENT_TOOLS = ("move", "turn")


class RunLogger:
    """JSONL log + PNG frames for one run, per the spec's logging contract."""

    def __init__(self, run_dir: Path):
        self.run_dir = run_dir
        self.frames_dir = run_dir / "frames"
        self.frames_dir.mkdir(parents=True)
        self.log_path = run_dir / "log.jsonl"
        self.step = 0

    def save_frame(self, jpeg_bytes: bytes) -> str:
        if Image is None:
            raise SystemExit("Pillow is required: pip install -r requirements.txt")
        self.step += 1
        path = self.frames_dir / f"step_{self.step:04d}.png"
        Image.open(io.BytesIO(jpeg_bytes)).save(path)  # frames saved as PNG beside the log
        return str(path.relative_to(self.run_dir))

    def write(self, record: dict) -> None:
        with self.log_path.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")

    def event(self, tool: str, args: dict, result, frame_path: str | None = None, usage: dict | None = None):
        record = {
            "step": self.step,
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "tool": tool,
            "args": args,
            "result": result,
            "frame_path": frame_path,
        }
        if usage:
            record["usage"] = usage
        self.write(record)


def run_claude(engine: EngineClient, logger: RunLogger, command: str, model: str, max_steps: int):
    client = anthropic.Anthropic()
    messages: list[dict] = []
    outcome, reason = "fail_not_reached", "step budget exhausted"
    totals = {"input": 0, "output": 0, "cache_read": 0, "cache_write": 0}
    attach_image = True  # first decision always carries a frame

    for _ in range(max_steps):
        state = engine.state()
        try:
            depth = engine.depth()
        except EngineError:
            depth = None
        state_line = (
            f"state={state['state']} x={state['x']:.2f} y={state['y']:.2f} "
            f"heading={state['heading']:.1f} holding={state['holding']} retries={state['retries']}"
        )
        state_line += " depth=null" if depth is None else f" depth={depth:.2f}m"

        if attach_image:
            jpeg = engine.look()
            frame_path = logger.save_frame(jpeg)
            content = [
                {
                    "type": "image",
                    "source": {"type": "base64", "media_type": "image/jpeg", "data": base64.b64encode(jpeg).decode()},
                },
                {"type": "text", "text": state_line},
            ]
        else:
            frame_path = None
            content = [{"type": "text", "text": state_line + " (no new image this step; move or turn to refresh the view)"}]

        messages.append({"role": "user", "content": content})
        response = client.messages.create(
            model=model,
            max_tokens=1024,
            system=[{"type": "text", "text": SYSTEM_PROMPT, "cache_control": {"type": "ephemeral"}}],
            tools=TOOLS,
            messages=prune_old_images(messages),
        )

        usage = response.usage
        step_usage = {
            "input": getattr(usage, "input_tokens", 0),
            "output": getattr(usage, "output_tokens", 0),
            "cache_read": getattr(usage, "cache_read_input_tokens", 0) or 0,
            "cache_write": getattr(usage, "cache_creation_input_tokens", 0) or 0,
        }
        for key in totals:
            totals[key] += step_usage[key]

        tool_uses = [block for block in response.content if block.type == "tool_use"]
        messages.append({"role": "assistant", "content": response.content})

        if not tool_uses:
            text = " ".join(
                getattr(block, "text", "") for block in response.content if block.type == "text"
            ).strip()
            logger.event("none", {}, {"error": "no tool call", "said": text[:200]})
            messages.append({"role": "user", "content": [{"type": "text", "text": "Reply with exactly one tool call."}]})
            continue

        call = tool_uses[0]
        result, finished, moved = execute_tool(engine, call)
        logger.event(call.name, dict(call.input or {}), result, frame_path=frame_path, usage=step_usage)
        messages.append({
            "role": "user",
            "content": [{
                "type": "tool_result",
                "tool_use_id": call.id,
                "content": [{"type": "text", "text": json.dumps(result)}],
            }],
        })

        if finished:
            outcome = result.get("outcome", "fail_not_reached")
            reason = result.get("reason", "")
            break
        # Fresh image next step only after an actual movement, per the spec's control loop.
        attach_image = moved

    return outcome, reason, totals


def execute_tool(engine: EngineClient, call):
    """Returns (result_dict, finished, moved)."""
    name = call.name
    args = call.input or {}
    try:
        if name == "move":
            return engine.move(float(args["distance"])), False, True
        if name == "turn":
            return engine.turn(float(args["degrees"])), False, True
        if name == "depth":
            return {"depth": engine.depth()}, False, False
        if name == "pickup":
            return engine.pickup(), False, False
        if name == "drop":
            return engine.drop(), False, False
        if name == "finish":
            outcome = args.get("outcome", "fail_not_reached")
            if outcome not in RUN_OUTCOMES:
                outcome = "fail_not_reached"
            return {"outcome": outcome, "reason": args.get("reason", "")}, True, False
        return {"error": f"unknown tool: {name}"}, False, False
    except (EngineError, KeyError, ValueError) as error:
        return {"error": str(error)}, False, False


def prune_old_images(messages: list[dict]) -> list[dict]:
    """Keep only the latest camera image in the request; earlier ones become one-line notes."""
    last_image = -1
    for index, message in enumerate(messages):
        if message["role"] == "user" and any(
            isinstance(block, dict) and block.get("type") == "image" for block in message["content"]
        ):
            last_image = index

    pruned = []
    for index, message in enumerate(messages):
        if index == last_image or message["role"] != "user":
            pruned.append(message)
            continue
        content = [
            {"type": "text", "text": "[camera frame from an earlier step omitted]"}
            if isinstance(block, dict) and block.get("type") == "image"
            else block
            for block in message["content"]
        ]
        pruned.append({**message, "content": content})
    return pruned


def run_dummy(engine: EngineClient, logger: RunLogger):
    """Scripted plumbing test that touches every endpoint. Zero API cost."""
    script = [
        ("turn", {"degrees": 45}),
        ("move", {"distance": 2.0}),
        ("depth", {}),
        ("move", {"distance": 20.0}),   # expected to hit the wall and back off
        ("turn", {"degrees": 90}),
        ("move", {"distance": 1.0}),
        ("pickup", {}),
        ("drop", {}),
        ("finish", {"outcome": "fail_not_reached", "reason": "dummy run for HTTP plumbing"}),
    ]
    for name, args in script:
        frame_path = logger.save_frame(engine.look())
        if name == "depth":
            result = {"depth": engine.depth()}
        elif name == "finish":
            result = dict(args)
        elif name == "move":
            result = engine.move(args["distance"])
        elif name == "turn":
            result = engine.turn(args["degrees"])
        else:
            result = getattr(engine, name)()
        logger.event(name, args, result, frame_path=frame_path)
        print(f"  {name} {args} -> {result}")
    return "fail_not_reached", "dummy run", {}


def main() -> int:
    parser = argparse.ArgumentParser(description="Membrane robot fetch agent")
    parser.add_argument("--command", help='Plain-language command, e.g. "Bring the round red thing."')
    parser.add_argument("--model", default=os.environ.get("MEMBRANE_MODEL", "claude-sonnet-5-5"))
    parser.add_argument("--max-steps", type=int, default=25, help="Soft target 25; hard stop 60.")
    parser.add_argument("--base-url", default="http://localhost:8080")
    parser.add_argument("--dummy", action="store_true", help="Scripted plumbing test, no API calls.")
    parser.add_argument("--no-record", action="store_true", help="Skip POV recording (logging only).")
    parser.add_argument("--out", default=None, help="Run directory override (default logs/<timestamp>).")
    args = parser.parse_args()

    if not args.dummy and not args.command:
        parser.error("--command is required unless --dummy is used")
    max_steps = min(max(1, args.max_steps), MAX_STEPS_HARD)

    engine = EngineClient(args.base_url)
    engine.wait_ready()

    run_dir = Path(args.out) if args.out else Path("logs") / datetime.now().strftime("run_%Y%m%d-%H%M%S")
    run_dir.mkdir(parents=True, exist_ok=True)
    logger = RunLogger(run_dir)
    print(f"Run directory: {run_dir}")

    command = args.command or "(dummy run)"
    engine.reset()
    if not args.no_record:
        pov_dir = str((run_dir / "pov").resolve())
        engine.record_start(command, frames_dir=pov_dir)

    try:
        if args.dummy:
            outcome, reason, totals = run_dummy(engine, logger)
        else:
            outcome, reason, totals = run_claude(engine, logger, command, args.model, max_steps)
    finally:
        if not args.no_record:
            try:
                engine.record_stop()
            except EngineError as error:
                print(f"record_stop failed: {error}")

    steps = logger.step
    (run_dir / "meta.json").write_text(json.dumps({
        "command": command,
        "outcome": outcome,
        "reason": reason,
        "steps": steps,
        "model": None if args.dummy else args.model,
        "finished_utc": datetime.now(timezone.utc).isoformat(),
    }, indent=2) + "\n", encoding="utf-8")

    logger.write({"event": "run_summary", "outcome": outcome, "reason": reason, "steps": steps,
                  "usage_totals": totals})
    print(f"Outcome: {outcome} ({reason}) after {steps} steps")
    if totals:
        print(f"Tokens: input {totals['input']} (cached read {totals['cache_read']}, "
              f"write {totals['cache_write']}), output {totals['output']}")
    return 0 if outcome == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
